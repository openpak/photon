namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// A claimed consequential outcome (damage / kill / loot / XP) extracted from a relayed RPC, expressed
/// in terms the shadow world-state can reason about — independent of the wire encoding. Fields are
/// optional because the decoder is best-effort; a rule that needs a field it didn't get must fail open.
/// </summary>
/// <param name="SenderActor">The actor number that sent the RPC (the <em>claimed</em> attacker).</param>
/// <param name="TargetViewId">The target photonView ID, if the RPC carried one.</param>
/// <param name="Damage">The first numeric parameter read as a damage value, if any.</param>
/// <param name="Method">The RPC method name, if sent by name (null for shortcut-id RPCs).</param>
public readonly record struct OutcomeClaim(int SenderActor, int? TargetViewId, float? Damage, string? Method);

/// <summary>The verdict of an <see cref="IOutcomeRule"/> over a single claim.</summary>
public enum OutcomeJudgment
{
    /// <summary>The rule cannot fault the claim (includes "can't judge" — fail-open).</summary>
    Valid,
    /// <summary>The rule proved the claim impossible against authoritative state — reject it.</summary>
    Reject,
}

/// <summary>
/// A pluggable evaluator of <see cref="OutcomeClaim"/>s against the authoritative <see cref="RoomWorldState"/>.
/// This is the spec's <b>hybrid hook</b>: today's rules do clean-room <em>plausibility / consistency</em>
/// checks (no game formulas), but a future rule can be added or swapped to fully recompute a specific
/// high-value outcome without rearchitecting the pipeline.
///
/// <para><b>Contract — rules MUST fail open:</b> return <see cref="OutcomeJudgment.Valid"/> whenever the
/// rule cannot positively prove a violation (e.g. the target is unknown to the shadow, or a needed field
/// is absent). Only return <see cref="OutcomeJudgment.Reject"/> for outcomes that are impossible given
/// observed authoritative state.</para>
/// </summary>
public interface IOutcomeRule
{
    /// <param name="reason">On <see cref="OutcomeJudgment.Reject"/>, a short human-readable cause for logs.</param>
    OutcomeJudgment Evaluate(in OutcomeClaim claim, RoomWorldState state, out string? reason);
}
