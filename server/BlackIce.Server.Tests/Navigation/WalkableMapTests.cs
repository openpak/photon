using System.IO;
using BlackIce.Server.Core.Navigation;
using Xunit;

namespace BlackIce.Server.Tests.Navigation;

/// <summary>
/// The runtime-learned walkable model: positions quantized into cubic cells, deduped, and round-tripped
/// through the BWLK file format. Built from real-player positions only (bots aren't physics-validated).
/// </summary>
public class WalkableMapTests
{
    [Fact]
    public void Records_dedupe_into_cells()
    {
        var map = new WalkableMap(cellSize: 3f);
        Assert.True(map.Record(0, 0, 0));          // new cell
        Assert.False(map.Record(1, 1, 1));         // same 3u cell → not new
        Assert.True(map.Record(10, 0, 0));         // a distinct cell
        Assert.Equal(2, map.Count);
        Assert.True(map.Contains(1.4f, 0, 0));     // within the first cell
        Assert.False(map.Contains(100, 0, 0));     // never visited
    }

    [Fact]
    public void Bounds_span_the_recorded_cells()
    {
        // Cell-aligned inputs (multiples of 3) so cell centers equal the inputs — keeps the assertion exact.
        var map = new WalkableMap(3f);
        map.Record(0, 0, 0);
        map.Record(30, 6, -15);
        var b = map.Bounds();
        Assert.Equal(0f, b.minX); Assert.Equal(30f, b.maxX);
        Assert.Equal(-15f, b.minZ); Assert.Equal(0f, b.maxZ);
        Assert.Equal(0f, b.minY); Assert.Equal(6f, b.maxY);
    }

    [Fact]
    public void Empty_map_has_zero_bounds()
    {
        Assert.Equal((0f, 0f, 0f, 0f, 0f, 0f), new WalkableMap().Bounds());
    }

    [Fact]
    public void TryGround_returns_the_nearby_floor_closest_to_the_target_height()
    {
        var map = new WalkableMap(3f);
        map.Record(0, 0, 0);      // ground floor cell at y=0
        map.Record(0, 30, 0);     // an upper floor cell at the same XZ, y=30

        // Near a query at the same XZ: asking near y=2 grounds on the ground floor; near y=28 → upper floor.
        Assert.True(map.TryGround(0.5f, 0.5f, nearY: 2f, xzRadius: 10f, out var low));
        Assert.Equal(0f, low, 3);
        Assert.True(map.TryGround(0.5f, 0.5f, nearY: 28f, xzRadius: 10f, out var high));
        Assert.Equal(30f, high, 3);

        // No walked cell within range → not grounded (outside the explored area).
        Assert.False(map.TryGround(500f, 500f, nearY: 0f, xzRadius: 10f, out _));
    }

    [Fact]
    public void Save_and_load_round_trips_the_cells()
    {
        var map = new WalkableMap(2.5f);
        for (int i = 0; i < 100; i++) map.Record(i * 4f, 2f, (i % 7) * 4f);
        int expected = map.Count;

        using var ms = new MemoryStream();
        map.Save(ms);
        ms.Position = 0;
        var loaded = WalkableMap.Load(ms);

        Assert.Equal(expected, loaded.Count);
        Assert.Equal(2.5f, loaded.CellSize);
        foreach (var (x, y, z) in map.Points())
            Assert.True(loaded.Contains(x, y, z), $"loaded map missing cell ({x},{y},{z})");
    }

    [Fact]
    public void Load_rejects_a_bad_magic()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Throws<InvalidDataException>(() => WalkableMap.Load(ms));
    }
}
