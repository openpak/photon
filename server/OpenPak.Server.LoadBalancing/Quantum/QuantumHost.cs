using System.Diagnostics;
using OpenPak.Photon;
using OpenPak.Server.Core;

namespace OpenPak.Server.LoadBalancing.Quantum;

/// <summary>
/// What Photon Cloud's Quantum plugin does for a Quantum 2 room: the deterministic session's server half.
/// Quantum is lockstep: every client simulates the whole game, so the server never simulates. It hands out
/// player slots, collects the session and runtime configs, starts the simulation, keeps the tick clock and
/// turns every player's inputs into one confirmed input stream (event 102) that all clients play back.
/// Player data and commands (RPCs) arrive as protocol messages and are folded into that player's next
/// confirmed input. Wire formats: <see cref="QuantumProtocol"/>.
/// </summary>
public sealed class QuantumHost
{
    /// <summary>(room, actor, event code, payload, reliable).</summary>
    private readonly Action<string, int, byte, byte[], bool> _send;
    private readonly Dictionary<string, QuantumRoom> _rooms = new();
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public QuantumHost(Action<string, int, byte, byte[], bool> send) => _send = send;

    public double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Ticks a confirmed tick may wait for a late player's input before the server fills it in.</summary>
    public int HardTolerance { get; set; } = 8;

    public QuantumRoom Room(string room)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(room, out var r)) _rooms[room] = r = new QuantumRoom(this, room);
            return r;
        }
    }

    public bool Has(string room) { lock (_gate) return _rooms.ContainsKey(room); }

    /// <summary>A Quantum event (100/101 protocol, 102 input) from <paramref name="actor"/>.</summary>
    public void Feed(string room, int actor, byte eventCode, byte[] payload)
    {
        var r = Room(room);
        lock (r)
        {
            try
            {
                if (eventCode == QuantumProtocol.InputEvent) r.OnInputs(actor, payload);
                else r.OnProtocol(actor, payload);
            }
            catch (EndOfStreamException ex)
            {
                Log.Warn("Quantum", $"{Redact.Room(room)} actor {actor} ev {eventCode}: {ex.Message} ({payload.Length}B {PhotonNames.Hex(payload, 24)})");
            }
        }
    }

    public void PlayerLeft(string room, int actor)
    {
        QuantumRoom? r;
        lock (_gate) _rooms.TryGetValue(room, out r);
        if (r is null) return;
        lock (r)
        {
            r.Leave(actor);
            if (r.Empty) lock (_gate) _rooms.Remove(room);
        }
    }

    public void Tick()
    {
        QuantumRoom[] rooms;
        lock (_gate) rooms = _rooms.Values.ToArray();
        foreach (var r in rooms) lock (r) r.Tick(Now);
    }

    internal void Send(string room, int actor, byte code, byte[] payload, bool reliable) => _send(room, actor, code, payload, reliable);
}

public sealed class QuantumRoom
{
    private readonly QuantumHost _host;
    private readonly string _room;
    private readonly Dictionary<int, int[]> _slotsByActor = new();
    private readonly Dictionary<int, int> _actorBySlot = new();
    private readonly HashSet<int> _startedActors = new();
    private readonly SortedDictionary<int, Dictionary<int, TickInput>> _pending = new();
    private readonly Dictionary<int, Queue<(byte[] Data, bool Command)>> _rpcs = new();
    private readonly Dictionary<int, TickInput> _last = new();
    private readonly LinkedList<byte[]> _recentBlocks = new();
    private byte[]? _sessionConfig;     // DeterministicSessionConfig bits, as the first client sent them
    private int _sessionConfigBits;
    private byte[]? _runtimeConfig;
    private bool _started;
    private double _startTime;
    private int _initialTick;
    private int _confirmedTick;         // last tick broadcast
    private int _playerCount;
    private int _updateFps = 60;
    private bool _fixedSizeEnabled;
    private int _fixedSize;

    internal QuantumRoom(QuantumHost host, string room) { _host = host; _room = room; }

    public bool Empty => _slotsByActor.Count == 0;
    public bool Started => _started;
    public int ConfirmedTick => _confirmedTick;

    private sealed record TickInput(byte[]? Data, byte[]? Rpc, int Flags);

    // --- protocol (events 100/101) ---

