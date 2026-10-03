using OpenPak.Photon;
using OpenPak.Server.LoadBalancing.Fusion;

namespace OpenPak.Server.Tests;

/// <summary>
/// The clean-room Shared-mode simulation (<see cref="FusionSharedRoom"/>) against
/// docs/protocol/08-fusion-shared-simulation.md, driven by scripted byte-level clients over the real
/// <see cref="FusionRelayPeer"/> under a fake clock.
/// </summary>
public class FusionSharedRoomTests
{
    private const double Round = 1.0 / FusionSharedRoom.ServerSendRate;
    private double _now;
    private readonly Dictionary<int, FusionSimTestClient> _clients = new();
    private readonly List<(int Actor, byte[] Data, bool Reliable)> _sent = new();

    private FusionSharedRoom NewRoom(bool sceneAuthorityMaster = false) =>
        new("room", (actor, data, reliable) =>
        {
            _sent.Add((actor, data, reliable));
            if (_clients.TryGetValue(actor, out var client)) client.Receive(data);
        }, _now, new FusionRoomOptions { SceneAuthorityMasterClient = sceneAuthorityMaster });

    private FusionSimTestClient Join(FusionSharedRoom room, int actor)
    {
        var client = new FusionSimTestClient(actor);
        _clients[actor] = client;
        room.Register(actor, FusionProtocol.UniqueIdForActor(actor));
        room.Receive(actor, client.Connect(), _now);
        Step(room);
        return client;
    }

    /// <summary>Runs <paramref name="rounds"/> send rounds; every client answers each with an empty packet (its acks).</summary>
    private void Step(FusionSharedRoom room, int rounds = 1)
    {
        for (var i = 0; i < rounds; i++)
        {
            _now += Round;
            room.Tick(_now);
            foreach (var client in _clients.Values.ToArray()) Send(room, client, client.Packet());
        }
    }

    private void Send(FusionSharedRoom room, FusionSimTestClient client, byte[] datagram)
    {
        foreach (var piece in FusionSimTestClient.OnTheWire(datagram)) room.Receive(client.Actor, piece, _now);
    }

    private static uint IdOf(FusionSimTestClient client, uint n) => client.Counter << 19 | n;

    private static SimMessage Rpc(uint objectId, int argument, int target = -1)
    {
        var payload = new byte[12];
        BitConverter.TryWriteBytes(payload.AsSpan(0), objectId);
        BitConverter.TryWriteBytes(payload.AsSpan(6), (ushort)1);
        BitConverter.TryWriteBytes(payload.AsSpan(8), argument);
        return new SimMessage { BitLength = 96, Payload = payload, Target = target, Flags = target >= 0 ? SimMessageFlags.TargetPlayer : 0 };
    }

    private static SimMessage Internal(int type, params int[] fields)
    {
        var payload = new byte[4 + 4 * fields.Length];
        BitConverter.TryWriteBytes(payload, type);
        for (var i = 0; i < fields.Length; i++) BitConverter.TryWriteBytes(payload.AsSpan(4 + 4 * i), fields[i]);
        return new SimMessage { BitLength = payload.Length * 8, Payload = payload, Flags = SimMessageFlags.Internal | SimMessageFlags.TargetServer };
    }

    private static List<SimMessage> MessagesOf(FusionSimTestClient client) => client.Packets.SelectMany(p => p.Messages).ToList();

    private static int ArgumentOf(SimMessage message) => BitConverter.ToInt32(message.Payload, 8);

    /// <summary>Creates an object on <paramref name="owner"/> and lets it replicate.</summary>
    private uint Spawn(FusionSharedRoom room, FusionSimTestClient owner, uint n, int flags = 0, int authority = 0, int words = 22)
    {
        var id = IdOf(owner, n);
        Send(room, owner, owner.Packet(records: [(id, FusionSimTestClient.Header(words, flags, authority == 0 ? owner.PlayerRaw : authority), [(20, 5)])]));
        Step(room, 3);
        return id;
    }

    // ---- Join, structs, master (§7, §11) ----

    [Fact]
    public void A_joiner_receives_runtime_config_scene_physics_and_its_player_data()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        Step(room, 4);

