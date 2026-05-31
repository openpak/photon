using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class RoomSessionRelayTests
{
    private static PeerConnection Peer(out List<EventData> raised)
    {
        var captured = new List<EventData>();
        raised = captured;
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0),
                                   new NullHandler(), (_, _) => { });
        p.OnRaised = captured.Add;
        return p;
    }

    private sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    [Fact]
    public void Event_is_relayed_to_other_actors_not_the_sender()
    {
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));
        var a = Peer(out var aRaised); session.Join(actor: 1, a);
        var b = Peer(out var bRaised); session.Join(actor: 2, b);

        var ev = new EventData(200, new() { { 245, "hello" } });
        session.RelayFrom(senderActor: 1, ev);

        Assert.Empty(aRaised);
        Assert.Single(bRaised);
        Assert.Equal(200, bRaised[0].Code);
    }

    [Fact]
    public void A_left_actor_stops_receiving()
    {
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));
        var a = Peer(out _); session.Join(1, a);
        var b = Peer(out var bRaised); session.Join(2, b);
        session.Leave(2);

        session.RelayFrom(1, new EventData(200, new()));
        Assert.Empty(bRaised);
    }

    [Fact]
    public void Drop_verdict_relays_nothing()
    {
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new DropAll() }));
        var a = Peer(out _); session.Join(1, a);
        var b = Peer(out var bRaised); session.Join(2, b);

        session.RelayFrom(1, new EventData(200, new()));
        Assert.Empty(bRaised);
    }

    [Fact]
    public void Originate_relays_the_event_and_the_extras_to_others()
    {
        var extra = new EventData(202, new());
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new OriginateExtra(extra) }));
        var a = Peer(out _); session.Join(1, a);
        var b = Peer(out var bRaised); session.Join(2, b);

        session.RelayFrom(1, new EventData(200, new()));
        Assert.Equal(2, bRaised.Count);
        Assert.Equal(200, bRaised[0].Code);
        Assert.Equal(202, bRaised[1].Code);
    }

    private sealed class DropAll : IEventInterceptor
    {
        public RelayVerdict Intercept(EventContext ctx) => RelayVerdict.Drop();
    }
    private sealed class OriginateExtra : IEventInterceptor
    {
        private readonly EventData _extra;
        public OriginateExtra(EventData extra) => _extra = extra;
        public RelayVerdict Intercept(EventContext ctx) => RelayVerdict.Originate(ctx.Event, new[] { _extra });
    }
}
