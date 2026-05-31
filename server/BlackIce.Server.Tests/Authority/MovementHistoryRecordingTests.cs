using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

/// <summary>
/// Phase 3c: the movement validator records ACCEPTED positions into the room's lag-comp history, and a
/// snap-corrected teleport is never recorded (apply-after-validate extends to the rewind timeline).
/// Position events are built with the production <see cref="PositionInfo.BuildEvent"/> so the wire shape
/// is exactly what <see cref="PositionInfo.From"/> decodes.
/// </summary>
public class MovementHistoryRecordingTests
{
    private static EventContext Ctx(EventData ev, string room = "r", int actor = 3) => new(room, actor, ev);

    private static MovementValidationInterceptor New(AuthorityStrictness level, float maxUnitsPerSecond, RoomWorldState world)
        => new(maxUnitsPerSecond, new AuthorityPolicy(level),
               new ViolationTracker(int.MaxValue, TimeSpan.FromMinutes(5)), world);

    [Fact]
    public void Accepted_positions_are_recorded_to_world_history()
    {
        var world = new RoomWorldState();
        var sut = New(AuthorityStrictness.Observe, maxUnitsPerSecond: 100000f, world);   // generous cap: not flagged

        sut.Intercept(Ctx(PositionInfo.BuildEvent(1, 0, 0, 0)));   // baseline accepted
        System.Threading.Thread.Sleep(30);
        sut.Intercept(Ctx(PositionInfo.BuildEvent(1, 7, 0, 0)));   // accepted move

        Assert.Equal(2, world.History.SampleCount(1));
        Assert.True(world.TryPositionAt(1, DateTime.UtcNow, out var pos));   // clamp-to-latest
        Assert.Equal(7d, pos.x, 3);                                          // latest = the accepted move
    }

    [Fact]
    public void Observe_records_even_a_flagged_move()
    {
        // Under Observe a teleport is forwarded (no-op) AND the shadow advances — so the rewind history
        // still captures it. (Recording is independent of the verdict except for the snap-correct case.)
        var world = new RoomWorldState();
        var policy = new AuthorityPolicy(AuthorityStrictness.Observe);
        Assert.Equal(RelayAction.Forward, policy.ActionFor(ViolationKind.Movement));   // sanity: Observe forwards
        var sut = new MovementValidationInterceptor(10f, policy,
            new ViolationTracker(int.MaxValue, TimeSpan.FromMinutes(5)), world);

        sut.Intercept(Ctx(PositionInfo.BuildEvent(1, 0, 0, 0)));
        System.Threading.Thread.Sleep(50);
        var v = sut.Intercept(Ctx(PositionInfo.BuildEvent(1, 5000, 0, 0)));   // way over 10 u/s, but Observe

        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Equal(2, world.History.SampleCount(1));
    }

    [Fact]
    public void Snap_corrected_teleport_is_not_recorded()
    {
        var world = new RoomWorldState();
        var sut = New(AuthorityStrictness.Enforce, maxUnitsPerSecond: 100f, world);

        sut.Intercept(Ctx(PositionInfo.BuildEvent(1, 0, 0, 0)));   // baseline accepted -> recorded
        System.Threading.Thread.Sleep(50);
        var verdict = sut.Intercept(Ctx(PositionInfo.BuildEvent(1, 5000, 0, 0)));   // teleport -> snap-corrected

        Assert.Equal(RelayAction.Rewrite, verdict.Action);
        Assert.Equal(1, world.History.SampleCount(1));               // teleport NOT recorded
        Assert.True(world.TryPositionAt(1, DateTime.UtcNow, out var pos));
        Assert.Equal(0d, pos.x, 3);                                  // rewind retains last-good
    }

    [Fact]
    public void Movement_without_world_still_works()
    {
        // The world is optional: the 3-arg ctor (no world) must behave exactly as before — no recording,
        // no NullReferenceException.
        var sut = new MovementValidationInterceptor(100f, AuthorityPolicy.Default,
            new ViolationTracker(int.MaxValue, TimeSpan.FromMinutes(5)));
        var verdict = sut.Intercept(Ctx(PositionInfo.BuildEvent(1, 0, 0, 0)));
        Assert.Equal(RelayAction.Forward, verdict.Action);
    }

    [Fact]
    public void Default_policy_is_observe_noop()
    {
        Assert.Equal(AuthorityStrictness.Observe, AuthorityPolicy.Default.Strictness);
        Assert.Equal(RelayAction.Forward, AuthorityPolicy.Default.ActionFor(ViolationKind.Movement));
        Assert.Equal(RelayAction.Forward, AuthorityPolicy.Default.ActionFor(ViolationKind.Outcome));
    }

    [Fact]
    public void Direct_record_accumulates_samples()
    {
        var world = new RoomWorldState();
        world.RecordPosition(1, 0, 0, 0, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        world.RecordPosition(1, 1, 0, 0, new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));
        Assert.Equal(2, world.History.SampleCount(1));
    }
}
