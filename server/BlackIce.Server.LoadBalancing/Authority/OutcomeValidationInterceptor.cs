using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Zero-trust authority over consequential outcome RPCs (PUN event 200). Builds an <see cref="OutcomeClaim"/>
/// from the relayed RPC and runs the configured <see cref="IOutcomeRule"/>s against the room's
/// <see cref="RoomWorldState"/>. The first rule that rejects wins; otherwise the outcome is accepted.
///
/// <para>Follows the project's anti-cheat convention: detection-only by default (<c>enforce</c> false) —
/// a rejected outcome is logged and still forwarded; set <c>enforce</c> (via
/// <see cref="AnticheatOptions.Enforce"/>) to <see cref="RelayAction.Drop"/> it instead. Fail-open
/// throughout: a non-200 event, an RPC we can't decode, or a claim no rule can fault is forwarded
/// untouched. The flag tally is atomic so it stays exact under any stray concurrency.</para>
/// </summary>
public sealed class OutcomeValidationInterceptor : IEventInterceptor
{
    private readonly RoomWorldState _world;
    private readonly IReadOnlyList<IOutcomeRule> _rules;
    private readonly bool _enforce;

    private int _flaggedCount;
    public int FlaggedCount => _flaggedCount;

    public OutcomeValidationInterceptor(RoomWorldState world, IReadOnlyList<IOutcomeRule> rules, bool enforce = false)
    {
        _world = world;
        _rules = rules;
        _enforce = enforce;
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

        System.Threading.Interlocked.Increment(ref _flaggedCount);
        Log.Warn("Authority", $"actor {ctx.SenderActor} in \"{ctx.RoomName}\" outcome rejected: {reason} " +
                              $"(via {rpc.Method ?? "<shortcut rpc>"}) -> {(_enforce ? "DROPPED" : "forwarded (log-only)")}");

        return _enforce ? RelayVerdict.Drop() : RelayVerdict.Forward(ctx.Event);
    }
}
