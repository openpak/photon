using System.Net;
using System.Net.Sockets;
using BlackIce.Photon;
using BlackIce.Photon.Transport;

namespace BlackIce.Server.Core;

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
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info(_name, $"listening on UDP :{((IPEndPoint)_socket.Client.LocalEndPoint!).Port}");
        // Single-threaded loop: ReceiveAsync is bounded by a per-iteration timeout so that idle
        // periods still wake us to run peer maintenance. Because receive + maintenance share this
        // one thread, _peers needs no locking.
        var lastMaintenance = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                recvCts.CancelAfter(_maintenanceInterval);
                var result = await _socket.ReceiveAsync(recvCts.Token);

                try { Process(result.Buffer, result.RemoteEndPoint); }
                catch (Exception ex)
                {
                    // A single bad datagram must never take the listener down — but we now log the
                    // full exception (was: one-line message) so a mid-session failure is visible.
                    Log.Exception(_name, $"drop datagram from {result.RemoteEndPoint} " +
                                         $"({result.Buffer.Length}B: {PhotonNames.Hex(result.Buffer, 64)})", ex);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }   // real shutdown
            catch (OperationCanceledException) { /* receive timed out → fall through to maintenance */ }
            catch (SocketException ex) { Log.Debug(_name, $"recv socket error (ignored): {ex.SocketErrorCode}"); }

            var now = DateTime.UtcNow;
            if (now - lastMaintenance >= _maintenanceInterval) { RunMaintenance(now); lastMaintenance = now; }
        }
        Log.Info(_name, "listener loop exited");
    }

    /// <summary>Pings quiet peers and evicts ones gone silent past <see cref="DeadTimeout"/> or hard-kicked.</summary>
    private void RunMaintenance(DateTime now)
    {
        List<IPEndPoint>? dead = null;
        foreach (var (ep, peer) in _peers)
        {
            if (peer.WantsDisconnect || now - peer.LastInboundUtc >= _deadTimeout) (dead ??= new()).Add(ep);
            else peer.MaybePing(now, _pingQuietAfter);
        }
        if (dead is not null)
            foreach (var ep in dead)
            {
                if (!_peers.Remove(ep, out var peer)) continue;
                var why = peer.WantsDisconnect ? "kicked" : $"no inbound for {_deadTimeout.TotalSeconds:F0}s+";
                Log.Info(_name, $"evicting peer {ep} ({why}); {_peers.Count} remain");
                peer.NotifyDisconnect();
            }

        // Run host-supplied work on this same single thread, after the peer bookkeeping above. A
        // throwing hook (e.g. a bot bug) must never take the listener down, so swallow + log.
        try { OnMaintenance?.Invoke(); }
        catch (Exception ex) { Log.Exception(_name, "OnMaintenance hook threw", ex); }
    }

    private void Process(byte[] datagram, IPEndPoint from)
    {
        Log.Trace(_name, $"RECV {from} {datagram.Length}B: {PhotonNames.Hex(datagram, 128)}");
        if (datagram.Length < PhotonHeader.Size) { Log.Debug(_name, $"runt datagram from {from} ({datagram.Length}B)"); return; }
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
            Log.Info(_name, $"new peer {from} (total {_peers.Count})");
        }
        peer.HandlePacket(header, commands);
    }

    private void Send(IPEndPoint to, IReadOnlyList<NCommand> commands, int challenge)
    {
        if (commands.Count == 0) return;
        var body = commands.SelectMany(c => c.ToBytes()).ToArray();
        var packet = new byte[PhotonHeader.Size + body.Length];
        new PhotonHeader(0, false, (byte)commands.Count, Environment.TickCount, challenge).WriteTo(packet);
        body.CopyTo(packet, PhotonHeader.Size);
        try
        {
            int sent = _socket.Send(packet, packet.Length, to);
            if (Log.Enabled(LogLevel.Trace))
            {
                var kinds = string.Join("+", commands.Select(c => PhotonNames.Command(c.CommandType)));
                Log.Trace(_name, $"SEND {to} {sent}B [{kinds}] challenge={challenge}: {PhotonNames.Hex(packet, 128)}");
            }
        }
        catch (Exception ex)
        {
            // If the server ever stops replying mid-session the client times out and reconnects;
            // a send failure is the prime suspect, so surface it loudly rather than letting it bubble.
            Log.Exception(_name, $"SEND to {to} failed ({packet.Length}B)", ex);
        }
    }
}
