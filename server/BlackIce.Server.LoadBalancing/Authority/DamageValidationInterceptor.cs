using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Watches relayed damage RPCs (PUN event 200 carrying a DamagePacket) and LOGS any damage value
/// above a sane maximum. Phase 2b is detection-only: it always forwards the event unchanged. A later
/// phase can switch over-threshold hits to Rewrite/Drop once thresholds are tuned against live play.
/// </summary>
public sealed class DamageValidationInterceptor : IEventInterceptor
{
    private readonly float _maxDamage;
    public int FlaggedCount { get; private set; }

    public DamageValidationInterceptor(float maxDamage) => _maxDamage = maxDamage;

    public RelayVerdict Intercept(EventContext ctx)
    {
        var info = PunRpcInfo.From(ctx.Event);
        if (info?.DamageValue is float dmg && dmg > _maxDamage)
        {
            FlaggedCount++;
            Log.Warn("Authority", $"actor {ctx.SenderActor} in \"{ctx.RoomName}\" dealt suspicious damage " +
                                  $"{dmg:F1} (> max {_maxDamage:F0}) via {info.Value.Method ?? "<shortcut rpc>"} " +
                                  $"-> forwarded (log-only)");
        }
        return RelayVerdict.Forward(ctx.Event);
    }
}
