using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

public class WorldSnapshotHistoryTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void No_samples_returns_false()
    {
        var h = new WorldSnapshotHistory();
        Assert.False(h.TryPositionAt(1, T0, out _));
    }

    [Fact]
    public void Single_sample_is_returned_for_any_time()
    {
        var h = new WorldSnapshotHistory();
        h.Record(1, 3, 4, 5, T0);
        Assert.True(h.TryPositionAt(1, T0.AddSeconds(-10), out var before));
        Assert.Equal((3f, 4f, 5f), before);
        Assert.True(h.TryPositionAt(1, T0.AddSeconds(10), out var after));
        Assert.Equal((3f, 4f, 5f), after);
    }

    [Fact]
    public void Interpolates_between_two_samples()
    {
        var h = new WorldSnapshotHistory();
        h.Record(1, 0, 0, 0, T0);
        h.Record(1, 10, 20, -40, T0.AddSeconds(2));
        Assert.True(h.TryPositionAt(1, T0.AddSeconds(1), out var mid));   // halfway
        Assert.Equal(5d, mid.x, 3);
        Assert.Equal(10d, mid.y, 3);
        Assert.Equal(-20d, mid.z, 3);
    }

    [Fact]
    public void Clamps_outside_the_window()
    {
        var h = new WorldSnapshotHistory();
        h.Record(1, 0, 0, 0, T0);
        h.Record(1, 10, 0, 0, T0.AddSeconds(2));
        Assert.True(h.TryPositionAt(1, T0.AddSeconds(-5), out var before));
        Assert.Equal(0d, before.x, 3);
        Assert.True(h.TryPositionAt(1, T0.AddSeconds(5), out var after));
        Assert.Equal(10d, after.x, 3);
    }

    [Fact]
    public void Evicts_oldest_beyond_capacity()
    {
        var h = new WorldSnapshotHistory(capacity: 2);
        h.Record(1, 0, 0, 0, T0);
        h.Record(1, 10, 0, 0, T0.AddSeconds(1));
        h.Record(1, 20, 0, 0, T0.AddSeconds(2));   // evicts the T0 sample
        Assert.Equal(2, h.SampleCount(1));
        // Earliest retained sample is now T0+1s (x=10); a query before it clamps to 10, not the evicted 0.
        Assert.True(h.TryPositionAt(1, T0, out var clamped));
        Assert.Equal(10d, clamped.x, 3);
    }

    [Fact]
    public void Entities_are_independent()
    {
        var h = new WorldSnapshotHistory();
        h.Record(1, 1, 0, 0, T0);
        h.Record(2, 2, 0, 0, T0);
        Assert.True(h.TryPositionAt(1, T0, out var a));
        Assert.True(h.TryPositionAt(2, T0, out var b));
        Assert.Equal(1d, a.x, 3);
        Assert.Equal(2d, b.x, 3);
    }

    [Fact]
    public void Capacity_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldSnapshotHistory(0));
    }
}
