using System.Buffers.Binary;
using OpenPak.Photon;
using OpenPak.Server.LoadBalancing.Fusion;

namespace OpenPak.Server.Tests;

/// <summary>
/// The server side of Fusion's relay transport (<see cref="FusionRelayPeer"/>) against
/// docs/protocol/07-fusion-relay-transport.md: handshake (§5), notify channel (§6), fragmentation (§7),
/// reliable channel (§8), keepalive/timeout under a fake clock, and malformed input.
/// </summary>
public class FusionRelayPeerTests
{
    private readonly List<(int Actor, byte[] Data, bool Reliable)> _sent = new();
    private readonly List<(int Actor, object? Token, bool Delivered)> _resolved = new();
    private readonly List<(int Actor, byte[] Payload)> _notifies = new();
    private readonly List<(int Actor, ReliableId Id, byte[] Data)> _reliables = new();
    private readonly List<(int Actor, NetDisconnectReason Reason)> _disconnects = new();
    private int _connectedEvents;

    private FusionRelayPeer NewPeer(int maxConnections = 4)
    {
        var peer = new FusionRelayPeer(maxConnections, (actor, data, reliable) => _sent.Add((actor, data, reliable)));
        peer.Connected += _ => _connectedEvents++;
        peer.Disconnected += (c, reason) => _disconnects.Add((c.Actor, reason));
        peer.NotifyDelivered += (c, token) => _resolved.Add((c.Actor, token, true));
        peer.NotifyLost += (c, token) => _resolved.Add((c.Actor, token, false));
        peer.NotifyReceived += (c, reader) => _notifies.Add((c.Actor, reader.ReadBytesAligned(reader.BitsRemaining / 8)));
        peer.ReliableReceived += (c, id, data) => _reliables.Add((c.Actor, id, data));
        return peer;
    }

    private static ulong UniqueIdOf(int actor) => BinaryPrimitives.ReadUInt64LittleEndian(FusionProtocol.UniqueIdForActor(actor));

    private static byte[] ConnectFrom(int actor, ulong? uniqueId = null) =>
        FusionRelayCodec.EncodeConnect(new NetConnectionId(0, 0, 1), [], uniqueId ?? UniqueIdOf(actor));

    /// <summary>Registers <paramref name="actor"/> (player id = actor) and completes its handshake.</summary>
    private FusionRelayConnection Connect(FusionRelayPeer peer, int actor)
    {
        peer.Register(actor, FusionProtocol.UniqueIdForActor(actor), playerId: actor);
        peer.Receive(actor, ConnectFrom(actor));
        _sent.Clear();
        return peer.ConnectionOf(actor)!;
    }

    private static byte[] Notify(NetPacketType type, ushort sequence, ushort ackSequence = 0, ulong ackMask = 0, byte[]? payload = null, byte fragment = 0)
    {
        payload ??= [0x00];
        var datagram = new byte[FusionRelayCodec.NotifyHeaderSize + payload.Length];
        FusionRelayCodec.WriteNotifyHeader(datagram, new NotifyHeader(type, fragment, sequence, ackSequence, ackMask));
        payload.CopyTo(datagram, FusionRelayCodec.NotifyHeaderSize);
        return datagram;
    }

    private static NetBitWriter Payload(int bytes, byte fill = 0xAB)
    {
        var w = new NetBitWriter();
        w.WriteBytesAligned(Enumerable.Repeat(fill, bytes).ToArray());
        return w;
    }

    private static NotifyHeader HeaderOf(byte[] datagram)
    {
        Assert.True(FusionRelayCodec.TryReadNotifyHeader(datagram, out var header));
        return header;
    }

    // ---- Handshake (§5) ----

