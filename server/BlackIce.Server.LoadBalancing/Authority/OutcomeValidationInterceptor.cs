using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Zero-trust authority over consequential outcome RPCs (PUN event 200). Builds an <see cref="OutcomeClaim"/>
/// from the relayed RPC and runs the configured <see cref="IOutcomeRule"/>s against the room's
/// <see cref="RoomWorldState"/>. The first rule that rejects wins; otherwise the outcome is accepted.
///
/// <para>A rejection acts per the realm <see cref="AuthorityPolicy"/>: Observe/Warn forward (Warn logs +
/// tallies); Enforce/Strict <see cref="RelayAction.Drop"/> the outcome (zero-trust). Fail-open throughout:
/// a non-200 event, an RPC we can't decode, or a claim no rule can fault is forwarded untouched. The flag
/// tally is atomic so it stays exact under any stray concurrency.</para>
/// </summary>
public sealed class OutcomeValidationInterceptor : IEventInterceptor
{
    private readonly RoomWorldState _world;
    private readonly IReadOnlyList<IOutcomeRule> _rules;
    private readonly AuthorityPolicy _policy;
    private readonly ViolationTracker _violations;

    private int _flaggedCount;
    public int FlaggedCount => _flaggedCount;

    public OutcomeValidationInterceptor(RoomWorldState world, IReadOnlyList<IOutcomeRule> rules)
        : this(world, rules, AuthorityPolicy.Default, new ViolationTracker(int.MaxValue, TimeSpan.FromMinutes(5))) { }

    public OutcomeValidationInterceptor(RoomWorldState world, IReadOnlyList<IOutcomeRule> rules,
                                        AuthorityPolicy policy, ViolationTracker violations)
    {
        _world = world;
        _rules = rules;
        _policy = policy;
        _violations = violations;
    }

    public RelayVerdict Intercept(EventContext ctx)
    {
        if (PunRpcInfo.From(ctx.Event) is not { } rpc) return RelayVerdict.Forward(ctx.Event);   // not a 200 RPC

        var claim = new OutcomeClaim(ctx.SenderActor, rpc.ViewId, rpc.DamageValue, rpc.Method);

        string? reason = null;
        bool rejected = false;
        foreach (var rule in _rules)
        {
            if (rule.Evaluate(in claim, _world, out reason) == OutcomeJudgment.Reject) { rejected = true; break; }
        }

        if (!rejected) return RelayVerdict.Forward(ctx.Event);   // accepted: no rule could fault it

        if (_policy.CountsViolations)
        {
            System.Threading.Interlocked.Increment(ref _flaggedCount);
            _violations.Flag(ctx.RoomName, ctx.SenderActor);
            Log.Warn("Authority", $"actor {ctx.SenderActor} in \"{ctx.RoomName}\" outcome rejected: {reason} " +
                                  $"(via {rpc.Method ?? "<shortcut rpc>"}) -> {_policy.Strictness}");
        }

        return _policy.ActionFor(ViolationKind.Outcome) == RelayAction.Drop
            ? RelayVerdict.Drop()
            : RelayVerdict.Forward(ctx.Event);
    }
}