    internal void OnProtocol(int actor, byte[] payload)
    {
        var r = new QuantumBitReader(payload);
        while (r.CanRead(8))
        {
            var id = r.ReadByte();
            switch (id)
            {
                case QuantumProtocol.Join:
                {
                    var joinId = r.ReadString();
                    var version = r.ReadString();
                    var slotsWanted = r.ReadInt();
                    var initialTick = r.ReadInt();
                    var playerCount = r.ReadInt();
                    if (r.CanRead(1)) r.ReadBool();     // Rejoin (newer protocols)
                    HandleJoin(actor, version, Math.Max(1, slotsWanted), initialTick, playerCount);
                    break;
                }
                case QuantumProtocol.SessionConfig:
                {
                    var requested = r.ReadBool();
                    var start = r.BitPosition;
                    var isNull = r.ReadBool();
                    if (!isNull) r.BitPosition = start + QuantumProtocol.SessionConfigBits;
                    if (!requested && !isNull && _sessionConfig is null) StoreSessionConfig(payload, start, r.BitPosition - start);
                    break;
                }
                case QuantumProtocol.RuntimeConfig:
                {
                    var requested = r.ReadBool();
                    var cfg = r.ReadByteArray();
                    if (!requested && cfg is not null) _runtimeConfig ??= cfg;
                    break;
                }
                case QuantumProtocol.StartRequest:
                    HandleStartRequest(actor);
                    break;
                case QuantumProtocol.SetPlayerData:
                case QuantumProtocol.Command:
                {
                    var index = r.ReadInt();
                    var data = r.ReadByteArray();
                    var slot = SlotOf(actor, index);
                    if (slot >= 0 && data is not null)
                    {
                        if (!_rpcs.TryGetValue(slot, out var q)) _rpcs[slot] = q = new();
                        q.Enqueue((data, id == QuantumProtocol.Command));
                    }
                    break;
                }
                case QuantumProtocol.RttUpdate: r.ReadInt(); break;
                case QuantumProtocol.TickChecksum: r.ReadInt(); r.ReadULong(); break;
                case QuantumProtocol.InputMissing:
                {
                    r.ReadInt();
                    // TickMissing[]: presence bit, u16 count, {int tick, ulong mask (PlayerCount bits)}.
                    if (r.ReadBool())
                    {
                        var n = (int)r.ReadBits(16);
                        for (var i = 0; i < n; i++) { r.ReadInt(); r.ReadBits(Math.Min(64, Math.Max(1, _playerCount))); }
                    }
                    ResendRecent(actor);
                    break;
                }
                case QuantumProtocol.Disconnect: r.ReadString(); break;
                default:
                    Log.Info("Quantum", $"{Redact.Room(_room)} actor {actor} sent protocol message {id}; not handled, rest of the packet dropped");
                    return;
            }
        }
    }

    private void HandleJoin(int actor, string? version, int slotsWanted, int initialTick, int playerCount)
    {
        if (playerCount > 0 && _playerCount == 0) _playerCount = playerCount;
        if (!_slotsByActor.TryGetValue(actor, out var slots))
        {
            var list = new List<int>();
            for (var s = 0; list.Count < slotsWanted && s < Math.Max(_playerCount, 64); s++)
                if (!_actorBySlot.ContainsKey(s)) list.Add(s);
            slots = list.ToArray();
            _slotsByActor[actor] = slots;
            foreach (var s in slots) _actorBySlot[s] = actor;
        }
        if (_initialTick == 0) _initialTick = initialTick;
        Log.Info("Quantum", $"{Redact.Room(_room)} actor {actor} Join v{version} slots=[{string.Join(",", slots)}] players={_playerCount}");
        var w = new QuantumBitWriter();
        w.WriteByte(QuantumProtocol.Joined);
        w.WriteBool(true);                                   // Confirmed
        w.WriteInt(QuantumProtocol.JoinConfirmed);           // Status
        w.WriteIntArray(slots);                              // PlayerSlots
        if (_sessionConfig is null) { w.WriteByte(QuantumProtocol.SessionConfig); w.WriteBool(true); w.WriteBool(true); }
        if (_runtimeConfig is null) { w.WriteByte(QuantumProtocol.RuntimeConfig); w.WriteBool(true); w.WriteByteArray(null); }
        _host.Send(_room, actor, QuantumProtocol.ProtocolEvent, w.ToArray(), true);
    }