    [Fact]
    public void Connect_before_registration_gets_no_reply_then_accepted_once_registered()
    {
        var peer = NewPeer();
        peer.Receive(1, ConnectFrom(1));
        Assert.Empty(_sent);   // Waiting: no reply, no state
        Assert.Null(peer.ConnectionOf(1));

        peer.Register(1, FusionProtocol.UniqueIdForActor(1), playerId: 1);
        peer.Receive(1, ConnectFrom(1));

        var (actor, data, reliable) = Assert.Single(_sent);
        Assert.Equal((1, false), (actor, reliable));
        Assert.True(FusionRelayCodec.TryDecodeAccepted(data, out var local, out var remote, out var counter));
        Assert.Equal(new NetConnectionId(0, 0, 1), local);
        Assert.Equal(new NetConnectionId(0, 0, 1), remote);   // the client's own id echoed
        Assert.Equal(1u, counter);
        Assert.Equal(1, _connectedEvents);
        Assert.Equal(NetConnectionStatus.Connected, peer.ConnectionOf(1)!.Status);
    }

    [Fact]
    public void Duplicate_connect_resends_the_identical_accepted()
    {
        var peer = NewPeer();
        peer.Register(1, FusionProtocol.UniqueIdForActor(1), 1);
        peer.Receive(1, ConnectFrom(1));
        peer.Receive(1, ConnectFrom(1));

        Assert.Equal(2, _sent.Count);
        Assert.Equal(_sent[0].Data, _sent[1].Data);
        Assert.Equal(1, _connectedEvents);
    }

    [Fact]
    public void Each_connection_gets_its_own_counter_and_slot()
    {
        var peer = NewPeer();
        var first = Connect(peer, 1);
        var second = Connect(peer, 2);

        Assert.Equal((1u, (short)0), (first.Counter, first.LocalId.GroupIndex));
        Assert.Equal((2u, (short)1), (second.Counter, second.LocalId.GroupIndex));
    }

    [Fact]
    public void Full_server_refuses_with_server_full()
    {
        var peer = NewPeer(maxConnections: 1);
        Connect(peer, 1);
        peer.Register(2, FusionProtocol.UniqueIdForActor(2), 2);
        peer.Receive(2, ConnectFrom(2));

        Assert.Equal("010302", Convert.ToHexString(Assert.Single(_sent).Data));
        Assert.Null(peer.ConnectionOf(2));
    }

    [Fact]
    public void A_unique_id_not_issued_to_this_actor_is_refused()
    {
        var peer = NewPeer();
        peer.Register(1, FusionProtocol.UniqueIdForActor(1), 1);
        peer.Register(2, FusionProtocol.UniqueIdForActor(2), 2);

        peer.Receive(2, ConnectFrom(2, UniqueIdOf(1)));   // actor 2 presents actor 1's id
        peer.Receive(3, ConnectFrom(3, UniqueIdOf(1)));   // unregistered actor presents actor 1's id

        Assert.All(_sent, s => Assert.Equal("010303", Convert.ToHexString(s.Data)));
        Assert.Equal(2, _sent.Count);
        Assert.Null(peer.ConnectionOf(2));
    }

    [Fact]
    public void Datagrams_other_than_connect_from_an_unknown_actor_are_dropped()
    {
        var peer = NewPeer();
        peer.Receive(5, Notify(NetPacketType.NotifyData, 1));
        peer.Receive(5, FusionRelayCodec.EncodeDisconnect(NetDisconnectReason.Requested));
        Assert.Empty(_sent);
        Assert.Empty(_notifies);
    }

    [Fact]
    public void Requested_disconnect_sends_twice_then_the_slot_is_held_for_the_shutdown_time()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        peer.Disconnect(connection, [0x42]);

        Assert.True(FusionRelayCodec.TryDecodeDisconnect(Assert.Single(_sent).Data, out var reason, out var token));
        Assert.Equal(NetDisconnectReason.Requested, reason);
        Assert.Equal(new byte[] { 0x42 }, token);
        Assert.Equal((1, NetDisconnectReason.Requested), Assert.Single(_disconnects));

        _sent.Clear();
        peer.Update(0.0);   // → Shutdown: second Disconnect, without the token
        Assert.True(FusionRelayCodec.TryDecodeDisconnect(Assert.Single(_sent).Data, out reason, out token));
        Assert.Equal(NetDisconnectReason.Requested, reason);
        Assert.Empty(token);
        Assert.Equal(NetConnectionStatus.Shutdown, connection.Status);

        // During the shutdown second a fresh Connect finds the dying slot and is ignored.
        _sent.Clear();
        peer.Update(0.5);
        peer.Receive(1, ConnectFrom(1));
        Assert.Empty(_sent);

