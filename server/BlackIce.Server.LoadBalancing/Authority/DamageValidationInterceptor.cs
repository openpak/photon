using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Watches relayed damage RPCs (PUN event 200 carrying a DamagePacket) and flags a single-hit damage
/// value above a sane maximum, or a non-finite (NaN/Inf) value from garbage bytes. Detection-only unless
/// <c>enforce</c> is set, then the offending hit drops. (Per-window rate is handled by HitRateInterceptor;
/// full server-recompute of damage outcomes is a later, zero-trust phase.)
/// </summary>
public sealed class DamageValidationInterceptor : IEventInterceptor
{
    private readonly float _maxDamage;
    private readonly bool _enforce;
    public int FlaggedCount { get; private set; }

    public DamageValidationInterceptor(float maxDamage, bool enforce = false)
    {
        _maxDamage = maxDamage;
        _enforce = enforce;
    }

    public RelayVerdict Intercept(EventContext ctx)
    {
        var info = PunRpcInfo.From(ctx.Event);
        if (info?.DamageValue is not float dmg) return RelayVerdict.Forward(ctx.Event);
        if (float.IsFinite(dmg) && dmg <= _maxDamage) return RelayVerdict.Forward(ctx.Event);

        FlaggedCount++;
        var detail = float.IsFinite(dmg) ? $"{dmg:F1} (> max {_maxDamage:F0})" : $"non-finite ({dmg})";
        Log.Warn("Authority", $"actor {ctx.SenderActor} in \"{ctx.RoomName}\" dealt suspicious damage " +
                              $"{detail} via {info.Value.Method ?? "<shortcut rpc>"} " +
                              $"-> {(_enforce ? "DROPPED" : "forwarded (log-only)")}");
        return _enforce ? RelayVerdict.Drop() : RelayVerdict.Forward(ctx.Event);
    }
}