    private void StoreSessionConfig(byte[] payload, int start, int bits)
    {
        var w = new QuantumBitWriter();
        var src = new QuantumBitReader(payload);
        w.WriteBitsFrom(src, start, bits);
        _sessionConfig = w.ToArray();
        _sessionConfigBits = bits;
        // Read the fields the server itself needs (order: see QuantumProtocol.SessionConfigBits).
        var r = new QuantumBitReader(_sessionConfig);
        r.ReadBool();                       // null bit
        r.ReadInt();                        // BackgroundThreadPriority
        var players = r.ReadInt();
        for (var i = 0; i < 5; i++) r.ReadBool();
        r.ReadInt(); r.ReadInt(); r.ReadInt();  // InputDelayMin/Max/PingStart
        var fps = r.ReadInt();
        for (var i = 0; i < 13; i++) r.ReadInt();
        r.ReadBool();                       // ChecksumCrossPlatformDeterminism
        _fixedSizeEnabled = r.ReadBool();
        _fixedSize = r.ReadInt();
        if (players > 0) _playerCount = players;
        if (fps is > 0 and <= 240) _updateFps = fps;
        Log.Info("Quantum", $"{Redact.Room(_room)} session config: players={_playerCount} fps={_updateFps} fixedInput={(_fixedSizeEnabled ? _fixedSize : 0)}");
    }

    private void HandleStartRequest(int actor)
    {
        if (_sessionConfig is null || _runtimeConfig is null)
        {
            Log.Warn("Quantum", $"{Redact.Room(_room)} actor {actor} StartRequest before the configs arrived; asking again");
            var q = new QuantumBitWriter();
            if (_sessionConfig is null) { q.WriteByte(QuantumProtocol.SessionConfig); q.WriteBool(true); q.WriteBool(true); }
            if (_runtimeConfig is null) { q.WriteByte(QuantumProtocol.RuntimeConfig); q.WriteBool(true); q.WriteByteArray(null); }
            _host.Send(_room, actor, QuantumProtocol.ProtocolEvent, q.ToArray(), true);
            _deferredStarts.Add(actor);
            return;
        }
        if (!_started)
        {
            _started = true;
            _startTime = _host.Now;
            _confirmedTick = _initialTick;
            Log.Info("Quantum", $"{Redact.Room(_room)} simulation started by actor {actor} at tick {_initialTick}");
        }
        if (!_startedActors.Add(actor)) return;
        var w = new QuantumBitWriter();
        w.WriteByte(QuantumProtocol.SimulationStart);
        w.WriteBool(false);                     // Reconnect
        w.WriteDouble(_host.Now);               // ServerTime
        w.WriteByteArray(_runtimeConfig);
        w.WriteBitsFrom(new QuantumBitReader(_sessionConfig), 0, _sessionConfigBits);
        _host.Send(_room, actor, QuantumProtocol.ProtocolEvent, w.ToArray(), true);
    }

    private readonly HashSet<int> _deferredStarts = new();

    // --- inputs (event 102) ---

    /// <summary>A client's input packet: back-to-back records of u8 player index, u32 tick, data
    /// (presence bit + u16 length + bytes), one rpc bit (always 0 from the client), 4-bit flags.</summary>
    internal void OnInputs(int actor, byte[] payload)
    {
        var r = new QuantumBitReader(payload);
        while (r.BitsLeft >= 8 + 32 + 1)
        {
            var index = r.ReadByte();
            var tick = r.ReadInt();
            byte[]? data = null;
            if (r.ReadBool()) data = r.ReadBytes((int)r.ReadBits(16));
            if (r.ReadBool()) { r.ReadBytes((int)r.ReadBits(16)); }   // an rpc here is a client bug; skip it
            var flags = (int)r.ReadBits(4);
            var slot = SlotOf(actor, index);
            if (slot < 0 || tick <= _confirmedTick) continue;
            if (!_pending.TryGetValue(tick, out var perSlot)) _pending[tick] = perSlot = new();
            perSlot.TryAdd(slot, new TickInput(data, null, flags));
            if (r.BitsLeft < 8) break;
        }
    }

    private int SlotOf(int actor, int index)
    {
        if (!_slotsByActor.TryGetValue(actor, out var slots)) return -1;
        // The client names its player by local index or by its global slot; accept either.
        if (Array.IndexOf(slots, index) >= 0) return index;
        return index >= 0 && index < slots.Length ? slots[index] : -1;
    }

    internal void Leave(int actor)
    {
        if (_slotsByActor.Remove(actor, out var slots))
            foreach (var s in slots) { _actorBySlot.Remove(s); _last.Remove(s); }
        _startedActors.Remove(actor);
    }

