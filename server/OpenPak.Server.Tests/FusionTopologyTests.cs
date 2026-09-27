using System.Collections.Generic;
using System.Net;
using OpenPak.Photon;
using OpenPak.Photon.Transport;
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

    /// A ClientServer peer's protocol-package traffic is relayed to the other actors as well as
    /// answered: answering nothing at all left Moving Out 2 sending one event and timing out after
    /// fifteen seconds of silence.
    [Fact]
    public void ClientServerProtocolPackagesAreRelayedToTheOtherActors()
    {
        var (h, reg) = NewHandler();

        var host = new PeerConnection("GameServer", new IPEndPoint(IPAddress.Loopback, 1), new Null(), (_, _) => { });
        var hostRaised = new List<EventData>();
        host.OnRaised = hostRaised.Add;
        host.Status = SessionStatus.Authenticated;
        h.OnOperationRequest(host, new OperationRequest(226, new Dictionary<byte, object>
        {
            { 255, "co-op" }, { 213, "ClientServer" },
        }));

        var other = new PeerConnection("GameServer", new IPEndPoint(IPAddress.Loopback, 2), new Null(), (_, _) => { });
        var otherRaised = new List<EventData>();
        other.OnRaised = otherRaised.Add;
        other.Status = SessionStatus.Authenticated;
        h.OnOperationRequest(other, new OperationRequest(226, new Dictionary<byte, object>
        {
            { 255, "co-op" }, { 213, "ClientServer" },
        }));

        hostRaised.Clear();
        otherRaised.Clear();

        var payload = new byte[] { 1, 1, FusionProtocol.JoinTypeRequest };  // msg id 1 = Join
        h.OnOperationRequest(host, new OperationRequest(253, new Dictionary<byte, object>
        {
            { 244, FusionProtocol.ProtocolPackageEventCode },
            { 245, payload },
            { 250, true },
        }));

        // the other peer sees it...
        Assert.Contains(otherRaised, e => e.Code == FusionProtocol.ProtocolPackageEventCode);

    }

    /// eNet retransmits an unacknowledged reliable command on a timer, so the acknowledgement has to
    /// leave before any application work for that packet — a Fusion Join creates a whole simulation,
    /// and an ACK measured 830 ms behind its command had Moving Out 2 retransmitting until the peer
    /// was dropped.
    [Fact]
    public void ReliableCommandsAreAcknowledgedBeforeTheWorkIsDone()
    {
        var sends = new List<(IReadOnlyList<NCommand> Commands, int Challenge)>();
        var order = new List<string>();

        var handler = new RecordingHandler(order);
        var peer = new PeerConnection("GameServer", new IPEndPoint(IPAddress.Loopback, 0), handler,
                                      (cmds, ch) => { order.Add("ack"); sends.Add((cmds, ch)); });

        // One packet carrying a reliable application send: the ACK must precede OnOperationRequest.
        var payload = new byte[] { 0xF3, 0x02, 0xFD, 0x03 };
        var command = new NCommand(NCommand.SendReliable, 1, NCommand.FlagReliable, 4, 1, payload);
        peer.HandlePacket(new PhotonHeader(0, false, 1, 1234, 42), new[] { command });

        Assert.Contains("ack", order);
        if (order.Contains("work"))
        {
            Assert.True(order.IndexOf("ack") < order.IndexOf("work"),
                        "the acknowledgement must be sent before the payload is processed");
        }
        Assert.Contains(sends, s => s.Commands.Any(c => c.CommandType == NCommand.Acknowledge));
    }

    private sealed class RecordingHandler(List<string> order) : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) => order.Add("work");
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) => order.Add("work");
        public void OnDisconnect(PeerConnection peer) => order.Add("work");
    }
}
