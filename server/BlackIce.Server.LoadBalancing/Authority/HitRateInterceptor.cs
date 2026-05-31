using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Per-actor combat-rate guard over damage RPCs (PUN event 200 carrying a DamagePacket). Flags an actor
/// whose recent activity, within <see cref="AnticheatOptions.RateWindow"/>, exceeds any of:
/// too many hits (rapid-fire / aimbot fire rate), too much cumulative damage, too many headshots
/// (only when <see cref="AnticheatOptions.HeadshotFlagOffset"/> is configured for the game's DamagePacket),
/// or a non-finite damage value (garbage bytes). Detection-only unless <see cref="AnticheatOptions.Enforce"/>.
/// Driven from the single listener thread, so the per-actor meters need no locking.
/// </summary>
public sealed class HitRateInterceptor : IEventInterceptor
{
    private readonly AnticheatOptions _opt;
    private readonly Dictionary<int, SlidingWindowCounter> _hits = new();
    private readonly Dictionary<int, SlidingWindowCounter> _damage = new();
    private readonly Dictionary<int, SlidingWindowCounter> _headshots = new();
    public int FlaggedCount { get; private set; }

    public HitRateInterceptor(AnticheatOptions options) => _opt = options;

    public RelayVerdict Intercept(EventContext ctx)
    {
        var info = PunRpcInfo.From(ctx.Event);
        if (info is not { DamageValue: float dmg }) return RelayVerdict.Forward(ctx.Event);   // only damage RPCs

        var now = DateTime.UtcNow;
        int actor = ctx.SenderActor;

        var hits = Meter(_hits, actor);
        hits.Add(now);
        var damage = Meter(_damage, actor);
        damage.Add(now, float.IsFinite(dmg) ? Math.Max(0f, dmg) : 0f);

        SlidingWindowCounter? headshots = null;
        if (_opt.HeadshotFlagOffset is int off && info.Value.IsHeadshot(off, _opt.HeadshotFlagMask))
        {
            headshots = Meter(_headshots, actor);
            headshots.Add(now);
        }

        string? reason =
            !float.IsFinite(dmg) ? "non-finite damage value"
            : hits.Count(now) > _opt.MaxHitsPerWindow
                ? $"{hits.Count(now)} hits in {_opt.RateWindowSeconds:F1}s (> {_opt.MaxHitsPerWindow})"
            : damage.Sum(now) > _opt.MaxDamagePerWindow
                ? $"{damage.Sum(now):F0} damage in {_opt.RateWindowSeconds:F1}s (> {_opt.MaxDamagePerWindow:F0})"
            : headshots is not null && headshots.Count(now) > _opt.MaxHeadshotsPerWindow
                ? $"{headshots.Count(now)} headshots in {_opt.RateWindowSeconds:F1}s (> {_opt.MaxHeadshotsPerWindow})"
            : null;

        if (reason is null) return RelayVerdict.Forward(ctx.Event);
        FlaggedCount++;
        Log.Warn("Authority", $"actor {actor} in \"{ctx.RoomName}\" combat rate: {reason} " +
                              $"-> {(_opt.Enforce ? "DROPPED" : "forwarded (log-only)")}");
        return _opt.Enforce ? RelayVerdict.Drop() : RelayVerdict.Forward(ctx.Event);
    }

    private SlidingWindowCounter Meter(Dictionary<int, SlidingWindowCounter> map, int actor)
        => map.TryGetValue(actor, out var m) ? m : map[actor] = new SlidingWindowCounter(_opt.RateWindow);
}
