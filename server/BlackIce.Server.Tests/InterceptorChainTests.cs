using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class InterceptorChainTests
{
    private static EventContext Ctx(EventData ev) => new(roomName: "co-op", senderActor: 1, ev);

    [Fact]
    public void Passthrough_interceptor_forwards_unchanged()
    {
        var ev = new EventData(200, new());
        var v = new PassthroughInterceptor().Intercept(Ctx(ev));
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Same(ev, v.Event);
    }

    [Fact]
    public void Chain_runs_in_order_and_stops_at_first_non_forward()
    {
        var ev = new EventData(200, new());
        var chain = new InterceptorChain(new IEventInterceptor[]
        {
            new PassthroughInterceptor(),
            new DropEverythingInterceptor(),
            new ThrowingInterceptor(),
        });
        var v = chain.Run(Ctx(ev));
        Assert.Equal(RelayAction.Drop, v.Action);
    }

    [Fact]
    public void A_throwing_interceptor_is_caught_and_treated_as_forward()
    {
        var ev = new EventData(200, new());
        var chain = new InterceptorChain(new IEventInterceptor[] { new ThrowingInterceptor() });
        var v = chain.Run(Ctx(ev));
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Same(ev, v.Event);
    }

    private sealed class DropEverythingInterceptor : IEventInterceptor
    {
        public RelayVerdict Intercept(EventContext ctx) => RelayVerdict.Drop();
    }
    private sealed class ThrowingInterceptor : IEventInterceptor
    {
        public RelayVerdict Intercept(EventContext ctx) => throw new System.InvalidOperationException("boom");
    }
}
