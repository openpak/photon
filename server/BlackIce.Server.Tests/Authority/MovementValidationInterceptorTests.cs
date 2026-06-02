using System.Buffers.Binary;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

public class MovementValidationInterceptorTests
{
    private static EventData PosEvent(int viewId, float x, float z)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        var view = new object[] { viewId, false, null!, new PhotonCustomData(86, b), new PhotonCustomData(81, new byte[16]) };
        return new EventData(201, new() { { 245, new object[] { 0, null!, view } } });
    }

    [Fact]
    public void Always_forwards_position_events()
    {
        var i = new MovementValidationInterceptor(maxUnitsPerSecond: 50f);
        var v = i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0)));
        Assert.Equal(RelayAction.Forward, v.Action);
    }

    [Fact]
    public void Exempt_actor_is_never_flagged_or_dropped()
    {
        // A verified-admin exemption: actor 1 is exempt, actor 2 is not. Both make an impossible jump.
        var i = new MovementValidationInterceptor(maxUnitsPerSecond: 50f, maxTeleportDistance: 100f,
                                                  enforce: true, isExempt: (room, actor) => actor == 1);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0)));
        System.Threading.Thread.Sleep(50);
        var exempt = i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 10000, 0)));   // would be a speedhack
        Assert.Equal(RelayAction.Forward, exempt.Action);   // exempt → forwarded despite the jump
        Assert.Equal(0, i.FlaggedCount);

        i.Intercept(new EventContext("co-op", 2, PosEvent(2001, 0, 0)));
        System.Threading.Thread.Sleep(50);
        var enforced = i.Intercept(new EventContext("co-op", 2, PosEvent(2001, 10000, 0)));
        Assert.Equal(RelayAction.Drop, enforced.Action);     // non-exempt → dropped
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Flags_a_teleport_jump_between_two_updates()
    {
        // Two updates ~0.1s apart (the interceptor uses wall-clock between calls); a 10000-unit jump
        // is far over 50 u/s. First call establishes baseline; second is flagged.
        var i = new MovementValidationInterceptor(maxUnitsPerSecond: 50f);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0)));
        System.Threading.Thread.Sleep(50);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 10000, 0)));
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Normal_walking_is_not_flagged()
    {
        var i = new MovementValidationInterceptor(maxUnitsPerSecond: 50f);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0)));
        System.Threading.Thread.Sleep(50);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 1, 0)));   // 1 unit in ~0.05s = 20 u/s
        Assert.Equal(0, i.FlaggedCount);
    }

    [Fact]
    public void Teleport_distance_is_flagged_independent_of_timing()
    {
        // Huge per-step jump but generous speed limit: the teleport-distance check catches it.
        var i = new MovementValidationInterceptor(maxUnitsPerSecond: float.MaxValue, maxTeleportDistance: 100f);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0)));
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 5000, 0)));   // 5000u jump > 100
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Non_finite_position_is_flagged_and_does_not_poison_the_baseline()
    {
        var i = new MovementValidationInterceptor(maxUnitsPerSecond: 50f, maxTeleportDistance: 100f);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0)));
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, float.NaN, 0)));   // garbage coordinate
        Assert.Equal(1, i.FlaggedCount);
        // A normal step from the original baseline is still fine (NaN never became the baseline).
        System.Threading.Thread.Sleep(50);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 1, 0)));
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Drops_violation_when_enforcing()
    {
        var i = new MovementValidationInterceptor(maxUnitsPerSecond: 50f, maxTeleportDistance: 100f, enforce: true);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0)));
        Assert.Equal(RelayAction.Drop, i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 5000, 0))).Action);
    }
}
