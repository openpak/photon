namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Zero-trust consistency check: an outcome (e.g. damage) aimed at an entity the world has already
/// <em>destroyed</em> is rejected — a client cannot keep acting on a corpse the server watched die. This
/// catches a real class of cheat/desync (damage-after-death, double-kill credit) using only observable
/// existence facts, with no game-formula knowledge.
///
/// <para><b>Fail-open</b> for everything else: a claim with no target, or a target the shadow has never
/// observed, is <see cref="OutcomeJudgment.Valid"/> — the server does not see every entity, so "unknown"
/// must never be treated as "cheat".</para>
/// </summary>
public sealed class DeadTargetOutcomeRule : IOutcomeRule
{
    public OutcomeJudgment Evaluate(in OutcomeClaim claim, RoomWorldState state, out string? reason)
    {
        reason = null;
        if (claim.TargetViewId is not int viewId) return OutcomeJudgment.Valid;   // no target: can't judge

        // IsAlive is tri-state: true=alive, false=known-destroyed, null=never observed (fail-open).
        if (state.IsAlive(viewId) == false)
        {
            reason = $"outcome targets destroyed view {viewId}";
            return OutcomeJudgment.Reject;
        }

        return OutcomeJudgment.Valid;   // alive or unknown: accept
    }
}
