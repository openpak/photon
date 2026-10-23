using System.Net;
using System.Net.Sockets;
using OpenPak.Photon;
using OpenPak.Photon.Transport;

namespace OpenPak.Server.Core;

/// <summary>
/// Receives Photon UDP datagrams for one server role, parses the packet header + commands,
/// and routes them to the owning <see cref="PeerConnection"/>. Malformed datagrams are logged
/// and dropped, never fatal.
/// </summary>
public sealed class UdpListener
{
    // Keepalive / dead-peer cleanup cadence (from config; defaults match Photon's ~1s/~10s). The
    // client pings us ~1s; we run maintenance each MaintenanceInterval, actively ping a peer that's
    // been inbound-silent for PingQuietAfter, and evict (the only way we reclaim peer state) one we
    // haven't heard from in DeadTimeout.
    private readonly TimeSpan _maintenanceInterval;
    private readonly TimeSpan _pingQuietAfter;
    private readonly TimeSpan _deadTimeout;
    private readonly TimeSpan _inRoomDeadTimeout;

    private readonly string _name;
    private readonly UdpClient _socket;
    private readonly IOperationHandler _handler;
    private readonly Dictionary<IPEndPoint, PeerConnection> _peers = new();

    /// <summary>
    /// Optional work to run once per maintenance pass, on this listener's single loop thread.
    /// Lets the host drive thread-affine work (e.g. ticking playerbots, whose relay path mutates
    /// the same EnetPeer send state this thread already touches) without introducing a data race.
    /// </summary>
    public System.Action? OnMaintenance { get; set; }

    /// <summary>Optional fast hook run on the listener thread every <see cref="TickInterval"/> (default
    /// 15 ms) — for work that must drive peers at simulation rate (the Fusion Shared-mode server ticks
    /// at 32 Hz+). Unlike <see cref="OnMaintenance"/> it bounds the receive wait, so it costs a wakeup
    /// per interval even when idle; leave null when unused.</summary>
    public System.Action? OnTick { get; set; }
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(15);

    /// <summary>The listener whose loop is running the current code (receive path, OnMaintenance,
    /// OnTick), or null outside any loop. The loop is sequential but NOT thread-bound: each awaited
    /// receive resumes on whatever pool thread is free, so "listener affinity" must be keyed on this,
    /// never on a thread id (a room sim keyed on the thread id stopped ticking for 20 s when the pool
    /// parked that thread — the 2026-09-05 two-player drop).</summary>
    public static UdpListener? Current => _current.Value;
    private static readonly AsyncLocal<UdpListener?> _current = new();

