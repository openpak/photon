using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// One authority/relay decision over an inbound in-room event. The default chain is a single
/// <see cref="PassthroughInterceptor"/> (pure relay). Authority interceptors (Phase 2b) and the
/// playerbot origination path (2c) plug in here.
/// </summary>
public interface IEventInterceptor
{
    RelayVerdict Intercept(EventContext ctx);
}

/// <summary>The default: relay every event unchanged.</summary>
public sealed class PassthroughInterceptor : IEventInterceptor
{
    public RelayVerdict Intercept(EventContext ctx) => RelayVerdict.Forward(ctx.Event);
}

/// <summary>
/// Runs interceptors in order, returning the first non-Forward verdict (Forward means "no opinion,
/// keep going"). If the whole chain forwards, the original event is forwarded. A throwing interceptor
/// is caught, logged, and skipped (treated as Forward) so an authority bug never drops a player.
/// </summary>
public sealed class InterceptorChain
{
    private readonly IEventInterceptor[] _interceptors;
    public InterceptorChain(IEventInterceptor[] interceptors) => _interceptors = interceptors;

    public RelayVerdict Run(EventContext ctx)
    {
        foreach (var i in _interceptors)
        {
            RelayVerdict v;
            try { v = i.Intercept(ctx); }
            catch (System.Exception ex)
            {
                Log.Exception("Relay", $"interceptor {i.GetType().Name} threw on event {ctx.Event.Code}", ex);
                continue;
            }
            if (v.Action != RelayAction.Forward) return v;
        }
        return RelayVerdict.Forward(ctx.Event);
    }
}
