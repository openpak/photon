using OpenPak.Server.Core;

namespace OpenPak.Server.LoadBalancing.Fusion;

/// <summary>Per-deployment switches for the Shared-mode simulation.</summary>
public sealed record FusionRoomOptions
{
    /// <summary>Room capacity written to RuntimeConfig.PlayerMaxCount and the transport's connection limit.</summary>
    public int MaxPlayers { get; init; } = 8;

    /// <summary>Create SceneInfo/PhysicsInfo with StateAuthority = MasterClient instead of None. The stock
    /// client asserts when the master consumes an initial scene under None, but a past build that combined
    /// this with an early master election stopped Outbound's join sync (spec §11.5, §19 item 3), so it
    /// stays off until tested on its own.</summary>
    public bool SceneAuthorityMasterClient { get; init; }
}

/// <summary>
/// One room's Fusion 2 Shared-mode simulation: the state-holding relay of
/// docs/protocol/08-fusion-shared-simulation.md on top of <see cref="FusionRelayPeer"/>. Stores every
/// object's words, owns the global structs, replicates per connection with delivered/lost feedback,
/// relays simulation messages, implements the internal messages and authority rules, and cleans up
/// after leavers. Single-threaded: every member runs on the caller's thread, and time advances only
/// through <see cref="Tick"/>.
/// </summary>
public sealed partial class FusionSharedRoom
{
    // Tick rates (spec §7.1): the SDK's TickRate.Default selection, which Outbound has run live.
    public const int TickRate = 64, ClientSendRate = 32, ServerTickRate = 64, ServerSendRate = 32;
    private const int SendIntervalTicks = TickRate / ServerSendRate;
    public const uint RuntimeConfigId = 1, SceneInfoId = 3, PhysicsInfoId = 4, FirstServerId = 1024;
    /// <summary>Ids below this are the server's (structs); a client's are <c>counter &lt;&lt; 19 | n</c> with counter ≥ 1.</summary>
    private const uint FirstClientId = 1u << 19;
    private const string Cat = "FusionRoom";
    private const double DepartureGrace = 1;

    private readonly FusionRoomOptions _options;
    private readonly FusionRelayPeer _peer;
    private readonly SortedSet<int> _registered = new();
    private readonly Dictionary<int, FusionSimPlayer> _players = new();
    /// <summary>Players whose client closed its relay connection and whose Photon leave is expected next.</summary>
    private readonly Dictionary<int, (FusionSimPlayer Player, double Deadline)> _departing = new();
    private bool _kicking;
    private readonly Dictionary<uint, FusionObject> _objects = new();
    /// <summary>Client objects in creation order: the rotation order of §8.3.</summary>
    private readonly List<FusionObject> _worldObjects = new();
    private readonly List<FusionObject> _playerData = new();
    private readonly FusionObject _runtimeConfig, _sceneInfo, _physicsInfo;
    private uint _nextServerId = FirstServerId;
    private bool _masterElectable;

    // Clock (spec §10). Time is seconds since the room started; the stamp is the send-round number.
    private readonly double _start;
    private double _now;
    /// <summary>Arrival time of the datagram being processed (time feedback, §10.3).</summary>
    private double _arrival;
    private int _tick;
    private int _lastRoundTick = -SendIntervalTicks;
    private int _round;
    private readonly double[] _tickTimes = new double[TickRate];
    private double _lastLog;
    private long _received, _sent, _relayed, _malformed;

