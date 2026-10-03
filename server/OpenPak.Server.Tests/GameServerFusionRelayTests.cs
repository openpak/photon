using System.Collections.Generic;
using System.Net;
using OpenPak.Photon;
using OpenPak.Server.Core;
using OpenPak.Server.Data;
using OpenPak.Server.LoadBalancing;
using OpenPak.Server.LoadBalancing.Fusion;
using Xunit;

namespace OpenPak.Server.Tests;

/// <summary>How <see cref="GameServerHandler"/> routes event 101 between the hosted Shared simulation and
/// the paths a room without one (ClientServer, Moving Out 2) has always taken.</summary>
public class GameServerFusionRelayTests
{
    private sealed class Recording(IFusionSessionHost inner) : IFusionSessionHost
    {
        public readonly List<(string Room, int Actor, bool ClientServer, int MaxPlayers)> Joined = new();
        public readonly List<bool> Fed = new();

        public void PlayerJoined(string room, int actor, byte[] uniqueId, bool clientServer = false, int maxPlayers = 0)
        {
            Joined.Add((room, actor, clientServer, maxPlayers));
            inner.PlayerJoined(room, actor, uniqueId, clientServer, maxPlayers);
        }

        public void PlayerLeft(string room, int actor) => inner.PlayerLeft(room, actor);

        public bool FeedRelay(string room, int actor, byte[] payload)
        {
            var consumed = inner.FeedRelay(room, actor, payload);
            Fed.Add(consumed);
            return consumed;
        }

        public void Tick() => inner.Tick();
    }

    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    private static readonly byte[] Fusion2JoinRequest = Convert.FromHexString("010602000000010000000200000001010100000000000000");

    private readonly Recording _fusion = new(new FusionSharedHost((_, _, _, _) => { }));
    private readonly GameServerHandler _handler;

    public GameServerFusionRelayTests()
    {
        var db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "room", IsEnabled = true });
        db.Context.SaveChanges();
        _handler = new GameServerHandler("s", new RoomRegistry(), allowAnonymousLan: true, realms: new RealmService(db.Context), fusion: _fusion);
    }

    private PeerConnection Join(string topology, out List<EventData> raised, byte? maxPlayers = null)
    {
        var peer = new PeerConnection("GameServer", new IPEndPoint(IPAddress.Loopback, Random.Shared.Next(2, 60000)), new Null(), (_, _) => { });
        var seen = new List<EventData>();
        peer.OnRaised = seen.Add;
        peer.Status = SessionStatus.Authenticated;
        var parameters = new Dictionary<byte, object> { { 255, "room" }, { 213, topology } };
        if (maxPlayers is { } max) parameters[248] = new Dictionary<object, object> { { (byte)255, max } };
        _handler.OnOperationRequest(peer, new OperationRequest(226, parameters));
        Raise(peer, FusionProtocol.ProtocolPackageEventCode, Fusion2JoinRequest);
        raised = seen;
        return peer;
    }

    private void Raise(PeerConnection peer, byte code, byte[] data, int[]? targets = null)
    {
        var parameters = new Dictionary<byte, object> { { 244, code }, { 245, data } };
        if (targets is not null) parameters[252] = targets;
        _handler.OnOperationRequest(peer, new OperationRequest(253, parameters));
    }

    private static byte[] RelayConnect() => FusionRelayCodec.EncodeConnect(new NetConnectionId(0, 0, 1), [], 42);

    private static bool Is101(EventData e) => e.Code == FusionRelayProtocol.DataEventCode;

    [Fact]
    public void A_client_server_room_keeps_its_pre_simulation_101_paths()
    {
        var host = Join("ClientServer", out var hostRaised);
        var client = Join("ClientServer", out var clientRaised);
        Assert.All(_fusion.Joined, j => Assert.True(j.ClientServer));
        hostRaised.Clear();
        clientRaised.Clear();

        // Relay traffic names its peer: carried to it, stamped with the sender.
        Raise(client, FusionRelayProtocol.DataEventCode, [3, 1, 2], targets: [1]);
        var carried = Assert.Single(hostRaised, Is101);
        Assert.Equal(2, carried.Parameters[254]);
        Assert.Empty(_fusion.Fed);

        // A Connect to actor 0: no simulation takes it, so the self-loopback Accepted answers it.
        Raise(client, FusionRelayProtocol.DataEventCode, RelayConnect(), targets: [0]);
        Assert.Equal([false], _fusion.Fed);
        Assert.Contains(clientRaised, e => Is101(e) && e.Parameters[245] is byte[] d && d[0] == 1 && d[1] == 2);   // Command Accepted
    }

    [Fact]
    public void A_shared_room_hands_101_to_its_simulation_with_the_rooms_capacity()
    {
        var a = Join("Shared", out var aRaised, maxPlayers: 6);
        var b = Join("Shared", out var bRaised);
        aRaised.Clear();
        bRaised.Clear();

        Raise(a, FusionRelayProtocol.DataEventCode, RelayConnect(), targets: [0]);
        Assert.Equal([true], _fusion.Fed);
        Assert.DoesNotContain(aRaised, Is101);   // answered by the simulation on its tick, not inline
        Assert.DoesNotContain(bRaised, Is101);   // and never relayed peer to peer
        Assert.All(_fusion.Joined, j => Assert.Equal(6, j.MaxPlayers));
    }
}