    public UdpListener(string name, int port, IOperationHandler handler, ListenerTimings? timings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, "port must be 0..65535");
        _name = name;
        _handler = handler;
        _socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        timings ??= new ListenerTimings();
        _maintenanceInterval = timings.Maintenance;
        _pingQuietAfter = timings.PingQuiet;
        _deadTimeout = timings.DeadTimeout;
        _inRoomDeadTimeout = timings.InRoomDeadTimeout;
    }

    /// <summary>The UDP port the listener is bound to (the real one when constructed with port 0).</summary>
    public int LocalPort => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info(_name, $"listening on UDP :{((IPEndPoint)_socket.Client.LocalEndPoint!).Port}");
        // Single-threaded loop: one receive and one timer task stay pending across iterations and
        // are re-armed only when they complete, so idle periods still wake us for tick/maintenance.
        // Never cancel a per-iteration receive: on Linux a cancelled Socket.ReceiveAsync stays queued
        // in SocketAsyncContext until data arrives, and at one cancel per 16 ms tick that leaked
        // ~6 M receive operations (2.8 GB) in 80 min. Because receive + maintenance share this one
        // thread, _peers needs no locking.
        _current.Value = this;   // flows to every continuation of this loop, whichever thread runs it
        var lastMaintenance = DateTime.UtcNow;
        var lastTick = lastMaintenance;
        Task<UdpReceiveResult>? recv = null;
        Task? timer = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                recv ??= _socket.ReceiveAsync(ct).AsTask();
                if (timer is null)
                {
                    var wait = _maintenanceInterval;
                    if (OnTick is not null)
                    {
                        var untilTick = TickInterval - (DateTime.UtcNow - lastTick);
                        wait = untilTick < wait ? (untilTick > TimeSpan.Zero ? untilTick : TimeSpan.FromMilliseconds(1)) : wait;
                    }
                    timer = Task.Delay(wait, ct);
                }
                if (await Task.WhenAny(recv, timer) == recv)
                {
                    var r = recv;
                    recv = null;   // re-armed next iteration, also after a faulted receive
                    var result = await r;
                    try { Process(result.Buffer, result.RemoteEndPoint); }
                    catch (Exception ex)
                    {
                        // A single bad datagram must never take the listener down — but we now log the
                        // full exception (was: one-line message) so a mid-session failure is visible.
                        Log.Exception(_name, $"drop datagram from {Redact.Peer(result.RemoteEndPoint)} " +
                                             $"({result.Buffer.Length}B: {Redact.Hex(result.Buffer, 64)})", ex);
                    }
                }
                else timer = null;   // timed out → fall through to tick / maintenance
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }   // real shutdown
            catch (SocketException ex) { Log.Debug(_name, $"recv socket error (ignored): {ex.SocketErrorCode}"); }

            var now = DateTime.UtcNow;
            if (OnTick is not null && now - lastTick >= TickInterval)
            {
                lastTick = now;
                try { OnTick(); }
                catch (Exception ex) { Log.Exception(_name, "OnTick hook threw", ex); }
            }
            if (now - lastMaintenance >= _maintenanceInterval) { RunMaintenance(now); lastMaintenance = now; }
        }
        Log.Info(_name, "listener loop exited");
    }

    /// <summary>
    /// How long a peer may go inbound-silent before eviction, by session stage. A peer that has joined
    /// a room (a host, or a joined player) gets the longer <c>InRoomDeadTimeout</c>: the Switch
    /// friend-invite system applet SUSPENDS the game, so an in-room host can neither send nor answer
    /// keepalives while it is open, and no short timeout survives an applet left on screen. Losing
    /// in-room state drops the whole session (the client's "connection interrupted" screen), so it is
    /// reaped only after the long grace; a pre-room peer stuck at connect/auth is reclaimed quickly.
    /// </summary>
    public TimeSpan EvictionGrace(SessionStatus status)
        => status == SessionStatus.InRoom ? _inRoomDeadTimeout : _deadTimeout;

    /// <summary>Pings quiet peers and evicts ones gone silent past their <see cref="EvictionGrace"/> or hard-kicked.</summary>
    private void RunMaintenance(DateTime now)
    {
        List<IPEndPoint>? dead = null;
        foreach (var (ep, peer) in _peers)
        {
            if (peer.WantsDisconnect || now - peer.LastInboundUtc >= EvictionGrace(peer.Status)) (dead ??= new()).Add(ep);
            else peer.MaybePing(now, _pingQuietAfter);
        }
        if (dead is not null)
            foreach (var ep in dead)
            {
                if (!_peers.Remove(ep, out var peer)) continue;
                var why = peer.WantsDisconnect ? "kicked" : $"no inbound for {EvictionGrace(peer.Status).TotalSeconds:F0}s+";
                Log.Info(_name, $"evicting peer {Redact.Peer(ep)} ({why}); {_peers.Count} remain");
                peer.NotifyDisconnect();
            }

        // Run host-supplied work on this same single thread, after the peer bookkeeping above. A
        // throwing hook (e.g. a bot bug) must never take the listener down, so swallow + log.
        try { OnMaintenance?.Invoke(); }
        catch (Exception ex) { Log.Exception(_name, "OnMaintenance hook threw", ex); }
    }

    private void Process(byte[] datagram, IPEndPoint from)
    {
        Log.Trace(_name, $"RECV {Redact.Peer(from)} {datagram.Length}B: {Redact.Hex(datagram, 128)}");
        // Photon's region ping (a client choosing the best region pings each one): answered before any
        // peer state exists, exactly as it came. No real Photon packet has this shape (125 commands in 13 bytes).
        if (RegionPing.IsPing(datagram)) { _socket.Send(datagram, datagram.Length, from); return; }
        if (datagram.Length < PhotonHeader.Size) { Log.Debug(_name, $"runt datagram from {Redact.Peer(from)} ({datagram.Length}B)"); return; }
        var header = PhotonHeader.ReadFrom(datagram);
        Log.Trace(_name, $"  header peer={header.PeerId} crc={header.CrcEnabled} cmds={header.CommandCount} " +
                         $"srvTime={header.ServerTime} challenge={header.Challenge}");

        var commands = new List<NCommand>(header.CommandCount);
        int offset = PhotonHeader.Size;
        for (int i = 0; i < header.CommandCount && offset + NCommand.HeaderSize <= datagram.Length; i++)
        {
            var cmd = NCommand.Parse(datagram.AsSpan(offset), out int consumed);
            if (consumed <= 0) { Log.Warn(_name, $"command parse stalled at offset {offset} (consumed {consumed})"); break; }
            Log.Trace(_name, $"  cmd[{i}] {PhotonNames.Command(cmd.CommandType)} ch={cmd.ChannelId} " +
                             $"flags={cmd.Flags} seq={cmd.ReliableSequenceNumber} payload={cmd.Payload.Length}B");
            commands.Add(cmd);
            offset += consumed;
        }

        if (!_peers.TryGetValue(from, out var peer))
        {
            peer = new PeerConnection(_name, from, _handler, (cmds, challenge) => Send(from, cmds, challenge));
            _peers[from] = peer;
            Log.Info(_name, $"new peer {Redact.Peer(from)} (total {_peers.Count})");
        }
        peer.HandlePacket(header, commands);
    }

    /// <summary>
    /// Sends the commands, packing as many as fit per datagram. The MTU limit is the client's, not
    /// ours: it reads each datagram into a buffer exactly one MTU long, so a bigger one is truncated
    /// and dropped by its command parser (measured against the real client library).
    /// </summary>
    private void Send(IPEndPoint to, IReadOnlyList<NCommand> commands, int challenge)
    {
        var batch = new List<NCommand>(commands.Count);
        int size = PhotonHeader.Size;
        foreach (var cmd in commands)
        {
            int cmdSize = cmd.ToBytes().Length;
            if (batch.Count > 0 && size + cmdSize > EnetPeer.Mtu) { SendOne(to, batch, challenge); batch.Clear(); size = PhotonHeader.Size; }
            batch.Add(cmd);
            size += cmdSize;
        }
        if (batch.Count > 0) SendOne(to, batch, challenge);
    }

    private void SendOne(IPEndPoint to, IReadOnlyList<NCommand> commands, int challenge)
    {
        if (commands.Count == 0) return;
        var body = commands.SelectMany(c => c.ToBytes()).ToArray();
        var packet = new byte[PhotonHeader.Size + body.Length];
        new PhotonHeader(0, false, (byte)commands.Count, Environment.TickCount, challenge).WriteTo(packet);
        body.CopyTo(packet, PhotonHeader.Size);
        if (packet.Length > EnetPeer.Mtu)
            Log.Warn(_name, $"datagram {packet.Length}B exceeds the client's {EnetPeer.Mtu}B MTU — it will be dropped unread " +
                            $"([{string.Join("+", commands.Select(c => PhotonNames.Command(c.CommandType)))}])");
        try
        {
            int sent = _socket.Send(packet, packet.Length, to);
            if (Log.Enabled(LogLevel.Trace))
            {
                var kinds = string.Join("+", commands.Select(c => PhotonNames.Command(c.CommandType)));
                Log.Trace(_name, $"SEND {Redact.Peer(to)} {sent}B [{kinds}] challenge={challenge}: {Redact.Hex(packet, 128)}");
            }
        }
        catch (Exception ex)
        {
            // If the server ever stops replying mid-session the client times out and reconnects;
            // a send failure is the prime suspect, so surface it loudly rather than letting it bubble.
            Log.Exception(_name, $"SEND to {Redact.Peer(to)} failed ({packet.Length}B)", ex);
        }
    }
}
