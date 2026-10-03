using System.Buffers.Binary;
using OpenPak.Server.Core;

namespace OpenPak.Server.LoadBalancing.Fusion;

/// <summary>
/// The server side of Fusion 2's relay transport for one room: the peer at actor 0 that Fusion clients
/// connect to over Photon event 101 (docs/protocol/07-fusion-relay-transport.md). It runs the
/// Connect/Accepted handshake, the sequenced and delivery-notified notify channel, fragmentation, the
/// reliable channel and its player-to-player proxying, keepalive and timeouts, and hands payloads up to
/// the simulation layer through the events below.
///
/// Pure and deterministic: no sockets, threads or clock. Datagrams come in through <see cref="Receive"/>,
/// time advances only through <see cref="Update"/> (a datagram is stamped with the time of the last
/// update), and every outbound datagram goes to the sink given at construction as
/// <c>(actor, datagram, reliable)</c> — reliable only for the pieces of a fragmented notify packet.
/// Single-threaded by contract: the caller is the Game listener loop. Event handlers run synchronously
/// inside <see cref="Receive"/>/<see cref="Update"/> and may call back into the peer.
/// </summary>
public sealed class FusionRelayPeer
{
    public const int NotifyWindowSize = 128;
    /// <summary>A notify sequence further than this from the newest received one disconnects (128 × 16).</summary>
    public const int SequenceBounds = NotifyWindowSize * 16;
    public const int AckForceCount = 8;
    public const double AckForceTimeout = 0.1, ReliableSendInterval = 0.05;

    /// <summary>Reliable slices stored ahead of the next expected one. A legitimate sender is held to
    /// roughly the notify window; the SDK has no bound, but this is a trust boundary.</summary>
    private const int MaxReliableReorder = 1024;
    /// <summary>Slices queued for one connection by the player-to-player proxy before further ones are
    /// dropped, so one client cannot grow another's queue without bound (the drain is 20 slices/s).</summary>
    private const int MaxForwardQueue = 4096;
    private const int ServerPlayerId = -1;
    private const string Cat = "FusionRelay";

    private readonly Action<int, byte[], bool> _send;
    private readonly int _maxConnections;
    private readonly double _connectionTimeout;
    private readonly double _shutdownTime;

    /// <summary>Slots indexed by GroupIndex; null = free. Holds Disconnected/Shutdown connections until release.</summary>
    private readonly List<FusionRelayConnection?> _slots = new();
    private readonly List<uint> _slotGenerations = new();
    /// <summary>The address map: live connections by actor. A connection leaves it when it reaches Shutdown.</summary>
    private readonly Dictionary<int, FusionRelayConnection> _byActor = new();
    /// <summary>Every occupied slot by UniqueId, including dying ones (a Connect for a dying slot is ignored).</summary>
    private readonly Dictionary<ulong, FusionRelayConnection> _byUniqueId = new();
    private readonly Dictionary<int, (ulong UniqueId, int PlayerId)> _registered = new();
    private int _occupiedSlots;
    private uint _counter;
    private int _serverSourceSend;
    private double _now;

    /// <param name="maxConnections">The room's player limit; a Connect beyond it gets Refused(ServerFull).</param>
    /// <param name="send">Outbound sink: <c>(actor, datagram, reliable)</c>.</param>
    /// <param name="connectionTimeout">Inactivity disconnect, seconds (project config <c>Network.ConnectionTimeout</c>).</param>
    /// <param name="shutdownTime">Slot reuse delay after a disconnect, seconds (<c>Network.ConnectionShutdownTime</c>).</param>
    public FusionRelayPeer(int maxConnections, Action<int, byte[], bool> send, double connectionTimeout = 10, double shutdownTime = 1)
    {
        _maxConnections = Math.Clamp(maxConnections, 1, 2048);
        _send = send;
        _connectionTimeout = connectionTimeout;
        _shutdownTime = shutdownTime;
    }

    /// <summary>A client completed the handshake; it was just sent Accepted.</summary>
    public event Action<FusionRelayConnection>? Connected;

