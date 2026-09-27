using System.Collections.Generic;
using System.Net;
using OpenPak.Photon;
using OpenPak.Server.Core;
using OpenPak.Server.Data;
using OpenPak.Server.LoadBalancing;
using Xunit;

namespace OpenPak.Server.Tests;

/// <summary>
/// Fusion has two topologies and they need opposite things from this server. Outbound joins in
/// **Shared**, where the authoritative simulation belongs to Photon Cloud's plugin and we host it
/// (OpenPak.Server.Fusion). Moving Out 2 joins in **ClientServer** — parameter 213 — where the host
/// *client* owns the simulation and the server is only a relay. Hosting a Shared simulation for one of
/// those, and answering it as actor 0, made it drop the session seconds after joining.
/// </summary>
public class FusionTopologyTests
{
    private static (GameServerHandler h, RoomRegistry reg) NewHandler()
    {
        var db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "co-op", IsEnabled = true });
        db.Context.SaveChanges();
        var reg = new RoomRegistry();

        return (new GameServerHandler("s", reg, allowAnonymousLan: true, realms: new RealmService(db.Context)), reg);
    }

    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    private static PeerRoomState JoinWith(string? topology)
    {
        var (h, _) = NewHandler();
        var peer = new PeerConnection("GameServer", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { });
        peer.OnRaised = _ => { };
        peer.Status = SessionStatus.Authenticated;

        var parameters = new Dictionary<byte, object> { { 255, "co-op" } };
        if (topology is not null)
        {
            parameters[213] = topology;
        }
        h.OnOperationRequest(peer, new OperationRequest(226, parameters));

        return Assert.IsType<PeerRoomState>(peer.Tag);
    }

    [Fact]
    public void AClientServerJoinIsRecognised()
    {
        var state = JoinWith("ClientServer");
        Assert.Equal("ClientServer", state.FusionTopology);
        Assert.True(state.IsClientServerTopology);
    }

    [Fact]
    public void ASharedJoinIsNotTreatedAsClientServer()
    {
        var state = JoinWith("Shared");
        Assert.Equal("Shared", state.FusionTopology);
        Assert.False(state.IsClientServerTopology);
    }

    /// A client that says nothing keeps the Shared behaviour this server was built around, so no
    /// existing title changes behaviour.
    [Fact]
    public void ATopologyWeWereNotToldAboutKeepsTheSharedBehaviour()
    {
        var state = JoinWith(null);
        Assert.Null(state.FusionTopology);
        Assert.False(state.IsClientServerTopology);
    }
}
