using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class KillBusTests
{
    [Fact]
    public void PublishDeath_invokes_the_Died_subscribers_with_the_notice()
    {
        var bus = new KillBus();
        DeathNotice? seen = null;
        bus.Died += n => seen = n;

        bus.PublishDeath(new DeathNotice("co-op", 6));

        Assert.NotNull(seen);
        Assert.Equal("co-op", seen!.Value.Room);
        Assert.Equal(6, seen.Value.Victim);
    }
}