    /// <summary>A connection ended (any cause). Its outstanding notify tokens will never be reported:
    /// treat them all as dropped.</summary>
    public event Action<FusionRelayConnection, NetDisconnectReason>? Disconnected;

    /// <summary>A NotifyData payload arrived. The reader is positioned at bit 112 (just after the header)
    /// and runs to the end of the datagram or reassembled packet, so up to 7 trailing zero bits may follow
    /// the real payload. A read past the end throws; the handler must catch it and drop the packet.</summary>
    public event Action<FusionRelayConnection, NetBitReader>? NotifyReceived;

    /// <summary>The peer acknowledged the notify packet sent with this token.</summary>
    public event Action<FusionRelayConnection, object?>? NotifyDelivered;

    /// <summary>The notify packet sent with this token was not received; the simulation decides what to resend.</summary>
    public event Action<FusionRelayConnection, object?>? NotifyLost;

    /// <summary>An in-order reliable slice addressed to the server (<c>Target == -1</c>). Slices for other
    /// players are forwarded by the peer and never surface here. Joining slices into messages (by
    /// Source/SourceSend until TotalLength) is the caller's job (spec §8).</summary>
    public event Action<FusionRelayConnection, ReliableId, byte[]>? ReliableReceived;

    /// <summary>Allows <paramref name="actor"/> to connect with <paramref name="uniqueId"/> (the 8 bytes sent
    /// in its event-100 Join Confirmation). Until this is called its Connects are answered with nothing
    /// ("Waiting"), and the client retries every 0.5 s for about 5 s.</summary>
    /// <param name="playerId">The value this player's reliable slices carry as Source/Target.</param>
    public void Register(int actor, ReadOnlySpan<byte> uniqueId, int playerId)
    {
        if (uniqueId.Length != 8) throw new ArgumentException("a Fusion UniqueId is 8 bytes", nameof(uniqueId));
        _registered[actor] = (BinaryPrimitives.ReadUInt64LittleEndian(uniqueId), playerId);
    }

    /// <summary>Forgets <paramref name="actor"/>'s registration and disconnects its connection (Requested).</summary>
    public void Unregister(int actor)
    {
        _registered.Remove(actor);
        if (_byActor.TryGetValue(actor, out var connection)) Disconnect(connection);
    }

    /// <summary>The live connection for <paramref name="actor"/>, if any.</summary>
    public FusionRelayConnection? ConnectionOf(int actor) => _byActor.GetValueOrDefault(actor);

    /// <summary>One event-101 datagram from <paramref name="actor"/>. Untrusted input: never throws on
    /// malformed data (handlers raised from here may, see <see cref="NotifyReceived"/>).</summary>
    public void Receive(int actor, ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length == 0) return;
        if (!_byActor.TryGetValue(actor, out var connection))
        {
            // Only a Connect may come from an unknown address (spec §3).
            if (FusionRelayCodec.TryDecodeConnect(datagram, out var connect)) HandleNewConnect(actor, connect);
            else if (Log.Enabled(LogLevel.Trace)) Log.Trace(Cat, $"actor {actor} type {datagram[0]} dropped: no connection");
            return;
        }