        Assert.Equal(1u, a.Counter);
        var records = a.Packets.SelectMany(p => p.Records).Where(r => r.Full).ToDictionary(r => r.Id);
        var config = records[FusionSharedRoom.RuntimeConfigId];
        Assert.Equal(29, config.Header![1]);
        Assert.Equal(32, config.Header[6]);
        Assert.Equal([(20, 64), (21, 32), (22, 64), (23, 32), (24, 1), (25, 64), (28, 2)], config.Words);
        Assert.Equal(33, records[FusionSharedRoom.SceneInfoId].Header![1]);
        Assert.Equal(0, records[FusionSharedRoom.SceneInfoId].Header![8]);
        Assert.Equal(30, records[FusionSharedRoom.PhysicsInfoId].Header![1]);

        var playerData = records[FusionSharedRoom.FirstServerId];
        Assert.Equal((23, 1, 2, 32, 0), (playerData.Header![1], playerData.Header[2], playerData.Header[3], playerData.Header[6], playerData.Header[8]));
        Assert.Equal([(20, 2), (22, 1)], playerData.Words);

        // The tick advances with the clock and a packet goes out every round, empty or not.
        var ticks = a.Packets.Select(p => p.Tick).ToList();
        Assert.True(ticks.Count >= 5);
        Assert.Equal(ticks.Order(), ticks);
        Assert.True(ticks[^1] > ticks[0]);
        Assert.All(a.Packets.Skip(2), p => Assert.Empty(p.Records));   // confirmed and unchanged: nothing resent
    }

    [Fact]
    public void Scene_authority_switch_makes_scene_and_physics_info_master_client_objects()
    {
        var room = NewRoom(sceneAuthorityMaster: true);
        var a = Join(room, 1);
        Step(room, 2);
        Assert.Equal(-1, a.LatestWord(FusionSharedRoom.SceneInfoId, 8));
        Assert.Equal(-1, a.LatestWord(FusionSharedRoom.PhysicsInfoId, 8));
    }

    [Fact]
    public void Master_client_is_the_lowest_actor_written_from_the_second_join_on()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        Step(room, 2);
        Assert.Null(a.LatestWord(FusionSharedRoom.RuntimeConfigId, 26));   // solo host: None, as live-tested

        var b = Join(room, 2);
        Step(room, 3);
        Assert.Equal(a.PlayerRaw, a.LatestWord(FusionSharedRoom.RuntimeConfigId, 26));
        Assert.Equal(a.PlayerRaw, b.LatestWord(FusionSharedRoom.RuntimeConfigId, 26));

        room.Unregister(1);
        Step(room, 3);
        Assert.Equal(b.PlayerRaw, b.LatestWord(FusionSharedRoom.RuntimeConfigId, 26));
    }

    [Fact]
    public void Packets_are_stamped_with_the_time_of_their_own_round()
    {
        var room = NewRoom();
        Join(room, 1);
        Step(room, 3);   // the client answers each round at the round's own time
        Assert.Equal(0, room.ConnectionOf(1)!.Rtt, 6);
    }

    // ---- Objects (§8, §9) ----

    [Fact]
    public void A_client_object_reaches_everyone_already_in_the_room_and_later_joiners()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var id = Spawn(room, a, 1024, flags: 4);

        var created = b.Packets.SelectMany(p => p.Records).First(r => r.Id == id);
        Assert.True(created.Full);
        Assert.Equal((22, 7, 4, a.PlayerRaw, 1), (created.Header![1], created.Header[2], created.Header[6], created.Header[8], created.Header[9]));
        Assert.Contains((20, 5), created.Words);

        Send(room, a, a.Packet(records: [(id, null, [(20, 6), (21, -3)])]));
        Step(room, 2);
        Assert.Equal(6, b.LatestWord(id, 20));
        Assert.Equal(-3, b.LatestWord(id, 21));
        Assert.False(b.Packets.SelectMany(p => p.Records).Last(r => r.Id == id).Full);   // confirmed by then

        var c = Join(room, 3);   // the join sweep
        Step(room, 2);
        Assert.Equal(6, c.LatestWord(id, 20));
        Assert.True(c.Packets.SelectMany(p => p.Records).First(r => r.Id == id).Full);
    }

    [Fact]
    public void Header_word_9_carries_interest_three_for_global_objects_one_for_the_rest()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var global = Spawn(room, a, 1024, flags: FusionObject.FlagGlobalObjectInterest);
        var local = Spawn(room, a, 1025);
        Assert.Equal(3, b.Packets.SelectMany(p => p.Records).First(r => r.Id == global).Header![9]);
        Assert.Equal(1, b.Packets.SelectMany(p => p.Records).First(r => r.Id == local).Header![9]);
        Assert.Equal(0, b.Packets.SelectMany(p => p.Records).First(r => r.Id == FusionSharedRoom.RuntimeConfigId).Header![9]);
    }

    [Fact]
    public void A_client_creates_only_in_its_own_id_range_and_under_its_own_authority()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);

        // B pre-creates A's next id under its own authority: refused, so A's spawn of that id stands.
        Send(room, b, b.Packet(records: [(IdOf(a, 1024), FusionSimTestClient.Header(22, 0, b.PlayerRaw), [(20, 66)])]));
        Step(room, 2);
        var aSpawn = Spawn(room, a, 1024);
        Assert.Equal(a.PlayerRaw, b.LatestWord(aSpawn, 8));
        Assert.Equal(5, b.LatestWord(aSpawn, 20));

        // B creates in its own range naming A, or None, as authority: B's own.
        var named = Spawn(room, b, 1024, authority: a.PlayerRaw);
        Assert.Equal(b.PlayerRaw, a.LatestWord(named, 8));
        var master = Spawn(room, b, 1025, authority: FusionObject.PlayerMasterClient);
        Assert.Equal(-1, a.LatestWord(master, 8));   // MasterClient objects are allowed
    }

    [Fact]
    public void A_lost_packet_is_resent_even_though_nothing_changed_since()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var id = Spawn(room, a, 1024);

        Send(room, a, a.Packet(records: [(id, null, [(20, 77)])]));
        b.DropNext = true;   // the packet carrying 77 never reaches B
        Step(room);
        Assert.NotEqual(77, b.LatestWord(id, 20));

        Step(room, 3);   // B's acks report the gap, the server sees the packet lost and selects the word again
        Assert.Equal(77, b.LatestWord(id, 20));
    }

    [Fact]
    public void Struct_echoes_are_discarded_and_set_player_object_is_the_only_way_to_link_an_avatar()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var avatar = Spawn(room, b, 1024);
        var bPlayerData = FusionSharedRoom.FirstServerId + 1;

        Send(room, b, b.Packet(records: [(bPlayerData, null, [(21, 999), (22, 42)]), (FusionSharedRoom.RuntimeConfigId, null, [(20, 8)])]));
        Step(room, 2);
        Assert.Null(a.LatestWord(bPlayerData, 21));
        Assert.Equal(2, a.LatestWord(bPlayerData, 22));
        Assert.Equal(64, a.LatestWord(FusionSharedRoom.RuntimeConfigId, 20));

        Send(room, b, b.Packet(messages: [Internal(6, (int)avatar)]));
        Step(room, 2);
        Assert.Equal((int)avatar, a.LatestWord(bPlayerData, 21));
        Assert.Equal((int)avatar, b.LatestWord(bPlayerData, 21));
    }

    [Fact]
    public void The_transform_block_is_kept_in_wire_form_and_reemitted_verbatim()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var id = IdOf(a, 1024);
        var header = FusionSimTestClient.Header(40, flags: 8, a.PlayerRaw);
        Send(room, a, a.Notify(w =>
        {
            w.WriteBits(0, 64);
            w.WriteUInt32VarLength(FusionSimCodec.ZigZag32((int)id), 6);
            w.WriteBoolean(true);
            for (var i = 1; i < 20; i++)
            {
                w.WriteBoolean(header[i] != 0);
                if (header[i] != 0) FusionSimCodec.WriteWord(w, header[i]);
            }
            w.WriteUInt32VarLength(22 - 19, 4);   // the block: three positions, then the packed rotation
            foreach (var position in (int[])[1024, -2048, 5]) w.WriteUInt32VarLength(FusionSimCodec.ZigZag32(position), 4);
            w.WriteUInt32(0xDEADBEEF);
            w.WriteUInt32VarLength(30 - 28, 4);   // an ordinary word after it, relative to word 28
            FusionSimCodec.WriteWord(w, 9);
            w.WriteUInt32VarLength(0, 4);
            FusionSimCodec.PatchHeader(w, 0, 0, 1, 0, 200);
        }));
        Step(room, 3);

        var created = b.Packets.SelectMany(p => p.Records).First(r => r.Id == id);
        Assert.Equal([(22, 1024), (23, -2048), (24, 5), (25, unchecked((int)0xDEADBEEF)), (30, 9)], created.Words);
    }

    [Fact]
    public void A_large_object_is_fragmented_into_reliable_pieces_and_arrives_whole()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var id = IdOf(a, 1024);
        var words = Enumerable.Range(20, 3000).Select(i => (i, i * 7919)).ToArray();
        Send(room, a, a.Packet(records: [(id, FusionSimTestClient.Header(3020, 0, a.PlayerRaw), words)]));
        _sent.Clear();
        Step(room, 2);

        Assert.Contains(_sent, s => s.Actor == 2 && s.Reliable && s.Data[0] == (byte)NetPacketType.NotifyData && s.Data[1] != 0);
        Assert.Equal(3019 * 7919, b.LatestWord(id, 3019));
        Assert.Equal(words.Length, b.Packets.SelectMany(p => p.Records).First(r => r.Id == id).Words.Count);
    }

    [Fact]
    public void An_id_reused_while_its_destroy_is_in_flight_ends_up_as_the_new_object()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var id = Spawn(room, a, 1024);

        Send(room, a, a.Packet(destroys: [id]));
        b.DropNext = true;   // B misses the destroy, so it is still owed one
        Step(room);
        Send(room, a, a.Packet(records: [(id, FusionSimTestClient.Header(22, 0, a.PlayerRaw), [(20, 2)])]));
        Step(room, 4);

        Assert.Equal(2, b.LatestWord(id, 20));
        Assert.True(b.Packets.Last(p => p.Records.Any(r => r.Id == id)).Records.First(r => r.Id == id).Full);
        Assert.DoesNotContain(b.Packets.SkipWhile(p => !p.Records.Any(r => r.Id == id && r.Words.Contains((20, 2)))), p => p.Destroys.Contains(id));
    }

    // ---- Messages (§12) ----

    [Fact]
    public void Rpcs_are_relayed_to_everyone_else_or_to_their_target_with_per_destination_sequences()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var c = Join(room, 3);
        var id = Spawn(room, a, 1024);

        Send(room, a, a.Packet(messages: [Rpc(id, 1000)]));
        Step(room, 2);
        Send(room, a, a.Packet(messages: [Rpc(id, 1001, target: c.Actor)]));
        Step(room, 2);

        Assert.Empty(MessagesOf(a));
        var toB = Assert.Single(MessagesOf(b));
        Assert.Equal((1000, 1, -1, 1UL), (ArgumentOf(toB), toB.Source, toB.Target, toB.Sequence));
        Assert.Equal(SimMessageFlags.Remote, toB.Flags);
        var toC = MessagesOf(c);
        Assert.Equal([1000, 1001], toC.Select(ArgumentOf));
        Assert.Equal([1UL, 2UL], toC.Select(m => m.Sequence));
        Assert.Equal(-1, toC[1].Target);   // cleared on the wire, the receiver fills in itself
        Assert.True(toC[1].Flags.HasFlag(SimMessageFlags.TargetPlayer));
    }

    [Fact]
    public void Reliable_messages_are_delivered_in_order_once_a_gap_is_filled_and_duplicates_dropped()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var id = Spawn(room, a, 1024);
        SimMessage Numbered(int argument, ulong sequence)
        {
            var message = Rpc(id, argument);
            message.Sequence = sequence;
            return message;
        }
        byte[] Raw(params SimMessage[] messages) => a.Notify(w =>
        {
            w.WriteBits(0, 64);
            w.PadToByteBoundary();
            foreach (var m in messages) FusionSimCodec.WriteMessage(w, m, 300, m.Sequence);
            w.PadToByteBoundary();
            FusionSimCodec.PatchHeader(w, 0, messages.Length, 0, 0, 300);
        });

        Send(room, a, Raw(Numbered(2, 2), Numbered(3, 3)));
        Step(room, 2);
        Assert.Empty(MessagesOf(b));   // waits for 1
        Send(room, a, Raw(Numbered(1, 1), Numbered(2, 2)));
        Step(room, 2);
        Assert.Equal([1, 2, 3], MessagesOf(b).Select(ArgumentOf));
    }

    [Fact]
    public void A_lost_message_is_resent_with_its_sequence()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var id = Spawn(room, a, 1024);

        Send(room, a, a.Packet(messages: [Rpc(id, 5)]));
        b.DropNext = true;
        Step(room, 4);
        var message = Assert.Single(MessagesOf(b));
        Assert.Equal((5, 1UL), (ArgumentOf(message), message.Sequence));
    }

    [Fact]
    public void An_rpc_waits_until_its_object_is_confirmed_on_the_receiver()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var id = IdOf(a, 1024);
        Send(room, a, a.Packet(messages: [Rpc(id, 9)], records: [(id, FusionSimTestClient.Header(22, 0, a.PlayerRaw), [(20, 1)])]));
        Step(room, 3);

        var creation = b.Packets.FindIndex(p => p.Records.Any(r => r.Id == id));
        var rpc = b.Packets.FindIndex(p => p.Messages.Count > 0);
        Assert.True(creation >= 0 && rpc > creation, $"creation in packet {creation}, rpc in {rpc}");
    }

    [Fact]
    public void A_client_flooding_broadcasts_is_disconnected_and_nobody_else_is()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var c = Join(room, 3);
        var id = Spawn(room, a, 1024);
        var flood = Enumerable.Range(0, 255).Select(i =>
        {
            var message = Rpc(id, i);
            message.Flags |= SimMessageFlags.Unreliable;
            return message;
        }).ToList();
        for (var i = 0; i < 40; i++) Send(room, a, a.Packet(messages: flood));   // 10 200 messages, no round between
        Step(room, 2);

        Assert.Equal(2, room.PlayerCount);
        var before = b.Packets.Count;
        Step(room, 2);
        Assert.True(b.Packets.Count > before && c.Packets.Count > 0);   // B and C are still served
        Assert.True(MessagesOf(b).Count <= 512);
        Assert.Contains(b.Packets.SelectMany(p => p.Destroys), d => d == FusionSharedRoom.FirstServerId);   // A's PlayerData
    }

    [Fact]
    public void A_client_the_server_disconnects_is_cleaned_up_at_once_without_the_goodbye_grace()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var doomed = Spawn(room, a, 1024, flags: FusionObject.FlagDestroyWhenStateAuthorityLeaves);
        // 1025 reliable messages behind a sequence gap (sequence 1 never comes).
        var gap = Enumerable.Range(0, 1025).Select(i =>
        {
            var message = Rpc(doomed, i);
            message.Sequence = (ulong)(i + 2);
            return message;
        }).ToList();
        foreach (var batch in gap.Chunk(200))
        {
            Step(room, FusionSharedRoom.ServerSendRate / 2);   // under the message-rate bound
            Send(room, a, a.Notify(w =>
            {
                w.WriteBits(0, 64);
                w.PadToByteBoundary();
                foreach (var m in batch) FusionSimCodec.WriteMessage(w, m, 300, m.Sequence);
                w.PadToByteBoundary();
                FusionSimCodec.PatchHeader(w, 0, batch.Length, 0, 0, 300);
            }));
        }
        Step(room);

        Assert.Equal(1, room.PlayerCount);
        Assert.Contains(doomed, b.Packets.SelectMany(p => p.Destroys));   // not a second later: now
    }

    /// <summary>7800 data words of full-width values: a full record of about 45 KB, past what one of our packets carries.</summary>
    private static (int, int)[] WideWords(int offset = 0) => Enumerable.Range(20, 7800).Select(i => (i, int.MinValue + i + offset)).ToArray();

    [Fact]
    public void An_object_whose_full_record_cannot_be_sent_is_refused_and_holds_no_rpc()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var id = IdOf(a, 1024);
        Send(room, a, a.Packet(messages: [Rpc(id, 1)], records: [(id, FusionSimTestClient.Header(7820, 0, a.PlayerRaw), WideWords())]));
        Step(room, 3);

        Assert.DoesNotContain(b.Packets.SelectMany(p => p.Records), r => r.Id == id);
        Assert.Equal([1], MessagesOf(b).Select(ArgumentOf));   // not held for an object that will never come
    }

    [Fact]
    public void An_rpc_waits_for_its_object_two_seconds_at_most()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var id = IdOf(a, 1024);
        // Created small, then every word made full-width: a later joiner's full record no longer fits.
        Send(room, a, a.Packet(records: [(id, FusionSimTestClient.Header(7820, 0, a.PlayerRaw), Enumerable.Range(20, 7800).Select(i => (i, 1)).ToArray())]));
        Step(room, 2);
        Send(room, a, a.Packet(records: [(id, null, WideWords())]));
        Step(room, 2);
        var c = Join(room, 3);
        Send(room, a, a.Packet(messages: [Rpc(id, 7)]));

        Step(room, FusionSharedRoom.ServerSendRate);   // one second: still waiting
        Assert.Empty(MessagesOf(c));
        Step(room, FusionSharedRoom.ServerSendRate + 2);
        Assert.Equal([7], MessagesOf(c).Select(ArgumentOf));
    }

    // ---- State authority (§12.6) ----

    [Fact]
    public void Authority_requests_follow_the_shared_mode_rules()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var owned = Spawn(room, a, 1024);
        var overridable = Spawn(room, a, 1025, flags: FusionObject.FlagAllowStateAuthorityOverride);
        var masterObject = Spawn(room, a, 1026, authority: FusionObject.PlayerMasterClient);

        Send(room, b, b.Packet(messages: [Internal(4, (int)owned, 1), Internal(4, (int)overridable, 1), Internal(4, (int)masterObject, 1)]));
        Step(room, 2);
        Assert.Equal(a.PlayerRaw, b.LatestWord(owned, 8));          // held by a live player: denied
        Assert.Equal(b.PlayerRaw, b.LatestWord(overridable, 8));    // AllowStateAuthorityOverride: granted
        Assert.Equal(b.PlayerRaw, a.LatestWord(overridable, 8));    // the old authority hears it too
        Assert.Equal(-1, b.LatestWord(masterObject, 8));            // master-client objects are not stealable

        // The former authority's in-flight updates no longer apply.
        Send(room, a, a.Packet(records: [(overridable, null, [(20, 111)])]));
        Step(room, 2);
        Assert.NotEqual(111, b.LatestWord(overridable, 20));

        Send(room, a, a.Packet(messages: [Internal(4, (int)owned, 0)]));   // release
        Step(room, 2);
        Assert.Equal(0, b.LatestWord(owned, 8));
        Send(room, b, b.Packet(messages: [Internal(4, (int)owned, 1)]));   // no authority: granted
        Step(room, 2);
        Assert.Equal(b.PlayerRaw, a.LatestWord(owned, 8));
    }

    [Fact]
    public void A_departing_players_objects_cannot_be_claimed_before_its_cleanup()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var doomed = Spawn(room, a, 1024, flags: FusionObject.FlagDestroyWhenStateAuthorityLeaves);
        _clients.Remove(1);
        Send(room, a, FusionRelayCodec.EncodeDisconnect(NetDisconnectReason.Requested));   // goodbye, Photon leave pending

        Send(room, b, b.Packet(messages: [Internal(4, (int)doomed, 1)]));
        Step(room, 2);
        Assert.NotEqual(b.PlayerRaw, b.LatestWord(doomed, 8));

        room.Unregister(1);
        Step(room, 2);
        Assert.Contains(doomed, b.Packets.SelectMany(p => p.Destroys));
    }

    [Fact]
    public void Only_the_authority_may_update_or_destroy_an_object()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var id = Spawn(room, a, 1024);

        Send(room, b, b.Packet(destroys: [id], records: [(id, null, [(20, 666)])]));
        Step(room, 2);
        Assert.DoesNotContain(a.Packets, p => p.Destroys.Contains(id));
        Assert.NotEqual(666, a.LatestWord(id, 20));

        Send(room, a, a.Packet(destroys: [id]));
        Step(room, 2);
        Assert.Contains(b.Packets, p => p.Destroys.Contains(id));
    }

    // ---- Leave (§11.4) ----

    [Fact]
    public void A_leavers_player_data_and_destroy_flagged_objects_go_and_the_rest_are_released()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var doomed = Spawn(room, a, 1024, flags: FusionObject.FlagDestroyWhenStateAuthorityLeaves);
        var kept = Spawn(room, a, 1025);
        var masterObject = Spawn(room, a, 1026, authority: FusionObject.PlayerMasterClient);

        room.Unregister(1);
        Step(room, 3);

        var destroyed = b.Packets.SelectMany(p => p.Destroys).ToList();
        Assert.Contains(doomed, destroyed);
        Assert.Contains(FusionSharedRoom.FirstServerId, destroyed);   // A's PlayerData: PlayerLeft on B
        Assert.DoesNotContain(kept, destroyed);
        Assert.Equal(0, b.LatestWord(kept, 8));
        Assert.Equal(-1, b.LatestWord(masterObject, 8));               // follows the master, now B
        Assert.Equal(b.PlayerRaw, b.LatestWord(FusionSharedRoom.RuntimeConfigId, 26));
        Assert.Equal(1, room.PlayerCount);

        room.Unregister(2);
        Assert.True(room.IsEmpty);
    }

    [Fact]
    public void A_client_that_closes_its_relay_is_cleaned_up_at_its_photon_leave_or_after_a_grace()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var c = Join(room, 3);
        var aObject = Spawn(room, a, 1024, flags: FusionObject.FlagDestroyWhenStateAuthorityLeaves);
        var cObject = Spawn(room, c, 1024, flags: FusionObject.FlagDestroyWhenStateAuthorityLeaves);
        _clients.Remove(1);
        _clients.Remove(3);

        Send(room, a, FusionRelayCodec.EncodeDisconnect(NetDisconnectReason.Requested));
        Send(room, c, FusionRelayCodec.EncodeDisconnect(NetDisconnectReason.Requested));
        Step(room, 3);
        Assert.Empty(b.Packets.SelectMany(p => p.Destroys));   // both wait for their Photon leave

        room.Unregister(1);   // A's leave: cleaned up with the master change, in one packet
        Step(room);
        var packet = b.Packets[^1];
        Assert.Contains(aObject, packet.Destroys);
        Assert.Contains(packet.Records, r => r.Id == FusionSharedRoom.RuntimeConfigId && r.Words.Contains((26, b.PlayerRaw)));
        Assert.DoesNotContain(cObject, b.Packets.SelectMany(p => p.Destroys));

        Step(room, FusionSharedRoom.ServerSendRate);   // C never leaves Photon: cleaned up after the grace
        Assert.Contains(cObject, b.Packets.SelectMany(p => p.Destroys));
        Assert.Equal(1, room.PlayerCount);
    }

    // ---- Untrusted input ----

    [Fact]
    public void Transport_level_garbage_through_the_room_disturbs_nobody_else()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var c = Join(room, 3);
        var id = Spawn(room, a, 1024);
        var random = new Random(1402);
        var sequence = (ushort)1000;

        for (var i = 0; i < 20_000; i++)
        {
            var datagram = new byte[random.Next(1, 1400)];
            random.NextBytes(datagram);
            datagram[0] = (byte)random.Next(0, 10);
            if (datagram[0] is 3 or 8 && datagram.Length >= FusionRelayCodec.NotifyHeaderSize && random.Next(2) == 0)
            {
                // A plausible notify header (in-window sequence, sometimes a fragment) over a random body.
                FusionRelayCodec.WriteNotifyHeader(datagram, new NotifyHeader((NetPacketType)datagram[0],
                    (byte)(random.Next(4) == 0 ? random.Next(0, 256) : 0), ++sequence, 0, 0));
            }
            room.Receive(random.Next(2) == 0 ? c.Actor : 9, datagram, _now);
            if (i % 2000 == 0) Step(room);
        }
        Step(room, 3);

        Send(room, a, a.Packet(records: [(id, null, [(20, 4242)])]));
        Step(room, 3);
        Assert.Equal(4242, b.LatestWord(id, 20));
        Assert.Equal(a.PlayerRaw, b.LatestWord(id, 8));
        Assert.Equal(0, a.DecodeFailures + b.DecodeFailures);
        Assert.Contains(room.PlayerCount, new[] { 2, 3 });   // C may have been disconnected; A and B never
        Assert.NotNull(room.ConnectionOf(1));
        Assert.NotNull(room.ConnectionOf(2));
    }

    [Fact]
    public void Each_creator_has_a_memory_quota_counted_in_real_bytes_and_destroys_refund_it()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        // A full record of a few dozen bits claims 32767 words = 256 KB of server memory.
        var huge = FusionSimTestClient.Header(32767, 0, a.PlayerRaw);
        var ids = Enumerable.Range(0, 70).Select(i => IdOf(a, 2000 + (uint)i)).ToList();
        foreach (var batch in ids.Chunk(10)) Send(room, a, a.Packet(records: batch.Select(id => (id, (int[]?)huge, Array.Empty<(int, int)>()))));
        Step(room, 2);
        var created = b.Packets.SelectMany(p => p.Records).Where(r => r.Full).Select(r => r.Id).ToHashSet();
        Assert.Equal(63, ids.Count(created.Contains));   // 16 MB / (256 KB + overhead)

        // Another player's quota is its own.
        var bObject = Spawn(room, b, 1024);
        Assert.Contains(a.Packets.SelectMany(p => p.Records), r => r.Id == bObject);

        // A destroy gives the bytes back.
        Send(room, a, a.Packet(destroys: [ids[0]]));
        Step(room);
        Send(room, a, a.Packet(records: [(ids[69], huge, [])]));
        Step(room, 2);
        Assert.Contains(b.Packets.SelectMany(p => p.Records), r => r.Id == ids[69] && r.Full);
    }

    [Fact]
    public void Malformed_and_hostile_packets_never_throw_and_never_corrupt_the_room()
    {
        var room = NewRoom();
        var a = Join(room, 1);
        var b = Join(room, 2);
        var id = Spawn(room, a, 1024);
        var random = new Random(1234);

        for (var i = 0; i < 2000; i++)
        {
            var payload = new byte[random.Next(1, 200)];
            random.NextBytes(payload);
            Send(room, b, b.Notify(w => w.WriteBytesAligned(payload)));
        }
        // Refused creations: too few words, the server's id range, a struct flag, a word past the end.
        Send(room, b, b.Packet(records:
        [
            (IdOf(b, 1), FusionSimTestClient.Header(5, 0, b.PlayerRaw), []),
            (2000, FusionSimTestClient.Header(22, 0, b.PlayerRaw), []),
            (IdOf(b, 2), FusionSimTestClient.Header(22, FusionObject.FlagStruct, b.PlayerRaw), []),
            (IdOf(b, 3), FusionSimTestClient.Header(22, 0, b.PlayerRaw), [(25, 1)]),
        ]));
        // A message claiming 4000 bits, and a header claiming 255 of everything with nothing behind it.
        Send(room, b, b.Notify(w =>
        {
            w.WriteBits(0, 64);
            w.WriteInt32VarLength(4000 * 8, 10);
            FusionSimCodec.PatchHeader(w, 0, 1, 0, 0, 1);
        }));
        Send(room, b, b.Notify(w => w.WriteBits(ulong.MaxValue, 64)));
        Step(room, 3);

        Assert.Equal(2, room.PlayerCount);
        Assert.DoesNotContain(a.Packets.SelectMany(p => p.Records), r => r.Id is 2000 || r.Id >> 19 == b.Counter);
        // Nothing B sent touched A's object or the structs, and both streams still decode.
        Assert.Equal(5, b.LatestWord(id, 20));
        Assert.Equal(a.PlayerRaw, b.LatestWord(id, 8));
        foreach (var client in new[] { a, b })
        {
            Assert.Equal(0, client.DecodeFailures);
            Assert.Equal(64, client.LatestWord(FusionSharedRoom.RuntimeConfigId, 20));
            Assert.Equal(a.PlayerRaw, client.LatestWord(FusionSharedRoom.RuntimeConfigId, 26));
            Assert.Equal(1, client.LatestWord(FusionSharedRoom.FirstServerId, 22));
            Assert.Equal(2, client.LatestWord(FusionSharedRoom.FirstServerId + 1, 22));
        }
        Send(room, a, a.Packet(records: [(id, null, [(20, 31337)])]));
        Step(room, 2);
        Assert.Equal(31337, b.LatestWord(id, 20));   // the room still replicates
    }
}
