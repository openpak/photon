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
/// (FusionSharedHost). Moving Out 2 joins in **ClientServer** — parameter 213 — where the host
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

    /// The client's HandleStartMessage awaits NetworkRunner.Initialize — which is what creates its
    /// simulation — while HandlePlayerRefMapping dereferences that simulation with no null guard. A
    /// mapping sent right after Start therefore kills the session (measured on Moving Out 2: the runner
    /// took ~850 ms and our mapping landed 1 ms after Start). Nobody needs a mapping for themselves
    /// anyway: the Join Confirmation already carried their PlayerRef and UniqueId.
    [Fact]
    public void ASoloHostIsNotSentAPlayerRefMappingForItself()
    {
        var (h, _) = NewHandler();
        var host = Joined(h, "solo", out var raised);

        Fusion(h, host, RealJoinRequest);                                                  // Join -> Confirmation
        raised.Clear();
        Fusion(h, host, new byte[] { FusionProtocol.MsgIdNetworkConfigSync, 1 });          // config sync -> Start

        var messages = raised.Where(e => e.Code == FusionProtocol.ProtocolPackageEventCode)
                             .Select(e => ((byte[])e.Parameters[245])[0]).ToArray();
        Assert.Contains((byte)5, messages);       // Start
        Assert.DoesNotContain((byte)8, messages); // PlayerRefMapping
    }

    /// And a mapping for a REAL other player is held back until the peer proves its runner exists, by
    /// sending us any Fusion packet after its own Start.
    [Fact]
    public void AnotherPlayersMappingWaitsForTheFirstPacketAfterStart()
    {
        var (h, _) = NewHandler();
        var host = Joined(h, "co-op", out var hostRaised);
        var joiner = Joined(h, "co-op", out var joinerRaised);

        foreach (var peer in new[] { host, joiner })
        {
            Fusion(h, peer, Fusion2JoinRequest);
            Fusion(h, peer, new byte[] { FusionProtocol.MsgIdNetworkConfigSync, 1 });
        }
        joinerRaised.Clear();
        Assert.DoesNotContain(joinerRaised, e => IsFusionMessage(e, 8));

        // any further Fusion packet from the joiner releases what was held back
        Fusion(h, joiner, new byte[] { 6, 1 });   // a Snapshot: the runner is up
        Assert.Contains(joinerRaised, e => IsFusionMessage(e, 8));
    }

    /// A real console's Join Request, byte for byte (Moving Out 2, 2026-09-27): protocol version 6,
    /// serialization version 1.1.8, Request / ClientServer / PeerMode.Server, no JoinRequests.
    private static readonly byte[] RealJoinRequest =
        Convert.FromHexString("010601000000010000000800000001010100000000000000");

    /// A synthetic Fusion 2 Join (serialization version 2.1.2, an arbitrary 2.x value: the server only
    /// checks the major version; Outbound sends 2.0.8). Only Fusion 2 has a PlayerRefMapping message.
    private static readonly byte[] Fusion2JoinRequest =
        Convert.FromHexString("010602000000010000000200000001010100000000000000");

    /// Message id 8 is PlayerRefMapping in Fusion 2 and DummyTrafficSync in Fusion 1 — a 1.x peer fed
    /// ours reads it as an instruction to send dummy traffic every 100 ms (confirmed by running our
    /// bytes through the Fusion 1.1.12 SDK's own deserializer). So a 1.x peer never gets one.
    [Fact]
    public void AFusion1PeerIsNeverSentAPlayerRefMapping()
    {
        var (h, _) = NewHandler();
        var host = Joined(h, "v1", out _);
        var joiner = Joined(h, "v1", out var joinerRaised);

        foreach (var peer in new[] { host, joiner })
        {
            Fusion(h, peer, RealJoinRequest);                                       // Fusion 1.1.8
            Fusion(h, peer, new byte[] { FusionProtocol.MsgIdNetworkConfigSync, 1 });
        }
        joinerRaised.Clear();
        Fusion(h, joiner, new byte[] { 6, 1 });   // whatever it sends, no mapping may follow
        Assert.DoesNotContain(joinerRaised, e => IsFusionMessage(e, 8));
    }

    private static bool IsFusionMessage(EventData e, byte messageId) =>
        e.Code == FusionProtocol.ProtocolPackageEventCode
        && e.Parameters.TryGetValue((byte)245, out var d) && d is byte[] { Length: > 0 } b && b[0] == messageId;

    private static PeerConnection Joined(GameServerHandler h, string room, out List<EventData> raised)
    {
        var peer = new PeerConnection("GameServer", new IPEndPoint(IPAddress.Loopback, Random.Shared.Next(2, 60000)),
                                      new Null(), (_, _) => { });
        var seen = new List<EventData>();
        peer.OnRaised = seen.Add;
        peer.Status = SessionStatus.Authenticated;
        h.OnOperationRequest(peer, new OperationRequest(226, new Dictionary<byte, object>
        {
            { 255, room }, { 213, "ClientServer" },
        }));
        raised = seen;
        return peer;
    }

    private static void Fusion(GameServerHandler h, PeerConnection peer, byte[] payload) =>
        h.OnOperationRequest(peer, new OperationRequest(253, new Dictionary<byte, object>
        {
            { 244, FusionProtocol.ProtocolPackageEventCode },
            { 245, payload },
        }));

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
