using System;
using System.Buffers.Binary;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

/// <summary>
/// Phase 3a: the movement interceptor still detects teleport/speedhack, but now ACTS on its verdict
/// per the realm's <see cref="AuthorityPolicy"/>: Observe/Warn forward (log), Enforce/Strict snap-correct
/// (Rewrite a 201 carrying the last-good position). Governing invariants under test:
///   * shadow position only advances to ACCEPTED positions — a rejected teleport never poisons the next
///     frame's speed calculation (apply-after-validate);
///   * fail-open: an unparseable/non-position event is forwarded untouched.
/// </summary>
public class MovementValidationInterceptorTests
{
    private static EventData PosEvent(int viewId, float x, float y, float z)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        var view = new object[] { viewId, false, null!, new PhotonCustomData(86, b) };
        return new EventData(201, new() { { 245, new object[] { 0, null!, view } } });
    }

    private static MovementValidationInterceptor New(
        AuthorityStrictness level, float maxUnitsPerSecond = 50f)
        => new(maxUnitsPerSecond, new AuthorityPolicy(level), new ViolationTracker(int.MaxValue, TimeSpan.FromHours(1)));

    [Fact]
    public void Always_forwards_position_events_under_observe()
    {
        var i = New(AuthorityStrictness.Observe);
        var v = i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0, 0)));
        Assert.Equal(RelayAction.Forward, v.Action);
    }

    [Fact]
    public void Flags_a_teleport_jump_between_two_updates()
    {
        var i = New(AuthorityStrictness.Warn);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0, 0)));
        System.Threading.Thread.Sleep(50);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 10000, 0, 0)));
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Normal_walking_is_not_flagged()
    {
        var i = New(AuthorityStrictness.Warn);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0, 0)));
        System.Threading.Thread.Sleep(50);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 1, 0, 0)));   // ~20 u/s
        Assert.Equal(0, i.FlaggedCount);
    }

    [Fact]
    public void Observe_forwards_even_a_blatant_teleport()
    {
        var i = New(AuthorityStrictness.Observe);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0, 0)));
        System.Threading.Thread.Sleep(50);
        var v = i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 10000, 0, 0)));
        Assert.Equal(RelayAction.Forward, v.Action);
    }

    [Fact]
    public void Enforce_snap_corrects_a_teleport_with_a_rewrite_carrying_last_good_position()
    {
        var i = New(AuthorityStrictness.Enforce);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 5, 6, 7)));   // last good
        System.Threading.Thread.Sleep(50);
        var v = i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 10000, 0, 0)));   // teleport

        Assert.Equal(RelayAction.Rewrite, v.Action);
        var corrected = PositionInfo.From(v.Event!);
        Assert.NotNull(corrected);
        Assert.Equal(1001, corrected!.Value.ViewId);
        Assert.Equal(5f, corrected.Value.X, 3);
        Assert.Equal(6f, corrected.Value.Y, 3);
        Assert.Equal(7f, corrected.Value.Z, 3);
    }

    [Fact]
    public void Shadow_position_does_not_advance_on_a_rejected_frame()
    {
        // After a rejected teleport, the next legitimate-looking small step is measured from the LAST
        // GOOD position (origin), not from the rejected teleport target. If the shadow had advanced to
        // the teleport target, the subsequent frame back near origin would itself look like a teleport.
        var i = New(AuthorityStrictness.Enforce);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0, 0)));   // last good = origin
        System.Threading.Thread.Sleep(50);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 10000, 0, 0)));   // rejected teleport
        System.Threading.Thread.Sleep(50);
        var v = i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 1, 0, 0)));   // small step from origin

        // Measured from origin (last good), 1 unit is fine -> forwarded, not re-flagged.
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Equal(1, i.FlaggedCount);   // only the teleport was flagged
    }

    [Fact]
    public void Non_position_events_are_forwarded_untouched_fail_open()
    {
        var i = New(AuthorityStrictness.Enforce);
        var ev = new EventData(202, new());
        var v = i.Intercept(new EventContext("co-op", 1, ev));
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Same(ev, v.Event);
    }

    [Fact]
    public void First_position_for_a_view_is_forwarded_no_baseline_yet()
    {
        // Can't judge speed with only one sample -> forward (fail-open).
        var i = New(AuthorityStrictness.Enforce);
        var v = i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 99999, 0, 0)));
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Equal(0, i.FlaggedCount);
    }
}