        peer.Update(1.0);   // released
        peer.Receive(1, ConnectFrom(1));
        Assert.True(FusionRelayCodec.TryDecodeAccepted(Assert.Single(_sent).Data, out var local, out _, out var counter));
        Assert.Equal(new NetConnectionId(0, 0, 2), local);   // same slot, next generation
        Assert.Equal(2u, counter);                           // the Counter is never reused
    }

    [Fact]
    public void Peer_disconnect_is_echoed_with_the_peers_reason()
    {
        var peer = NewPeer();
        Connect(peer, 1);
        peer.Receive(1, FusionRelayCodec.EncodeDisconnect(NetDisconnectReason.Timeout));

        Assert.True(FusionRelayCodec.TryDecodeDisconnect(Assert.Single(_sent).Data, out var reason, out _));
        Assert.Equal(NetDisconnectReason.Timeout, reason);
        Assert.Equal((1, NetDisconnectReason.Timeout), Assert.Single(_disconnects));
    }

    [Fact]
    public void Unregister_disconnects_the_actor()
    {
        var peer = NewPeer();
        Connect(peer, 1);
        peer.Unregister(1);
        Assert.Equal((1, NetDisconnectReason.Requested), Assert.Single(_disconnects));
    }

    // ---- Notify channel (§6) ----

    [Fact]
    public void Notify_payload_is_handed_up_from_bit_112_and_acked_on_the_next_send()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        peer.Receive(1, Notify(NetPacketType.NotifyData, 1, payload: [0x11, 0x22]));

        Assert.Equal(new byte[] { 0x11, 0x22 }, Assert.Single(_notifies).Payload);
        Assert.True(peer.TrySendNotify(connection, Payload(1, 0x00), "t1"));
        var datagram = Assert.Single(_sent).Data;
        Assert.Equal("030001000100" + "0100000000000000" + "00", Convert.ToHexString(datagram));
    }

    [Fact]
    public void Older_and_duplicate_packets_are_dropped_and_empty_ones_ignored()
    {
        var peer = NewPeer();
        Connect(peer, 1);
        peer.Receive(1, Notify(NetPacketType.NotifyData, 2, payload: [2]));
        peer.Receive(1, Notify(NetPacketType.NotifyData, 1, payload: [1]));   // reordered: discarded
        peer.Receive(1, Notify(NetPacketType.NotifyData, 2, payload: [2]));   // duplicate
        peer.Receive(1, Notify(NetPacketType.NotifyData, 3, payload: [])); // header only: dropped before acks
        Assert.Single(_notifies);
        Assert.Equal((ushort)2, peer.ConnectionOf(1)!.RecvSequence);
        Assert.Equal(1UL, peer.ConnectionOf(1)!.RecvMask);
    }

    [Fact]
    public void Acks_resolve_envelopes_oldest_first_into_delivered_and_lost()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        for (var i = 1; i <= 5; i++) Assert.True(peer.TrySendNotify(connection, Payload(1), i));

        peer.Update(0.03);
        // Client got 5, 3 and 1: AckSequence 5, mask bits 0, 2, 4.
        peer.Receive(1, FusionRelayCodec.EncodeNotifyAcks(5, 0b10101));

        Assert.Equal(new (int, object?, bool)[] { (1, 1, true), (1, 2, false), (1, 3, true), (1, 4, false), (1, 5, true) }, _resolved);
        Assert.Equal(0.03, connection.Rtt, 6);   // exact ack of a pending envelope
        Assert.Empty(connection.SendWindow);
    }

    [Fact]
    public void An_ack_that_has_not_reached_an_envelope_leaves_it_pending()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        peer.TrySendNotify(connection, Payload(1), "a");
        peer.TrySendNotify(connection, Payload(1), "b");
        peer.Receive(1, FusionRelayCodec.EncodeNotifyAcks(1, 1));

        Assert.Equal((1, (object?)"a", true), Assert.Single(_resolved));
        Assert.Single(connection.SendWindow);
    }

    [Fact]
    public void Envelopes_64_or_more_behind_the_ack_are_lost_even_with_mask_bits_set()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        for (var i = 1; i <= 65; i++) peer.TrySendNotify(connection, Payload(1), i);
        peer.Receive(1, FusionRelayCodec.EncodeNotifyAcks(65, ulong.MaxValue));

        Assert.Equal((1, (object?)1, false), _resolved[0]);   // 64 behind: outside the mask
        Assert.All(_resolved.Skip(1), r => Assert.True(r.Delivered));
    }

    [Fact]
    public void Send_sequence_and_acks_wrap_at_16_bits()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        connection.SendSequence = 65533;
        foreach (var token in new[] { "65534", "65535", "0", "1" }) peer.TrySendNotify(connection, Payload(1), token);
        Assert.Equal(new ushort[] { 65534, 65535, 0, 1 }, _sent.Select(s => HeaderOf(s.Data).Sequence));

        // Ack 1 with 1, 0 and 65534 received (bits 0, 1, 3); 65535 missing.
        peer.Receive(1, FusionRelayCodec.EncodeNotifyAcks(1, 0b1011));
        Assert.Equal(new (int, object?, bool)[] { (1, "65534", true), (1, "65535", false), (1, "0", true), (1, "1", true) }, _resolved);
    }

    [Fact]
    public void Receive_sequence_wraps_at_16_bits()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        connection.RecvSequence = 65534;
        connection.RecvMask = 1;
        peer.Receive(1, Notify(NetPacketType.NotifyData, 2));   // dist16(2, 65534) = +4

        Assert.Single(_notifies);
        Assert.Equal((ushort)2, connection.RecvSequence);
        Assert.Equal(0b10001UL, connection.RecvMask);
    }

    [Fact]
    public void A_gap_of_64_or_more_clears_the_receive_history()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        peer.Receive(1, Notify(NetPacketType.NotifyData, 1));
        peer.Receive(1, Notify(NetPacketType.NotifyData, 65));
        Assert.Equal(1UL, connection.RecvMask);
    }

    [Theory]
    [InlineData(2048, false)]
    [InlineData(2049, true)]
    public void A_sequence_jump_beyond_2048_disconnects(int ahead, bool disconnects)
    {
        var peer = NewPeer();
        Connect(peer, 1);
        peer.Receive(1, Notify(NetPacketType.NotifyData, (ushort)ahead));

        Assert.Equal(disconnects, _disconnects.Count == 1);
        if (!disconnects) return;
        Assert.Equal(NetDisconnectReason.SequenceOutOfBounds, _disconnects[0].Reason);
        Assert.True(FusionRelayCodec.TryDecodeDisconnect(Assert.Single(_sent).Data, out var reason, out _));
        Assert.Equal(NetDisconnectReason.SequenceOutOfBounds, reason);
    }

    [Theory]
    [InlineData(2048, false)]
    [InlineData(2049, true)]
    public void A_sequence_jump_beyond_2048_backwards_disconnects(int behind, bool disconnects)
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        connection.RecvSequence = 10;   // so the backward jump also crosses the 16-bit wrap
        peer.Receive(1, Notify(NetPacketType.NotifyData, (ushort)(10 - behind)));

        Assert.Equal(disconnects, _disconnects.Count == 1);
        Assert.Empty(_notifies);   // older packets are never accepted either way
    }

    [Fact]
    public void Send_window_holds_128_packets_until_acked()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        for (var i = 0; i < 128; i++) Assert.True(peer.TrySendNotify(connection, Payload(1), i));
        Assert.False(connection.CanSendNotify);
        Assert.False(peer.TrySendNotify(connection, Payload(1), 128));
        Assert.Empty(_disconnects);   // a stall, not a disconnect

        peer.Receive(1, FusionRelayCodec.EncodeNotifyAcks(1, 1));
        Assert.True(peer.TrySendNotify(connection, Payload(1), 128));
    }

    [Fact]
    public void Empty_or_oversized_payloads_are_rejected()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        Assert.Throws<ArgumentException>(() => peer.TrySendNotify(connection, new NetBitWriter()));
        Assert.Throws<ArgumentException>(() => peer.TrySendNotify(connection, Payload(FusionRelayCodec.MaxNotifyPayloadBytes + 1)));
    }

    // ---- Fragmentation (§7) ----

    [Theory]
    [InlineData(1266, new byte[] { 0x00 })]                   // 14 + 1266 = 1280: still one datagram
    [InlineData(1267, new byte[] { 0x01, 0x82 })]             // one byte over: 1122 + 145
    [InlineData(2244, new byte[] { 0x01, 0x82 })]             // exactly two chunks
    [InlineData(2245, new byte[] { 0x01, 0x02, 0x83 })]
    public void Large_notify_packets_are_split_into_reliable_pieces(int payloadBytes, byte[] fragments)
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        Assert.True(peer.TrySendNotify(connection, Payload(payloadBytes), "big"));

        Assert.Equal(fragments, _sent.Select(s => HeaderOf(s.Data).Fragment));
        Assert.All(_sent, s => Assert.Equal(fragments.Length > 1, s.Reliable));
        Assert.All(_sent, s => Assert.Equal((ushort)1, HeaderOf(s.Data).Sequence));   // one sequence for all pieces
        Assert.All(_sent, s => Assert.True(s.Data.Length <= FusionRelayCodec.MaxDatagramBytes));
        Assert.Equal(payloadBytes, _sent.Sum(s => s.Data.Length - FusionRelayCodec.NotifyHeaderSize));
        Assert.Single(connection.SendWindow);   // and one envelope
    }

    [Fact]
    public void The_largest_payload_is_forty_pieces()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        peer.TrySendNotify(connection, Payload(FusionRelayCodec.MaxNotifyPayloadBytes));
        Assert.Equal(40, _sent.Count);
        Assert.Equal(0x80 | 40, HeaderOf(_sent[^1].Data).Fragment);
    }

    /// <summary>A client's fragmented NotifyData: the payload cut into <paramref name="chunk"/>-byte pieces.</summary>
    private static List<byte[]> Pieces(ushort sequence, byte[] payload, int chunk = FusionRelayCodec.FragmentChunkBytes, ushort ackSequence = 0, ulong ackMask = 0)
    {
        var pieces = payload.Chunk(chunk).ToList();
        return pieces.Select((piece, i) => Notify(NetPacketType.NotifyData, sequence, ackSequence, ackMask, piece,
            (byte)((i + 1) | (i == pieces.Count - 1 ? 0x80 : 0)))).ToList();
    }

    [Fact]
    public void Fragments_are_reassembled_and_their_acks_processed_on_the_last_piece()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        peer.TrySendNotify(connection, Payload(1), "ours");
        var payload = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        var pieces = Pieces(1, payload, ackSequence: 1, ackMask: 1);

        peer.Receive(1, pieces[0]);
        peer.Receive(1, pieces[1]);
        Assert.Empty(_notifies);
        Assert.Empty(_resolved);
        peer.Receive(1, pieces[2]);

        Assert.Equal(payload, Assert.Single(_notifies).Payload);
        Assert.Equal((1, (object?)"ours", true), Assert.Single(_resolved));
        Assert.Equal((ushort)1, connection.RecvSequence);
    }

    [Fact]
    public void A_missing_or_overtaken_piece_drops_the_whole_packet()
    {
        var peer = NewPeer();
        Connect(peer, 1);
        var missing = Pieces(1, new byte[3000]);
        peer.Receive(1, missing[0]);
        peer.Receive(1, missing[2]);   // piece 2 never came

        var overtaken = Pieces(2, new byte[3000]);
        peer.Receive(1, overtaken[0]);
        peer.Receive(1, Notify(NetPacketType.NotifyData, 3, payload: [7]));   // a newer packet is accepted
        peer.Receive(1, overtaken[1]);
        peer.Receive(1, overtaken[2]);

        Assert.Equal(new byte[] { 7 }, Assert.Single(_notifies).Payload);
    }

    [Fact]
    public void Reassembly_beyond_51200_bytes_is_discarded()
    {
        var peer = NewPeer();
        Connect(peer, 1);
        foreach (var piece in Pieces(1, new byte[60_000], chunk: 30_000)) peer.Receive(1, piece);
        Assert.Empty(_notifies);

        foreach (var piece in Pieces(1, new byte[50_000], chunk: 25_000)) peer.Receive(1, piece);   // the receiver does not care about piece size
        Assert.Equal(50_000, Assert.Single(_notifies).Payload.Length);
    }

    // ---- Reliable channel (§8) ----

    private static readonly byte[] Key = Enumerable.Range(0, 16).Select(i => (byte)(0xC0 + i)).ToArray();

    private static byte[] ReliableDatagram(ushort notifySequence, ReliableId id, byte[] data)
    {
        var payload = new byte[FusionRelayCodec.ReliableIdSize + data.Length];
        FusionRelayCodec.WriteReliableId(payload, id);
        data.CopyTo(payload, FusionRelayCodec.ReliableIdSize);
        return Notify(NetPacketType.NotifyReliableData, notifySequence, payload: payload);
    }

    private static ReliableId ReliableIdOf(byte[] datagram)
    {
        Assert.True(FusionRelayCodec.TryReadReliableId(datagram.AsSpan(FusionRelayCodec.NotifyHeaderSize), out var id));
        return id;
    }

    private List<byte[]> SentReliable(int actor) =>
        _sent.Where(s => s.Actor == actor && s.Data[0] == (byte)NetPacketType.NotifyReliableData).Select(s => s.Data).ToList();

    [Fact]
    public void Reliable_messages_are_sliced_and_drained_one_slice_per_50ms()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        Assert.True(peer.SendReliable(connection, Key, new byte[2500]));

        peer.Update(0.00);
        peer.Update(0.01);
        Assert.Single(SentReliable(1));
        peer.Update(0.05);
        peer.Update(0.10);
        peer.Update(0.15);   // nothing left

        var slices = SentReliable(1);
        Assert.Equal(new uint[] { 1, 2, 3 }, slices.Select(s => ReliableIdOf(s).Sequence));
        Assert.Equal(new[] { 1088, 1088, 324 }, slices.Select(s => ReliableIdOf(s).SliceLength));
        Assert.Equal(new[] { 1150, 1150, 386 }, slices.Select(s => s.Length));
        var first = ReliableIdOf(slices[0]);
        Assert.Equal((2500, -1, 1, 1), (first.TotalLength, first.Source, first.SourceSend, first.Target));
        Assert.Equal(Key, first.Key);
        Assert.All(slices, s => Assert.Equal(0, HeaderOf(s).Fragment));
    }

    [Fact]
    public void A_lost_reliable_slice_is_resent_first_with_a_new_notify_sequence()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        peer.SendReliable(connection, Key, new byte[1088 * 3]);
        peer.Update(0.00);   // reliable 1 on notify 1
        peer.Update(0.05);   // reliable 2 on notify 2

        peer.Receive(1, FusionRelayCodec.EncodeNotifyAcks(2, 0b01));   // 2 arrived, 1 lost
        peer.Update(0.10);
        peer.Update(0.20);

        var slices = SentReliable(1);
        Assert.Equal(new uint[] { 1, 2, 1, 3 }, slices.Select(s => ReliableIdOf(s).Sequence));
        Assert.Equal(new ushort[] { 1, 2, 3, 4 }, slices.Select(s => HeaderOf(s).Sequence));
        Assert.Empty(_resolved);   // reliable envelopes never surface as notify delivered/lost
    }

    [Fact]
    public void Reliable_slices_are_delivered_in_order_and_resends_dropped()
    {
        var peer = NewPeer();
        Connect(peer, 1);
        ReliableId Slice(uint sequence) => new(sequence, 3, 6, 1, 1, -1, Key);

        peer.Receive(1, ReliableDatagram(1, Slice(2), [4, 5, 6]));
        Assert.Empty(_reliables);
        peer.Receive(1, ReliableDatagram(2, Slice(1), [1, 2, 3]));
        peer.Receive(1, ReliableDatagram(3, Slice(1), [1, 2, 3]));   // resent after a lost ack

        Assert.Equal(new uint[] { 1, 2 }, _reliables.Select(r => r.Id.Sequence));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, _reliables.SelectMany(r => r.Data));
        Assert.All(_reliables, r => Assert.Equal(3, r.Id.SliceLength));
    }

    [Fact]
    public void A_slice_for_another_player_is_proxied_with_that_connections_sequence()
    {
        var peer = NewPeer();
        Connect(peer, 1);
        var target = Connect(peer, 2);
        target.ReliableSendSequence = 40;

        peer.Receive(1, ReliableDatagram(1, new ReliableId(1, 3, 3, 1, 5, 2, Key), [9, 8, 7]));
        peer.Receive(1, ReliableDatagram(2, new ReliableId(2, 1, 1, 1, 6, 99, Key), [1]));   // no such player: dropped
        peer.Update(0);
        peer.Update(0.05);

        Assert.Empty(_reliables);
        var forwarded = Assert.Single(SentReliable(2));
        var id = ReliableIdOf(forwarded);
        Assert.Equal((41u, 3, 3, 1, 5, 2), (id.Sequence, id.SliceLength, id.TotalLength, id.Source, id.SourceSend, id.Target));
        Assert.Equal(new byte[] { 9, 8, 7 }, forwarded[^3..]);
        Assert.Empty(SentReliable(1));
    }

    [Fact]
    public void A_slice_naming_another_source_misstating_its_length_or_over_1088_bytes_is_dropped()
    {
        var peer = NewPeer();
        Connect(peer, 1);
        Connect(peer, 2);
        Connect(peer, 3);

        peer.Receive(1, ReliableDatagram(1, new ReliableId(1, 1, 1, 3, 5, 2, Key), [7]));   // poses as player 3
        peer.Receive(1, ReliableDatagram(2, new ReliableId(1, 1089, 1089, 1, 6, 2, Key), new byte[1089]));
        peer.Receive(1, ReliableDatagram(3, new ReliableId(1, 5, 5, 1, 6, 2, Key), [7]));   // claims 5 bytes, carries 1
        peer.Receive(1, ReliableDatagram(4, new ReliableId(1, 1, 1, 1, 7, 2, Key), [8]));   // the genuine one
        peer.Update(0);
        peer.Update(0.05);
        peer.Update(0.1);

        var forwarded = SentReliable(2);
        var id = ReliableIdOf(Assert.Single(forwarded));
        Assert.Equal((1, 7), (id.Source, id.SourceSend));
        Assert.Equal(1UL, peer.ConnectionOf(1)!.RecvMask);   // the dropped ones were never acknowledged
    }

    [Fact]
    public void Oversize_unfragmented_datagrams_and_fragmented_reliable_slices_are_dropped()
    {
        var peer = NewPeer();
        Connect(peer, 1);
        peer.Receive(1, Notify(NetPacketType.NotifyData, 1, payload: new byte[FusionRelayCodec.MaxDatagramBytes]));
        peer.Receive(1, ReliableDatagram(2, new ReliableId(1, 1, 1, 1, 1, -1, Key), [1]) is var slice ? Fragment(slice, 0x81) : null!);
        Assert.Empty(_notifies);
        Assert.Empty(_reliables);

        peer.Receive(1, Notify(NetPacketType.NotifyData, 3, payload: new byte[FusionRelayCodec.MaxDatagramBytes - FusionRelayCodec.NotifyHeaderSize]));
        Assert.Single(_notifies);   // exactly 1280 is fine
    }

    private static byte[] Fragment(byte[] datagram, byte fragment)
    {
        var copy = datagram.ToArray();
        Assert.True(FusionRelayCodec.TryReadNotifyHeader(copy, out var header));
        FusionRelayCodec.WriteNotifyHeader(copy, header with { Fragment = fragment });
        return copy;
    }

    [Fact]
    public void Counters_skip_the_server_id_range_and_any_namespace_still_in_use()
    {
        var peer = NewPeer(maxConnections: 4);
        var longLived = Connect(peer, 1);
        Assert.Equal(1u, longLived.Counter);
        var now = 0.0;
        // Reconnect a second player 8200 times: its counter wraps the 13 bits a client id carries.
        var seen = new List<uint>();
        for (var i = 0; i < 8200; i++)
        {
            var connection = Connect(peer, 2);
            seen.Add(connection.Counter & 0x1FFF);
            peer.Unregister(2);
            peer.Update(now += 1);
            peer.Update(now += 2);   // shutdown time passes: the slot is released
            longLived.LastRecv = now;
        }
        Assert.DoesNotContain(0u, seen);
        Assert.DoesNotContain(1u, seen);   // still player 1's namespace
    }

    // ---- Keepalive and timeout (§6.4) ----

    private List<byte[]> SentAcks() => _sent.Where(s => s.Data[0] == (byte)NetPacketType.NotifyAcks).Select(s => s.Data).ToList();

    [Fact]
    public void An_idle_connection_gets_an_ack_only_packet_every_100ms()
    {
        var peer = NewPeer();
        Connect(peer, 1);   // at t = 0
        var acksAt = new List<double>();
        foreach (var now in new[] { 0.05, 0.10, 0.11, 0.15, 0.21, 0.22, 0.30, 0.33 })
        {
            var before = SentAcks().Count;
            peer.Update(now);
            if (SentAcks().Count > before) acksAt.Add(now);
        }
        Assert.Equal(new[] { 0.11, 0.22, 0.33 }, acksAt);
        Assert.All(SentAcks(), a => Assert.Equal(14, a.Length));
    }

    [Fact]
    public void Eight_unacknowledged_receipts_force_an_ack_and_a_data_send_resets_the_count()
    {
        var peer = NewPeer();
        var connection = Connect(peer, 1);
        for (ushort s = 1; s <= 7; s++) peer.Receive(1, Notify(NetPacketType.NotifyData, s));
        peer.Update(0.01);
        Assert.Empty(SentAcks());

        peer.Receive(1, Notify(NetPacketType.NotifyData, 8));
        peer.Update(0.02);
        Assert.Equal("040000000800" + "FF00000000000000", Convert.ToHexString(Assert.Single(SentAcks())));

        for (ushort s = 9; s <= 16; s++) peer.Receive(1, Notify(NetPacketType.NotifyData, s));
        peer.TrySendNotify(connection, Payload(1));   // the acks ride on this instead
        peer.Update(0.03);
        Assert.Single(SentAcks());
    }

    [Fact]
    public void Silence_beyond_the_connection_timeout_disconnects()
    {
        var peer = NewPeer();
        Connect(peer, 1);
        peer.Update(5.0);
        peer.Receive(1, FusionRelayCodec.EncodeNotifyAcks(0, 0));   // any datagram counts as life
        peer.Update(15.0);
        Assert.Empty(_disconnects);

        peer.Update(15.01);
        Assert.Equal((1, NetDisconnectReason.Timeout), Assert.Single(_disconnects));
        Assert.True(FusionRelayCodec.TryDecodeDisconnect(_sent[^1].Data, out var reason, out _));
        Assert.Equal(NetDisconnectReason.Timeout, reason);
    }

    // ---- Malformed input (trust boundary) ----

    [Fact]
    public void Truncated_and_random_datagrams_never_throw()
    {
        var peer = NewPeer();
        Connect(peer, 1);
        peer.Register(2, FusionProtocol.UniqueIdForActor(2), 2);
        var valid = new List<byte[]>
        {
            ConnectFrom(1), ConnectFrom(2), FusionRelayCodec.EncodeDisconnect(NetDisconnectReason.Timeout, [1, 2]),
            FusionRelayCodec.EncodeNotifyAcks(3, 7), Notify(NetPacketType.NotifyData, 1, payload: new byte[40]),
            ReliableDatagram(2, new ReliableId(1, 4, 4, 1, 1, -1, Key), [1, 2, 3, 4]),
        };
        valid.AddRange(Pieces(3, new byte[3000]));
        foreach (var datagram in valid)
            for (var length = 0; length <= datagram.Length; length++)
                foreach (var actor in new[] { 1, 2, 3 })
                    peer.Receive(actor, datagram.AsSpan(0, length));

        var random = new Random(1402);
        var now = 0.0;
        for (var i = 0; i < 20_000; i++)
        {
            var datagram = new byte[random.Next(0, 160)];
            random.NextBytes(datagram);
            if (datagram.Length > 0) datagram[0] = (byte)random.Next(0, 10);   // mostly real packet types
            if (datagram.Length > 1 && datagram[0] == 1) datagram[1] = (byte)random.Next(0, 7);
            peer.Receive(random.Next(0, 4), datagram);
            if (i % 50 == 0) peer.Update(now += 0.02);
            if (peer.ConnectionOf(1) is null && i % 500 == 0) peer.Receive(1, ConnectFrom(1));
        }
    }
}
