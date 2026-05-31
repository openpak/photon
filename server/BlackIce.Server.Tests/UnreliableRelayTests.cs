using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Photon.Transport;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class UnreliableRelayTests
{
    private static PeerConnection Peer(out List<(EventData ev, bool unreliable)> raised)
    {
        var captured = new List<(EventData, bool)>();
        raised = captured;
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { });
        p.OnRaisedClassified = (ev, unrel) => captured.Add((ev, unrel));
        return p;
    }
    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    [Fact]
    public void Unreliable_event_is_relayed_unreliably_to_others()
    {
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));
        var a = Peer(out _); session.Join(1, a);
        var b = Peer(out var bRaised); session.Join(2, b);

        session.RelayFrom(senderActor: 1, new EventData(201, new() { { 245, "pos" } }), unreliable: true);

        Assert.Single(bRaised);
        Assert.True(bRaised[0].unreliable, "a position event that arrived unreliable must be relayed unreliable");
        Assert.Equal(201, bRaised[0].ev.Code);
    }

    [Fact]
    public void Reliable_event_is_relayed_reliably()
    {
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));
        var a = Peer(out _); session.Join(1, a);
        var b = Peer(out var bRaised); session.Join(2, b);

        session.RelayFrom(1, new EventData(200, new()), unreliable: false);

        Assert.Single(bRaised);
        Assert.False(bRaised[0].unreliable);
    }
}