    /// <param name="now">The caller's clock (seconds, any monotonic origin); the room's time starts here.</param>
    public FusionSharedRoom(string name, Action<int, byte[], bool> send, double now, FusionRoomOptions? options = null)
    {
        Name = name;
        _start = now;
        _options = options ?? new FusionRoomOptions();
        _peer = new FusionRelayPeer(_options.MaxPlayers, (actor, datagram, reliable) => { _sent++; send(actor, datagram, reliable); });
        _peer.Connected += OnConnected;
        _peer.Disconnected += (connection, reason) => OnDisconnected(connection, reason);
        _peer.NotifyReceived += OnNotifyReceived;
        _peer.NotifyDelivered += (connection, token) => OnPacketResolved(connection, token, delivered: true);
        _peer.NotifyLost += (connection, token) => OnPacketResolved(connection, token, delivered: false);
        // Reliable data for the server has no consumer in a relay; player-to-player data is proxied by the peer (§13).
        _peer.ReliableReceived += (connection, id, _) => Log.Debug(Cat, $"\"{Name}\" reliable data for the server from actor {connection.Actor} key ignored ({id.TotalLength} bytes)");

        var sceneAuthority = _options.SceneAuthorityMasterClient ? FusionObject.PlayerMasterClient : FusionObject.PlayerNone;
        _runtimeConfig = CreateStruct(RuntimeConfigId, 9, 0, 0, FusionObject.PlayerNone);
        _sceneInfo = CreateStruct(SceneInfoId, 13, 0, 0, sceneAuthority);
        _physicsInfo = CreateStruct(PhysicsInfoId, 10, 0, 0, sceneAuthority);
        int[] config = [TickRate, ClientSendRate, ServerTickRate, ServerSendRate, 1 /* SimulationModes.Server */,
                        _options.MaxPlayers, FusionObject.PlayerNone, FusionObject.PlayerNone, 2 /* Topologies.Shared */];
        for (var i = 0; i < config.Length; i++) _runtimeConfig.Set(FusionObject.HeaderWords + i, config[i], NextStamp);
    }

    public string Name { get; }

    /// <summary>No player is registered: the room can be dropped.</summary>
    public bool IsEmpty => _registered.Count == 0;

    public int PlayerCount => _players.Count;

    /// <summary>The stamp changes get until the next send round (spec §8.1: always above every sent stamp).</summary>
    private int NextStamp => _round + 1;

    /// <summary>The Photon room's master as this server advertises it: the lowest actor (spec §11.5).</summary>
    private int MasterActor => _registered.Count == 0 ? -1 : _registered.Min;

    /// <summary>Raw PlayerRef of whoever holds authority, resolving MasterClient (-1) to the master.</summary>
    private int ResolveAuthority(int raw) => raw == FusionObject.PlayerMasterClient
        ? (_registered.Count == 0 ? FusionObject.PlayerNone : MasterActor + 1)
        : raw;

    /// <summary>A peer's Fusion Join was answered: let its relay Connect in (spec §11.1).</summary>
    public void Register(int actor, byte[] uniqueId)
    {
        _peer.Register(actor, uniqueId, playerId: actor);
        if (!_registered.Add(actor)) return;
        ElectMaster();
    }

    /// <summary>The peer left the Photon room: close its connection and clean up after it (spec §11.4).</summary>
    public void Unregister(int actor)
    {
        _registered.Remove(actor);
        _peer.Unregister(actor);   // raises Disconnected when it was connected
        if (_departing.Remove(actor, out var departed)) CleanUpAfter(departed.Player, NetDisconnectReason.Requested);
        ElectMaster();
    }

    /// <summary>One event-101 datagram from <paramref name="actor"/> that arrived at <paramref name="now"/>
    /// (caller's clock). Never throws.</summary>
    public void Receive(int actor, byte[] datagram, double now)
    {
        _received++;
        _arrival = Math.Max(now - _start, 0);
        if (_players.TryGetValue(actor, out var player)) player.LastReceived = _arrival;
        try { _peer.Receive(actor, datagram); }
        catch (Exception ex) { Log.Exception(Cat, $"\"{Name}\" datagram from actor {actor} failed", ex); }
    }

