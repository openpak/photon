using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests.Bots;

public class WanderBehaviorTests
{
    [Fact]
    public void Produces_a_changing_position_each_tick()
    {
        var w = new WanderBehavior(startX: 0, startZ: 0, seed: 3);
        var p0 = w.Tick();
        var p1 = w.Tick();
        Assert.NotEqual((p0.X, p0.Z), (p1.X, p1.Z));   // it moves
    }

    [Fact]
    public void Stays_within_a_bounded_radius_of_start()
    {
        var w = new WanderBehavior(startX: 100, startZ: 100, seed: 9, radius: 5);
        for (int i = 0; i < 200; i++)
        {
            var p = w.Tick();
            Assert.InRange(p.X, 95f, 105f);
            Assert.InRange(p.Z, 95f, 105f);
        }
    }
}
