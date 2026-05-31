using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Per-actor event-flood guard: counts every relayed event from each actor in a sliding window and
/// flags an actor exceeding <see cref="AnticheatOptions.MaxEventsPerWindow"/> (a spamming/DoS client).
/// Detection-only unless <see cref="AnticheatOptions.Enforce"/> is set, then the excess events drop.
/// Driven from the single listener thread, so the per-actor meters need no locking.
/// </summary>
public sealed class EventRateInterceptor : IEventInterceptor
{
    private readonly AnticheatOptions _opt;
    private readonly Dictionary<int, SlidingWindowCounter> _events = new();
    public int FlaggedCount { get; private set; }

    public EventRateInterceptor(AnticheatOptions options) => _opt = options;

    public RelayVerdict Intercept(EventContext ctx)
    {
        var now = DateTime.UtcNow;
        var meter = Meter(ctx.SenderActor);
        meter.Add(now);
        if (meter.Count(now) <= _opt.MaxEventsPerWindow) return RelayVerdict.Forward(ctx.Event);

        FlaggedCount++;
        Log.Warn("Authority", $"actor {ctx.SenderActor} in \"{ctx.RoomName}\" event flood: {meter.Count(now)} " +
                              $"in {_opt.RateWindowSeconds:F1}s (> {_opt.MaxEventsPerWindow}) " +
                              $"-> {(_opt.Enforce ? "DROPPED" : "forwarded (log-only)")}");
        return _opt.Enforce ? RelayVerdict.Drop() : RelayVerdict.Forward(ctx.Event);
    }

    private SlidingWindowCounter Meter(int actor)
        => _events.TryGetValue(actor, out var m) ? m : _events[actor] = new SlidingWindowCounter(_opt.RateWindow);
}