    internal void Tick(double now)
    {
        foreach (var a in _deferredStarts.ToArray())
            if (_sessionConfig is not null && _runtimeConfig is not null) { _deferredStarts.Remove(a); HandleStartRequest(a); }
        if (!_started || _startedActors.Count == 0) return;
        var serverTick = _initialTick + (int)((now - _startTime) * _updateFps);
        var blocks = new List<byte[]>();
        while (_confirmedTick < serverTick)
        {
            var t = _confirmedTick + 1;
            _pending.TryGetValue(t, out var have);
            var allIn = _actorBySlot.Keys.All(s => have is not null && have.ContainsKey(s));
            if (!allIn && t > serverTick - _host.HardTolerance) break;
            blocks.Add(EncodeTick(t, have));
            _pending.Remove(t);
            _confirmedTick = t;
        }
        if (blocks.Count == 0) return;
        foreach (var b in blocks) { _recentBlocks.AddLast(b); if (_recentBlocks.Count > 8) _recentBlocks.RemoveFirst(); }
        Broadcast(_recentBlocks.ToList());
    }

    private void ResendRecent(int actor)
    {
        if (_recentBlocks.Count == 0) return;
        _host.Send(_room, actor, QuantumProtocol.InputEvent, Packet(_recentBlocks.ToList()), false);
    }

    private void Broadcast(List<byte[]> blocks)
    {
        var packet = Packet(blocks);
        foreach (var actor in _startedActors) _host.Send(_room, actor, QuantumProtocol.InputEvent, packet, false);
    }

    private byte[] Packet(List<byte[]> blocks)
    {
        var w = new QuantumBitWriter();
        // DeterministicTickInputEncodeHeader.
        w.WriteBits(100, 32);                       // MaxPing
        w.WriteDouble(_host.Now);                   // ServerTime
        w.WriteDouble(1.0);                         // ServerTimeScale
        w.WriteByte((byte)_playerCount);
        w.WriteBool(_fixedSizeEnabled);
        if (_fixedSizeEnabled) w.WriteBits((ulong)_fixedSize, 10);
        foreach (var b in blocks)
        {
            w.AlignToByte();
            w.WriteBytes(b);
        }
        return w.ToArray();
    }

    /// <summary>One confirmed tick: u32 tick, 1 bit complete, the received mask, then each player's input.</summary>
    private byte[] EncodeTick(int tick, Dictionary<int, TickInput>? have)
    {
        var w = new QuantumBitWriter();
        w.WriteInt(tick);
        w.WriteBool(true);                          // complete: every slot is in the mask
        var n = Math.Max(1, _playerCount);
        ulong mask = n >= 64 ? ulong.MaxValue : (1UL << n) - 1;
        if (n > 32) { w.WriteBits(mask & 0xFFFFFFFF, 32); w.WriteBits(mask >> 32, 32); }
        else w.WriteBits(mask, n);
        for (var slot = 0; slot < n; slot++)
        {
            TickInput input;
            if (have is not null && have.TryGetValue(slot, out var got)) input = got;
            else if (_actorBySlot.ContainsKey(slot) && _last.TryGetValue(slot, out var prev))
                input = prev with { Rpc = null, Flags = prev.Flags | QuantumProtocol.FlagReplacedByServer };
            else if (_actorBySlot.ContainsKey(slot)) input = new TickInput(null, null, QuantumProtocol.FlagReplacedByServer);
            else input = new TickInput(null, null, QuantumProtocol.FlagRepeatable | QuantumProtocol.FlagPlayerNotPresent);

            if (_rpcs.TryGetValue(slot, out var q) && q.Count > 0)
            {
                var (rpc, command) = q.Dequeue();
                input = input with { Rpc = rpc, Flags = command ? input.Flags | QuantumProtocol.FlagCommand : input.Flags & ~QuantumProtocol.FlagCommand };
            }
            if (have is not null && have.ContainsKey(slot)) _last[slot] = input with { Rpc = null, Flags = input.Flags & ~QuantumProtocol.FlagCommand };

            // SerializeForDecoder.
            var empty = input.Flags == (QuantumProtocol.FlagRepeatable | QuantumProtocol.FlagPlayerNotPresent) && input.Data is null && input.Rpc is null;
            w.WriteBool(empty);
            if (empty) continue;
            if (_fixedSizeEnabled && _fixedSize >= 1)
            {
                w.WriteBool(true);
                var fixedData = new byte[_fixedSize];
                input.Data?.AsSpan(0, Math.Min(_fixedSize, input.Data.Length)).CopyTo(fixedData);
                w.WriteBytes(fixedData);
            }
            else w.WriteByteArray(input.Data ?? Array.Empty<byte>());
            w.WriteByteArray(input.Rpc);
            w.WriteBits((ulong)(input.Flags & 0xF), 4);
        }
        return w.ToArray();
    }
}
