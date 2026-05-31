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
}