        connection.LastRecv = _now;   // any datagram, any status (spec §6.2)
        switch ((NetPacketType)datagram[0])
        {
            case NetPacketType.Command:
                HandleCommand(connection, datagram);
                break;
            case NetPacketType.NotifyAcks:
                if (connection.Status == NetConnectionStatus.Connected && FusionRelayCodec.TryReadNotifyHeader(datagram, out var acks))
                    ProcessAcks(connection, acks);
                break;
            case NetPacketType.NotifyData or NetPacketType.NotifyReliableData:
                // A notify packet with an empty payload is dropped before its acks are read (spec §3.2).
                if (connection.Status == NetConnectionStatus.Connected && datagram.Length > FusionRelayCodec.NotifyHeaderSize)
                    ReceiveNotify(connection, datagram);
                break;
            default:
                // UnreliableData is never sent by a Fusion client; Unconnected/MTU types are unused on the relay.
                if (Log.Enabled(LogLevel.Trace)) Log.Trace(Cat, $"actor {actor} type {datagram[0]} dropped");
                break;
        }
    }

    /// <summary>
    /// Sends one simulation packet on the notify channel: stamps the next sequence and our current acks,
    /// and fragments it into reliable pieces if it exceeds 1280 bytes with the header (spec §6.4, §7).
    /// <paramref name="token"/> comes back through exactly one of <see cref="NotifyDelivered"/> /
    /// <see cref="NotifyLost"/>, or never if the connection ends first. Returns false — nothing sent — when
    /// the connection is not Connected or its 128-packet window is full (<see cref="FusionRelayConnection.CanSendNotify"/>).
    /// </summary>
    /// <exception cref="ArgumentException">An empty payload (the receiver would drop it) or one over 44 880 bytes.</exception>
    public bool TrySendNotify(FusionRelayConnection connection, NetBitWriter payload, object? token = null)
    {
        var payloadBytes = payload.Bytes;
        if (payloadBytes.Length == 0) throw new ArgumentException("an empty NotifyData is dropped by the receiver", nameof(payload));
        if (payloadBytes.Length > FusionRelayCodec.MaxNotifyPayloadBytes) throw new ArgumentException("notify payload over 44 880 bytes", nameof(payload));
        if (!connection.CanSendNotify) return false;

        var header = StampNotify(connection, NetPacketType.NotifyData, token);
        connection.RecvUnacked = 0;   // our acks ride on this packet (a type 8 does not reset this)

        if (FusionRelayCodec.NotifyHeaderSize + payloadBytes.Length <= FusionRelayCodec.MaxDatagramBytes)
        {
            _send(connection.Actor, BuildNotify(header, payloadBytes), false);
            return true;
        }
        // One sequence and one envelope cover every piece; each piece repeats the header with its index.
        var pieces = (payloadBytes.Length + FusionRelayCodec.FragmentChunkBytes - 1) / FusionRelayCodec.FragmentChunkBytes;
        for (var piece = 1; piece <= pieces; piece++)
        {
            var offset = (piece - 1) * FusionRelayCodec.FragmentChunkBytes;
            var chunk = payloadBytes.Slice(offset, Math.Min(FusionRelayCodec.FragmentChunkBytes, payloadBytes.Length - offset));
            var fragment = (byte)(piece | (piece == pieces ? FusionRelayCodec.FragmentEndBit : 0));
            _send(connection.Actor, BuildNotify(header with { Fragment = fragment }, chunk), true);
        }
        return true;
    }

    /// <summary>Queues a message from the server to <paramref name="connection"/>'s player on the reliable
    /// channel: split into slices of ≤ 1088 bytes, drained one per 50 ms, resent until acknowledged (spec §8).
    /// Returns false when the connection is not Connected.</summary>
    /// <param name="key">The 16-byte application <c>ReliableKey</c>.</param>
    public bool SendReliable(FusionRelayConnection connection, ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
    {
        if (key.Length != FusionRelayCodec.ReliableKeyLength) throw new ArgumentException("a ReliableKey is 16 bytes", nameof(key));
        if (message.IsEmpty) throw new ArgumentException("an empty reliable message has no slices", nameof(message));
        if (connection.Status != NetConnectionStatus.Connected) return false;

        var sourceSend = ++_serverSourceSend;
        var keyBytes = key.ToArray();
        for (var offset = 0; offset < message.Length; offset += FusionRelayCodec.MaxReliableSliceBytes)
        {
            var data = message.Slice(offset, Math.Min(FusionRelayCodec.MaxReliableSliceBytes, message.Length - offset)).ToArray();
            var id = new ReliableId(++connection.ReliableSendSequence, data.Length, message.Length,
                ServerPlayerId, sourceSend, connection.PlayerId, keyBytes);
            connection.ReliableQueue.AddLast(new ReliableSlice(id, data));
        }
        return true;
    }

    /// <summary>Disconnects a Connected client (reason Requested): one Disconnect with
    /// <paramref name="token"/> now, a second without it on the next update (spec §5.4).</summary>
    public void Disconnect(FusionRelayConnection connection, ReadOnlySpan<byte> token = default)
    {
        if (connection.Status != NetConnectionStatus.Connected) return;
        connection.FollowUpDisconnectPending = true;
        DisconnectWith(connection, NetDisconnectReason.Requested, token);
    }

    /// <summary>
    /// Advances the transport to <paramref name="now"/> (seconds, any monotonic origin). Per Connected client,
    /// in this order: inactivity timeout, at most one reliable slice per 50 ms, then an ack-only packet if
    /// 8 packets are unacknowledged or nothing has been sent for 0.1 s — the only keepalive (spec §6.4).
    /// Disconnected clients move to Shutdown, and slots are released after the shutdown time.
    /// </summary>
    public void Update(double now)
    {
        _now = now;
        for (var index = 0; index < _slots.Count; index++)
        {
            var connection = _slots[index];
            switch (connection?.Status)
            {
                case NetConnectionStatus.Connected:
                    UpdateConnected(connection);
                    break;
                case NetConnectionStatus.Disconnected:
                    if (connection.FollowUpDisconnectPending)
                        _send(connection.Actor, FusionRelayCodec.EncodeDisconnect(NetDisconnectReason.Requested), false);
                    // Unacknowledged envelopes are disposed: neither delivered nor lost.
                    connection.SendWindow.Clear();
                    connection.ReliableQueue.Clear();
                    connection.ReliableStore.Clear();
                    connection.FragmentBuffer = null;
                    if (_byActor.GetValueOrDefault(connection.Actor) == connection) _byActor.Remove(connection.Actor);
                    connection.Status = NetConnectionStatus.Shutdown;
                    connection.ReleaseAt = now + _shutdownTime;
                    break;
                case NetConnectionStatus.Shutdown when now >= connection.ReleaseAt:
                    _slots[index] = null;
                    _occupiedSlots--;
                    _byUniqueId.Remove(connection.UniqueId);
                    Log.Debug(Cat, $"slot {index} released (actor {connection.Actor})");
                    break;
            }
        }
    }

    private void UpdateConnected(FusionRelayConnection connection)
    {
        if (_now - connection.LastRecv > _connectionTimeout)
        {
            Log.Debug(Cat, $"actor {connection.Actor} timed out after {_now - connection.LastRecv:F1}s of silence");
            DisconnectWith(connection, NetDisconnectReason.Timeout);
            return;
        }

        if (connection.ReliableQueue.First is { } next && connection.SendWindow.Count < NotifyWindowSize
            && _now - connection.LastReliableSend >= ReliableSendInterval)
        {
            connection.ReliableQueue.RemoveFirst();
            connection.LastReliableSend = _now;
            var header = StampNotify(connection, NetPacketType.NotifyReliableData, next.Value);
            var datagram = new byte[FusionRelayCodec.NotifyHeaderSize + FusionRelayCodec.ReliableIdSize + next.Value.Data.Length];
            FusionRelayCodec.WriteNotifyHeader(datagram, header);
            FusionRelayCodec.WriteReliableId(datagram.AsSpan(FusionRelayCodec.NotifyHeaderSize), next.Value.Id);
            next.Value.Data.CopyTo(datagram, FusionRelayCodec.NotifyHeaderSize + FusionRelayCodec.ReliableIdSize);
            _send(connection.Actor, datagram, false);
        }

        if (connection.RecvUnacked >= AckForceCount || _now - connection.LastNotifySend > AckForceTimeout)
        {
            _send(connection.Actor, FusionRelayCodec.EncodeNotifyAcks(connection.RecvSequence, connection.RecvMask), false);
            connection.RecvUnacked = 0;
            connection.LastNotifySend = _now;
        }
    }

    /// <summary>Assigns the next notify sequence, records the send-window envelope and returns the header
    /// carrying our current acks.</summary>
    private NotifyHeader StampNotify(FusionRelayConnection connection, NetPacketType type, object? token)
    {
        var sequence = ++connection.SendSequence;
        connection.SendWindow.Enqueue(new NotifyEnvelope(sequence, _now, type, token));
        connection.LastNotifySend = _now;
        return new NotifyHeader(type, 0, sequence, connection.RecvSequence, connection.RecvMask);
    }

    private static byte[] BuildNotify(NotifyHeader header, ReadOnlySpan<byte> payload)
    {
        var datagram = new byte[FusionRelayCodec.NotifyHeaderSize + payload.Length];
        FusionRelayCodec.WriteNotifyHeader(datagram, header);
        payload.CopyTo(datagram.AsSpan(FusionRelayCodec.NotifyHeaderSize));
        return datagram;
    }

    /// <summary>Sends one Disconnect with <paramref name="reason"/> and ends the connection; it moves to
    /// Shutdown on the next update.</summary>
    private void DisconnectWith(FusionRelayConnection connection, NetDisconnectReason reason, ReadOnlySpan<byte> token = default)
    {
        if (connection.Status != NetConnectionStatus.Connected) return;
        _send(connection.Actor, FusionRelayCodec.EncodeDisconnect(reason, token), false);
        connection.Status = NetConnectionStatus.Disconnected;
        connection.DisconnectReason = reason;
        Log.Debug(Cat, $"actor {connection.Actor} disconnected: {reason}");
        Disconnected?.Invoke(connection, reason);
    }

    /// <summary>A Connect from an actor with no live connection (spec §5.2 steps 1-2).</summary>
    private void HandleNewConnect(int actor, ConnectCommand connect)
    {
        if (_occupiedSlots >= _maxConnections)
        {
            Log.Debug(Cat, $"actor {actor} refused: server full ({_occupiedSlots}/{_maxConnections})");
            _send(actor, FusionRelayCodec.EncodeRefused(NetConnectFailedReason.ServerFull), false);
            return;
        }

        // The application decision. The SDK answers Waiting until the UniqueId has a PlayerRef mapping, then
        // Ok. Our registration also binds the UniqueId to the actor it was issued to, so a Connect presenting
        // another actor's UniqueId, or a different one than this actor was given, is refused outright
        // rather than accepted (SDK) or left to time out — a deliberate tightening at a trust boundary.
        if (!_registered.TryGetValue(actor, out var registration))
        {
            if (_registered.Values.Any(r => r.UniqueId == connect.UniqueId)) Refuse(actor, "UniqueId belongs to another actor");
            else if (Log.Enabled(LogLevel.Trace)) Log.Trace(Cat, $"actor {actor} Connect waiting: not registered yet");
            return;
        }
        if (registration.UniqueId != connect.UniqueId)
        {
            Refuse(actor, $"UniqueId {connect.UniqueId:X16} is not the registered {registration.UniqueId:X16}");
            return;
        }

        if (_byUniqueId.TryGetValue(connect.UniqueId, out var holder))
        {
            if (holder.Status == NetConnectionStatus.Connected)
            {
                // Spec §5.2 (inference): the slot follows the UniqueId to the new address and this Connect
                // gets no reply; the client's retry then hits the Connected case and gets the old Accepted.
                _byActor.Remove(holder.Actor);
                holder.Actor = actor;
                _byActor[actor] = holder;
                Log.Debug(Cat, $"connection {holder.LocalId.GroupIndex} moved to actor {actor}");
            }
            // A dying slot still holds the UniqueId until it is released; the client retries after that.
            return;
        }

        var connection = AllocateSlot(actor, registration.PlayerId, connect);
        // Status Created: process the Connect exactly as for an existing connection (spec §5.2 step 3).
        connection.RemoteId = connect.ConnectionId;
        connection.Counter = NextCounter();
        connection.Status = NetConnectionStatus.Connected;
        connection.LastRecv = _now;
        connection.LastNotifySend = _now;
        Log.Debug(Cat, $"actor {actor} connected: slot {connection.LocalId.GroupIndex} gen {connection.LocalId.Generation} counter {connection.Counter}");
        SendAccepted(connection);
        Connected?.Invoke(connection);
    }

    private void HandleCommand(FusionRelayConnection connection, ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 2) return;
        switch ((NetCommand)datagram[1])
        {
            case NetCommand.Connect:
                // Duplicate Connect: the client may have lost our Accepted, so send the same one again.
                // In any other status (dying) it is ignored.
                if (connection.Status == NetConnectionStatus.Connected) SendAccepted(connection);
                break;
            case NetCommand.Disconnect when FusionRelayCodec.TryDecodeDisconnect(datagram, out var reason, out _):
                // Recorded with the peer's own reason and echoed back with it (spec §5.4).
                if (connection.Status == NetConnectionStatus.Connected) DisconnectWith(connection, reason);
                else Log.Debug(Cat, $"actor {connection.Actor} Disconnect ignored in status {connection.Status}");
                break;
            default:
                Log.Debug(Cat, $"actor {connection.Actor} invalid command {datagram[1]}");
                break;
        }
    }

    /// <summary>NotifyData / NotifyReliableData from a Connected peer, longer than the header (spec §6.2).</summary>
    private void ReceiveNotify(FusionRelayConnection connection, ReadOnlySpan<byte> datagram)
    {
        FusionRelayCodec.TryReadNotifyHeader(datagram, out var header);
        int distance = (short)(header.Sequence - connection.RecvSequence);
        if (distance > SequenceBounds || distance < -SequenceBounds)
        {
            Log.Debug(Cat, $"actor {connection.Actor} sequence {header.Sequence} vs {connection.RecvSequence}: out of bounds");
            DisconnectWith(connection, NetDisconnectReason.SequenceOutOfBounds);
            return;
        }
        if (distance < 0) return;   // older than the newest: never accepted, the sender sees it lost
        // Trust boundary (spec §7): only NotifyData is ever fragmented, and an unfragmented datagram is at
        // most 1280 bytes. The SDK never sends anything else, so anything else is dropped unread.
        if (header.Fragment != 0 ? header.Type != NetPacketType.NotifyData : datagram.Length > FusionRelayCodec.MaxDatagramBytes)
        {
            if (Log.Enabled(LogLevel.Trace)) Log.Trace(Cat, $"actor {connection.Actor} {header.Type} fragment {header.Fragment} of {datagram.Length} bytes dropped");
            return;
        }
        if (header.Fragment != 0)
        {
            ReceiveFragment(connection, header, distance, datagram);
            return;
        }
        if (distance == 0) return;   // duplicate

        connection.ResetFragments();
        Accept(connection, header, distance);
        Deliver(connection, datagram.ToArray());
    }

    /// <summary>One piece of a fragmented notify packet (spec §7). Pieces must arrive consecutively with the
    /// same sequence; anything else discards the partial packet, which the sender then sees as lost.</summary>
    private void ReceiveFragment(FusionRelayConnection connection, NotifyHeader header, int distance, ReadOnlySpan<byte> datagram)
    {
        var index = header.Fragment & 0x7F;
        if (distance == 0) return;
        if (index == 1)
        {
            // Start over with the whole first datagram, header included, so the reassembled packet reads
            // like an unfragmented one. Per spec a first piece is never also the last (0x81 is not sent by
            // the SDK and would never complete here either).
            connection.ResetFragments();
            if (datagram.Length > FusionRelayCodec.MaxReassemblyBytes) return;
            connection.FragmentBuffer ??= new byte[FusionRelayCodec.MaxReassemblyBytes];
            datagram.CopyTo(connection.FragmentBuffer);
            connection.FragmentLength = datagram.Length;
            connection.FragmentIndex = 1;
            connection.FragmentDistance = distance;
            return;
        }
        if (index != connection.FragmentIndex + 1 || distance != connection.FragmentDistance)
        {
            connection.ResetFragments();
            return;
        }

        var chunk = datagram[FusionRelayCodec.NotifyHeaderSize..];
        if (connection.FragmentLength + chunk.Length > FusionRelayCodec.MaxReassemblyBytes)
        {
            connection.ResetFragments();
            return;
        }
        chunk.CopyTo(connection.FragmentBuffer.AsSpan(connection.FragmentLength));
        connection.FragmentLength += chunk.Length;
        connection.FragmentIndex = index;
        if ((header.Fragment & FusionRelayCodec.FragmentEndBit) == 0) return;

        var packet = connection.FragmentBuffer.AsSpan(0, connection.FragmentLength).ToArray();
        connection.ResetFragments();
        Accept(connection, header, distance);
        Deliver(connection, packet);
    }

    /// <summary>Takes the packet's sequence into the receive state and processes its acks (spec §6.2 "accept").</summary>
    private void Accept(FusionRelayConnection connection, NotifyHeader header, int distance)
    {
        connection.RecvSequence = header.Sequence;
        // A gap of 64 or more clears the history: everything older reads as lost to the sender.
        connection.RecvMask = distance >= 64 ? 1UL : (connection.RecvMask << distance) | 1UL;
        connection.RecvUnacked++;
        ProcessAcks(connection, header);
    }

    /// <summary>Resolves our send window against the peer's AckSequence/AckMask, oldest first (spec §6.3).</summary>
    private void ProcessAcks(FusionRelayConnection connection, NotifyHeader header)
    {
        // A handler may disconnect the connection; its remaining envelopes are then discarded at shutdown.
        while (connection.SendWindow.Count > 0 && connection.Status == NetConnectionStatus.Connected)
        {
            var envelope = connection.SendWindow.Peek();
            int distance = (short)(envelope.Sequence - header.AckSequence);
            if (distance > 0) break;   // the peer has not reached this one yet
            if (distance == 0) connection.Rtt = Math.Max(0, _now - envelope.SendTime);
            connection.SendWindow.Dequeue();
            var delivered = distance > -64 && ((header.AckMask >> -distance) & 1) != 0;

            if (envelope.Type == NetPacketType.NotifyReliableData)
            {
                // A lost slice is resent (with a new notify sequence) in a later reliable slot.
                if (!delivered) connection.ReliableQueue.AddFirst((ReliableSlice)envelope.Token!);
            }
            else if (delivered) NotifyDelivered?.Invoke(connection, envelope.Token);
            else NotifyLost?.Invoke(connection, envelope.Token);
        }
    }

    /// <summary>Hands an accepted packet (whole datagram or reassembled, header included) to its channel.</summary>
    private void Deliver(FusionRelayConnection connection, byte[] packet)
    {
        if (connection.Status != NetConnectionStatus.Connected) return;   // an ack handler disconnected it
        if (packet[0] == (byte)NetPacketType.NotifyData)
            NotifyReceived?.Invoke(connection, new NetBitReader(packet, FusionRelayCodec.NotifyHeaderSize * 8));
        else
            ReceiveReliable(connection, packet);
    }

    /// <summary>A reliable-channel slice (spec §8): delivered in Sequence order, out-of-order ones stored.</summary>
    private void ReceiveReliable(FusionRelayConnection connection, byte[] packet)
    {
        const int dataOffset = FusionRelayCodec.NotifyHeaderSize + FusionRelayCodec.ReliableIdSize;
        if (!FusionRelayCodec.TryReadReliableId(packet.AsSpan(FusionRelayCodec.NotifyHeaderSize), out var id))
        {
            Log.Debug(Cat, $"actor {connection.Actor} reliable slice too short ({packet.Length} bytes)");
            return;
        }
        // A slice is at most 1088 bytes, says its own length truthfully (a receiver trusts SliceLength),
        // and a player only ever sends its own messages: one naming another Source would let it inject
        // into that player's stream at the receiver (spec §8.1).
        var length = packet.Length - dataOffset;
        if (length > FusionRelayCodec.MaxReliableSliceBytes || id.SliceLength != length || id.Source != connection.PlayerId)
        {
            Log.Debug(Cat, $"actor {connection.Actor} reliable slice dropped: {length} bytes, SliceLength {id.SliceLength}, source {id.Source}");
            return;
        }
        var slice = new ReliableSlice(id, packet[dataOffset..]);

        var ahead = (int)(id.Sequence - (connection.ReliableLastDelivered + 1));
        // Behind = already delivered (a resend whose ack was lost). The SDK stores those forever; dropping
        // them is equivalent, since they could never match again. Far ahead is not something a sender
        // limited by the notify window produces.
        if (ahead < 0 || ahead >= MaxReliableReorder)
        {
            if (Log.Enabled(LogLevel.Trace)) Log.Trace(Cat, $"actor {connection.Actor} reliable {id.Sequence} dropped, expected {connection.ReliableLastDelivered + 1}");
            return;
        }
        if (ahead > 0)
        {
            connection.ReliableStore[id.Sequence] = slice;
            return;
        }

        DeliverReliable(connection, slice);
        while (connection.ReliableStore.Remove(connection.ReliableLastDelivered + 1, out var next))
            DeliverReliable(connection, next);
    }

    /// <summary>Hands a slice for the server up; proxies one for another player to that player's connection
    /// with a fresh Sequence and the rest of the id unchanged (spec §8, ClientToClientWithServerProxy).</summary>
    private void DeliverReliable(FusionRelayConnection connection, ReliableSlice slice)
    {
        connection.ReliableLastDelivered = slice.Id.Sequence;
        if (slice.Id.Target == ServerPlayerId)
        {
            ReliableReceived?.Invoke(connection, slice.Id, slice.Data);
            return;
        }

        var target = _byActor.Values.FirstOrDefault(c => c.PlayerId == slice.Id.Target && c.Status == NetConnectionStatus.Connected);
        if (target is null || target.ReliableQueue.Count >= MaxForwardQueue)
        {
            Log.Debug(Cat, $"actor {connection.Actor} reliable slice for player {slice.Id.Target} dropped: " +
                           (target is null ? "no such connection" : "its queue is full"));
            return;
        }
        target.ReliableQueue.AddLast(slice with { Id = slice.Id with { Sequence = ++target.ReliableSendSequence } });
    }

    private void Refuse(int actor, string why)
    {
        Log.Debug(Cat, $"actor {actor} refused: {why}");
        _send(actor, FusionRelayCodec.EncodeRefused(NetConnectFailedReason.ServerRefused), false);
    }

    private void SendAccepted(FusionRelayConnection connection) =>
        _send(connection.Actor, FusionRelayCodec.EncodeAccepted(connection.LocalId, connection.RemoteId, connection.Counter), false);

    private FusionRelayConnection AllocateSlot(int actor, int playerId, ConnectCommand connect)
    {
        var index = _slots.IndexOf(null);
        if (index < 0)
        {
            index = _slots.Count;
            _slots.Add(null);
            _slotGenerations.Add(0);
        }
        // Generation starts at 1 and is bumped on every reuse, never 0 (spec §5.3).
        var generation = _slotGenerations[index] + 1;
        if (generation == 0) generation = 1;
        _slotGenerations[index] = generation;

        var connection = new FusionRelayConnection(actor, playerId, connect.UniqueId, connect.Token,
            new NetConnectionId(0, (short)index, generation));
        _slots[index] = connection;
        _occupiedSlots++;
        _byActor[actor] = connection;
        _byUniqueId[connect.UniqueId] = connection;
        return connection;
    }

    /// <summary>The per-transport Counter (spec §5.3): first connection 1, +1 per accepted connection, never
    /// reset. A Shared client builds its NetworkIds as <c>Counter &lt;&lt; 19</c>, so only the low 13 bits
    /// matter: a value whose low bits are 0 (the server's own id range) or belong to a live connection is
    /// skipped, so a long-lived room never hands two connections one id namespace.</summary>
    private uint NextCounter()
    {
        const uint idBits = 0x1FFF;
        var inUse = _slots.Where(c => c is not null).Select(c => c!.Counter & idBits).ToHashSet();
        do _counter++;
        while ((_counter & idBits) == 0 || inUse.Contains(_counter & idBits));
        return _counter;
    }
}
