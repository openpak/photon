using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Authority over relayed damage RPCs (PUN event 200 carrying a DamagePacket). Flags any damage value
/// above a sane maximum and acts per the realm's <see cref="AuthorityPolicy"/>: Observe forwards (no
/// opinion); Warn forwards but logs + tallies; Enforce/Strict <see cref="RelayAction.Drop"/> the
/// over-threshold (zero-trust) outcome. Fail-open: a normal hit, a non-damage event, or an unparseable
/// RPC is forwarded untouched. The flag tally is atomic so it stays exact under any stray concurrency.
/// </summary>
public sealed class DamageValidationInterceptor : IEventInterceptor
{
    private readonly float _maxDamage;
    private readonly AuthorityPolicy _policy;
    private readonly ViolationTracker _violations;

    private int _flaggedCount;
    public int FlaggedCount => _flaggedCount;

    public DamageValidationInterceptor(float maxDamage)
        : this(maxDamage, AuthorityPolicy.Default, new ViolationTracker(int.MaxValue, TimeSpan.FromMinutes(5))) { }

    public DamageValidationInterceptor(float maxDamage, AuthorityPolicy policy, ViolationTracker violations)
    {
        _maxDamage = maxDamage;
        _policy = policy;
        _violations = violations;
    }

    public RelayVerdict Intercept(EventContext ctx)
    {
        var info = PunRpcInfo.From(ctx.Event);
        if (info?.DamageValue is not float dmg || dmg <= _maxDamage)
            return RelayVerdict.Forward(ctx.Event);   // not a damage RPC, or within bounds: fail-open

        if (_policy.CountsViolations)
        {
            System.Threading.Interlocked.Increment(ref _flaggedCount);
            _violations.Flag(ctx.RoomName, ctx.SenderActor);
            Log.Warn("Authority", $"actor {ctx.SenderActor} in \"{ctx.RoomName}\" dealt suspicious damage " +
                                  $"{dmg:F1} (> max {_maxDamage:F0}) via {info.Value.Method ?? "<shortcut rpc>"} " +
                                  $"-> {_policy.Strictness}");
        }

        return _policy.ActionFor(ViolationKind.Outcome) == RelayAction.Drop
            ? RelayVerdict.Drop()
            : RelayVerdict.Forward(ctx.Event);
    }
}
