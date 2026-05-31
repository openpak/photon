using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

public class DeadTargetOutcomeRuleTests
{
    private static readonly DeadTargetOutcomeRule Rule = new();

    [Fact]
    public void Claim_without_target_is_valid()
    {
        var world = new RoomWorldState();
        var claim = new OutcomeClaim(SenderActor: 1, TargetViewId: null, Damage: 50f, Method: "TakeDamage");
        Assert.Equal(OutcomeJudgment.Valid, Rule.Evaluate(in claim, world, out _));
    }

    [Fact]
    public void Unknown_target_is_valid_failopen()
    {
        var world = new RoomWorldState();   // never observed view 5
        var claim = new OutcomeClaim(1, 5, 50f, "TakeDamage");
        Assert.Equal(OutcomeJudgment.Valid, Rule.Evaluate(in claim, world, out _));
    }

    [Fact]
    public void Alive_target_is_valid()
    {
        var world = new RoomWorldState();
        world.ObserveSpawn(5);
        var claim = new OutcomeClaim(1, 5, 50f, "TakeDamage");
        Assert.Equal(OutcomeJudgment.Valid, Rule.Evaluate(in claim, world, out _));
    }

    [Fact]
    public void Dead_target_is_rejected_with_reason()
    {
        var world = new RoomWorldState();
        world.ObserveSpawn(5);
        world.ObserveDestroy(5);
        var claim = new OutcomeClaim(1, 5, 50f, "TakeDamage");
        Assert.Equal(OutcomeJudgment.Reject, Rule.Evaluate(in claim, world, out var reason));
        Assert.NotNull(reason);
        Assert.Contains("5", reason);
    }
}
