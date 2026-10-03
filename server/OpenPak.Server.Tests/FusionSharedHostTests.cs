using OpenPak.Photon;
using OpenPak.Photon.Transport;
using OpenPak.Server.LoadBalancing.Fusion;

namespace OpenPak.Server.Tests;

/// <summary><see cref="FusionSharedHost"/>: room lifetime, ClientServer rooms, and the delivery class of
/// what it sends.</summary>
public class FusionSharedHostTests
{
    private double _now;
    private readonly List<(string Room, int Actor, byte[] Data, bool Reliable)> _sent = new();

    private FusionSharedHost NewHost() => new((room, actor, data, reliable) => _sent.Add((room, actor, data, reliable)), clock: () => _now);

    private static byte[] Connect(int actor) => FusionRelayCodec.EncodeConnect(new NetConnectionId(0, 0, 1), [],
        BitConverter.ToUInt64(FusionProtocol.UniqueIdForActor(actor)));

    [Fact]
    public void A_shared_room_lives_from_its_first_join_to_its_last_leave()
    {
        var host = NewHost();
        Assert.False(host.FeedRelay("r", 1, Connect(1)));   // no room yet
        host.PlayerJoined("r", 1, FusionProtocol.UniqueIdForActor(1));
        Assert.True(host.FeedRelay("r", 1, Connect(1)));
        _now += 0.1;
        host.Tick();
        Assert.Contains(_sent, s => s.Actor == 1 && s.Data[0] == (byte)NetPacketType.Command);   // Accepted
        Assert.Contains(_sent, s => s.Actor == 1 && s.Data[0] == (byte)NetPacketType.NotifyData && !s.Reliable);

        host.PlayerLeft("r", 1);
        host.Tick();
        Assert.False(host.FeedRelay("r", 1, Connect(1)));
    }

    [Fact]
    public void A_failing_queued_action_does_not_drop_the_ones_behind_it()
    {
        var host = NewHost();
        host.PlayerJoined("r", 1, new byte[7]);   // not a UniqueId: registering it throws
        host.PlayerJoined("r", 2, FusionProtocol.UniqueIdForActor(2));
        host.FeedRelay("r", 2, Connect(2));
        host.Tick();
        Assert.Contains(_sent, s => s.Actor == 2 && FusionRelayCodec.TryDecodeAccepted(s.Data, out _, out _, out _));
    }

    [Fact]
    public void A_room_takes_the_photon_rooms_capacity_and_is_not_capped_at_eight_without_one()
    {
        var host = NewHost();
        for (var actor = 1; actor <= 12; actor++)
        {
            host.PlayerJoined("r", actor, FusionProtocol.UniqueIdForActor(actor));
            host.FeedRelay("r", actor, Connect(actor));
        }
        host.PlayerJoined("sized", 1, FusionProtocol.UniqueIdForActor(1), maxPlayers: 10);
        host.FeedRelay("sized", 1, Connect(1));
        _now += 0.1;
        host.Tick();

        for (var actor = 1; actor <= 12; actor++)
            Assert.Contains(_sent, s => s.Room == "r" && s.Actor == actor && FusionRelayCodec.TryDecodeAccepted(s.Data, out _, out _, out _));
        var client = new FusionSimTestClient(1);
        foreach (var s in _sent.Where(s => s.Room == "sized")) client.Receive(s.Data);
        Assert.Equal(10, client.LatestWord(FusionSharedRoom.RuntimeConfigId, 25));
    }

    [Fact]
    public void A_client_server_room_gets_no_simulation()
    {
        var host = NewHost();
        host.PlayerJoined("cs", 1, FusionProtocol.UniqueIdForActor(1), clientServer: true);
        Assert.False(host.FeedRelay("cs", 1, Connect(1)));
        host.Tick();
        Assert.Empty(_sent);
    }

    [Fact]
    public void The_unreliable_size_limit_is_exactly_one_client_datagram()
    {
        var limit = FusionSharedHost.MaxUnreliableDatagramBytes;
        var message = WireMessage.EventMessage(new EventData(101, new() { { (byte)254, 0 }, { (byte)245, new byte[limit] } }));
        var command = new EnetPeer().WrapUnreliable(message);
        Assert.Equal(EnetPeer.Mtu, PhotonHeader.Size + command.ToBytes().Length);
    }

    [Fact]
    public void A_datagram_over_the_limit_is_sent_reliably()
    {
        var host = NewHost();
        host.PlayerJoined("r", 1, FusionProtocol.UniqueIdForActor(1));
        host.PlayerJoined("r", 2, FusionProtocol.UniqueIdForActor(2));
        host.FeedRelay("r", 1, Connect(1));
        host.FeedRelay("r", 2, Connect(2));
        _now += 0.1;
        host.Tick();
        // A 300-word object of large values: a notify packet between the limit and the 1280-byte fragment threshold.
        var w = new NetBitWriter();
        w.WriteBits(0, 64);
        w.WriteUInt32VarLength(FusionSimCodec.ZigZag32(1 << 19 | 1024), 6);
        w.WriteBoolean(true);
        for (var i = 1; i < 20; i++)
        {
            var word = i switch { 1 => 300, 2 => 7, 8 => 2, _ => 0 };
            w.WriteBoolean(word != 0);
            if (word != 0) FusionSimCodec.WriteWord(w, word);
        }
        for (var i = 20; i < 300; i++)
        {
            w.WriteUInt32VarLength(1, 4);
            FusionSimCodec.WriteWord(w, 0x40000 + i);
        }
        w.WriteUInt32VarLength(0, 4);
        FusionSimCodec.PatchHeader(w, 0, 0, 1, 0, 50);
        var datagram = new byte[FusionRelayCodec.NotifyHeaderSize + w.ByteCount];
        FusionRelayCodec.WriteNotifyHeader(datagram, new NotifyHeader(NetPacketType.NotifyData, 0, 1, 0, 0));
        w.Bytes.CopyTo(datagram.AsSpan(FusionRelayCodec.NotifyHeaderSize));
        host.FeedRelay("r", 1, datagram);
        _sent.Clear();
        _now += 0.05;
        host.Tick();

        var toB = _sent.Where(s => s.Actor == 2 && s.Data[0] == (byte)NetPacketType.NotifyData).ToList();
        Assert.Contains(toB, s => s.Data.Length > FusionSharedHost.MaxUnreliableDatagramBytes && s.Data.Length <= FusionRelayCodec.MaxDatagramBytes && s.Reliable);
        Assert.All(_sent.Where(s => !s.Reliable), s => Assert.True(s.Data.Length <= FusionSharedHost.MaxUnreliableDatagramBytes));
    }
}