    /// <summary>
    /// Advances the room to <paramref name="now"/> (caller's clock): the server tick follows wall-clock time
    /// and never waits for anyone (spec §10.1, §10.4). Runs the transport's timers, and every
    /// <c>TickRate / ServerSendRate</c> ticks a send round: one packet per connection, even an empty one.
    /// </summary>
    public void Tick(double now)
    {
        _now = Math.Max(now - _start, _now);
        var tick = (int)(_now * TickRate);
        // Remember when each recent tick was reached, for the expected arrival time of §10.3.
        for (var reached = Math.Max(_tick + 1, tick - TickRate + 1); reached <= tick; reached++)
            _tickTimes[reached % TickRate] = _now;
        _tick = tick;

        if (_tick - _lastRoundTick >= SendIntervalTicks)
        {
            _lastRoundTick = _tick;
            _round++;
            foreach (var player in _players.Values.ToArray())
            {
                try { SendPacket(player); }
                catch (Exception ex) { Log.Exception(Cat, $"\"{Name}\" packet to actor {player.Actor} failed", ex); }
            }
        }
        _peer.Update(_now);
        foreach (var (actor, departed) in _departing.ToArray())
        {
            if (_now < departed.Deadline) continue;
            _departing.Remove(actor);
            CleanUpAfter(departed.Player, NetDisconnectReason.Requested);
        }

        if (_now - _lastLog >= 1 && Log.Enabled(LogLevel.Debug))
        {
            _lastLog = _now;
            Log.Debug(Cat, $"\"{Name}\" tick={_tick} players={_players.Count}/{_registered.Count} objects={_worldObjects.Count} " +
                           $"master={_runtimeConfig.Words[FusionObject.HeaderWords + 6]} received={_received} sent={_sent} relayed={_relayed} malformed={_malformed}");
        }
    }

    /// <summary>The server time at which tick <paramref name="tick"/> was reached, or its nominal time.</summary>
    private double TickTime(int tick) =>
        tick <= _tick && tick > _tick - TickRate && tick >= 0 ? _tickTimes[tick % TickRate] : (double)tick / TickRate;

    /// <summary>
    /// RuntimeConfig.MasterClient = the lowest actor (spec §11.5), written on every join and leave except
    /// the room's very first join: the DLL host's election ran before its RuntimeConfig existed, so a solo
    /// host saw None until a second player arrived, and Outbound's join sync was live-tested that way.
    /// </summary>
    private void ElectMaster()
    {
        if (!_masterElectable)
        {
            _masterElectable = true;
            return;
        }
        var master = _registered.Count == 0 ? FusionObject.PlayerNone : MasterActor + 1;
        var word = FusionObject.HeaderWords + 6;
        if (_runtimeConfig.Words[word] == master) return;
        _runtimeConfig.Set(word, master, NextStamp);
        Log.Debug(Cat, $"\"{Name}\" master client -> player {master}");
    }

    private FusionObject CreateStruct(uint id, int bodyWords, int type, int typeKind, int stateAuthority)
    {
        var obj = new FusionObject(id, FusionObject.HeaderWords + bodyWords);
        var stamp = NextStamp;
        obj.Set(FusionObject.WordCountWord, obj.WordCount, stamp);   // BehaviourCount 0
        obj.Set(FusionObject.TypeWord, type, stamp);
        obj.Set(FusionObject.TypeWord + 1, typeKind, stamp);
        obj.Set(FusionObject.FlagsWord, FusionObject.FlagStruct, stamp);
        obj.Set(FusionObject.StateAuthorityWord, stateAuthority, stamp);
        _objects.Add(id, obj);
        foreach (var player in _players.Values) player.Objects[id] = new ObjectSendState(ObjectSendStatus.CreatedUnconfirmed);
        return obj;
    }

