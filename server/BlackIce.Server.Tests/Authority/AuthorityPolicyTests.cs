using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

/// <summary>
/// Phase 3a: the <see cref="AuthorityPolicy"/> maps (strictness level × violation type) to a concrete
/// <see cref="RelayAction"/>. The mapping is the heart of the rollout posture: Observe/Warn never act,
/// Enforce/Strict snap-correct movement (Rewrite) and drop bad outcomes (Drop).
/// </summary>
public class AuthorityPolicyTests
{
    [Theory]
    // Movement violations: forwarded at Observe/Warn, snap-corrected (Rewrite) at Enforce/Strict.
    [InlineData(AuthorityStrictness.Observe, ViolationKind.Movement, RelayAction.Forward)]
    [InlineData(AuthorityStrictness.Warn, ViolationKind.Movement, RelayAction.Forward)]
    [InlineData(AuthorityStrictness.Enforce, ViolationKind.Movement, RelayAction.Rewrite)]
    [InlineData(AuthorityStrictness.Strict, ViolationKind.Movement, RelayAction.Rewrite)]
    // Outcome violations: forwarded at Observe/Warn, dropped at Enforce/Strict.
    [InlineData(AuthorityStrictness.Observe, ViolationKind.Outcome, RelayAction.Forward)]
    [InlineData(AuthorityStrictness.Warn, ViolationKind.Outcome, RelayAction.Forward)]
    [InlineData(AuthorityStrictness.Enforce, ViolationKind.Outcome, RelayAction.Drop)]
    [InlineData(AuthorityStrictness.Strict, ViolationKind.Outcome, RelayAction.Drop)]
    public void Maps_strictness_and_violation_to_action(
        AuthorityStrictness level, ViolationKind kind, RelayAction expected)
    {
        var policy = new AuthorityPolicy(level);
        Assert.Equal(expected, policy.ActionFor(kind));
    }

    [Theory]
    // A violation is only "counted" (logged + tallied) from Warn upward; Observe is pure forward.
    [InlineData(AuthorityStrictness.Observe, false)]
    [InlineData(AuthorityStrictness.Warn, true)]
    [InlineData(AuthorityStrictness.Enforce, true)]
    [InlineData(AuthorityStrictness.Strict, true)]
    public void Counts_violations_from_warn_upward(AuthorityStrictness level, bool expected)
    {
        Assert.Equal(expected, new AuthorityPolicy(level).CountsViolations);
    }

    [Theory]
    // Escalation (suppression/kick) is a Strict-only behavior.
    [InlineData(AuthorityStrictness.Observe, false)]
    [InlineData(AuthorityStrictness.Warn, false)]
    [InlineData(AuthorityStrictness.Enforce, false)]
    [InlineData(AuthorityStrictness.Strict, true)]
    public void Escalates_only_at_strict(AuthorityStrictness level, bool expected)
    {
        Assert.Equal(expected, new AuthorityPolicy(level).Escalates);
    }

    [Fact]
    public void Default_is_observe()
    {
        Assert.Equal(AuthorityStrictness.Observe, AuthorityPolicy.Default.Strictness);
        Assert.Equal(RelayAction.Forward, AuthorityPolicy.Default.ActionFor(ViolationKind.Movement));
        Assert.Equal(RelayAction.Forward, AuthorityPolicy.Default.ActionFor(ViolationKind.Outcome));
    }

    // ---- Parsing per-realm strictness from Realm.ExtraJson --------------------------------------

    [Fact]
    public void Parses_strictness_from_extra_json()
    {
        var p = AuthorityPolicy.FromExtraJson("{\"authority\":{\"strictness\":\"Enforce\"}}");
        Assert.Equal(AuthorityStrictness.Enforce, p.Strictness);
    }

    [Fact]
    public void Parses_strictness_case_insensitively()
    {
        var p = AuthorityPolicy.FromExtraJson("{\"authority\":{\"strictness\":\"strict\"}}");
        Assert.Equal(AuthorityStrictness.Strict, p.Strictness);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"authority\":{}}")]
    [InlineData("not json at all")]                       // fail-open: garbage -> Observe
    [InlineData("{\"authority\":{\"strictness\":\"bogus\"}}")] // unknown level -> Observe
    public void Defaults_to_observe_when_unset_or_invalid(string? extraJson)
    {
        Assert.Equal(AuthorityStrictness.Observe, AuthorityPolicy.FromExtraJson(extraJson).Strictness);
    }
}