    /// <summary>A relay connection was accepted (spec §11.2): PlayerData, then the join sweep.</summary>
    private void OnConnected(FusionRelayConnection connection)
    {
        if (_departing.Remove(connection.Actor, out var departed)) CleanUpAfter(departed.Player, NetDisconnectReason.Requested);
        // The server's id range is 1024..0x7FFFF; a room never lives long enough to run out.
        var playerData = CreateStruct(_nextServerId++, 3, 1 /* PlayerData */, 2 /* InternalStruct */, FusionObject.PlayerNone);
        var player = new FusionSimPlayer(connection, playerData.Id) { LastReceived = _now };
        playerData.Set(FusionObject.HeaderWords, player.PlayerRaw, NextStamp);
        playerData.Set(FusionObject.HeaderWords + 2, connection.Actor, NextStamp);
        _playerData.Add(playerData);

        foreach (var obj in _objects.Values) player.Objects[obj.Id] = new ObjectSendState(ObjectSendStatus.CreatedUnconfirmed);
        _players[connection.Actor] = player;
        Log.Debug(Cat, $"\"{Name}\" actor {connection.Actor} joined as player {player.PlayerRaw}, PlayerData {playerData.Id}, counter {connection.Counter}, {_worldObjects.Count} objects to send");
    }

    /// <summary>
    /// A relay connection ended, by leave, timeout or the client (spec §11.4). A client that says goodbye
    /// leaves the Photon room a few milliseconds later; its cleanup waits for that (up to
    /// <see cref="DepartureGrace"/>), so the Photon master hand-over in the Leave event reaches the others
    /// before the PlayerLeft that the PlayerData destroy raises, and both masters move together.
    /// </summary>
    private void OnDisconnected(FusionRelayConnection connection, NetDisconnectReason reason)
    {
        if (!_players.Remove(connection.Actor, out var leaver)) return;
        if (reason == NetDisconnectReason.Requested && !_kicking && _registered.Contains(connection.Actor))
        {
            _departing[connection.Actor] = (leaver, _now + DepartureGrace);
            return;
        }
        CleanUpAfter(leaver, reason);
    }

    /// <summary>A server-initiated disconnect: cleaned up at once, not given the goodbye grace.</summary>
    private void Kick(FusionSimPlayer player, string why)
    {
        Log.Warn(Cat, $"\"{Name}\" actor {player.Actor} disconnected: {why}");
        _kicking = true;
        try { _peer.Disconnect(player.Connection); }
        finally { _kicking = false; }
    }

    private void CleanUpAfter(FusionSimPlayer leaver, NetDisconnectReason reason)
    {
        if (_objects.TryGetValue(leaver.PlayerDataId, out var playerData)) Destroy(playerData);

        int destroyed = 0, released = 0;
        foreach (var obj in _worldObjects.ToArray())
        {
            if (obj.StateAuthority != leaver.PlayerRaw) continue;   // MasterClient objects follow the master
            if ((obj.Flags & FusionObject.FlagDestroyWhenStateAuthorityLeaves) != 0)
            {
                Destroy(obj);
                destroyed++;
            }
            else
            {
                obj.Set(FusionObject.StateAuthorityWord, FusionObject.PlayerNone, NextStamp);
                released++;
            }
        }
        Log.Debug(Cat, $"\"{Name}\" actor {leaver.Actor} left ({reason}): {destroyed} objects destroyed, {released} released");
    }

    /// <summary>Destroys an object (spec §8.5): its words go now, each connection that knows it is told.</summary>
    private void Destroy(FusionObject obj)
    {
        _objects.Remove(obj.Id);
        if (obj.IsStruct) _playerData.Remove(obj);
        else
        {
            // Swap-remove keeps a destroy O(1); the order is only the rotation order of §8.3.
            var last = _worldObjects[^1];
            _worldObjects[obj.WorldIndex] = last;
            last.WorldIndex = obj.WorldIndex;
            _worldObjects.RemoveAt(_worldObjects.Count - 1);
            Charge(obj, -1);
            if (obj.HasTransform) RememberTransformId(obj.Id);
        }
        foreach (var player in _players.Values)
        {
            if (!player.Objects.TryGetValue(obj.Id, out var state)) continue;
            if (state.Status != ObjectSendStatus.CreatedConfirmed && state.Sent == 0)
            {
                player.Objects.Remove(obj.Id);   // never carried by any packet the client accepted
                continue;
            }
            state.Status = ObjectSendStatus.DestroyUnconfirmed;
            player.PendingDestroys.Add(obj.Id);
        }
    }
}
