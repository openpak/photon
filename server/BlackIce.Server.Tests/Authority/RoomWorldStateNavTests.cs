using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

/// <summary>The navigation/kind extensions on the shadow world-state: spawns carry a prefab kind + position,
/// and queries (Alive, Nearest) let a bot find real targets. Also the WorldStateObserver parses these from
/// a real-shaped 202 instantiation payload.</summary>
public class RoomWorldStateNavTests
{
    [Fact]
    public void ObserveSpawn_records_kind_and_position()
    {
        var w = new RoomWorldState();
        w.ObserveSpawn(1002, "SpiderEnemy", 10, 2, -5);
        var e = w.Get(1002);
        Assert.NotNull(e);
        Assert.Equal("SpiderEnemy", e!.Kind);
        Assert.Equal(10f, e.X, 3);
        Assert.Equal(2f, e.Y, 3);
        Assert.Equal(-5f, e.Z, 3);
        Assert.True(e.Alive);
    }

    [Fact]
    public void Alive_excludes_destroyed_entities()
    {
        var w = new RoomWorldState();
        w.ObserveSpawn(1, "SpiderEnemy", 0, 0, 0);
        w.ObserveSpawn(2, "CrabEnemy", 1, 0, 0);
        w.ObserveDestroy(1);
        var alive = w.Alive();
        Assert.Single(alive);
        Assert.Equal(2, alive[0].ViewId);
    }

    [Fact]
    public void Nearest_returns_closest_matching_entity_by_xz()
    {
        var w = new RoomWorldState();
        w.ObserveSpawn(1, "SpiderEnemy", 100, 0, 0);
        w.ObserveSpawn(2, "CrabEnemy", 5, 0, 0);     // closest to origin
        w.ObserveSpawn(3, "XPGem", 1, 0, 0);          // closer, but not an enemy
        var nearest = w.Nearest(e => e.Kind!.Contains("Enemy"), 0, 0);
        Assert.NotNull(nearest);
        Assert.Equal(2, nearest!.ViewId);
    }

    [Fact]
    public void Nearest_skips_destroyed_entities()
    {
        var w = new RoomWorldState();
        w.ObserveSpawn(1, "SpiderEnemy", 1, 0, 0);
        w.ObserveSpawn(2, "SpiderEnemy", 50, 0, 0);
        w.ObserveDestroy(1);                          // the closer one is dead
        var nearest = w.Nearest(e => true, 0, 0);
        Assert.Equal(2, nearest!.ViewId);
    }

    [Fact]
    public void Observer_parses_prefab_and_position_from_a_202()
    {
        var w = new RoomWorldState();
        var sut = new WorldStateObserver(w);

        var pos = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(pos.AsSpan(0), 7f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(pos.AsSpan(4), 0f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(pos.AsSpan(8), -3f);
        var pdata = new System.Collections.Generic.Dictionary<object, object>
        {
            { (byte)0, "SpiderEnemy" },                                  // PrefabName
            { (byte)1, new PhotonCustomData(86, pos) },                  // Position (Vector3)
            { (byte)7, 1002 },                                           // ViewId
        };
        var ev = new EventData(202, new System.Collections.Generic.Dictionary<byte, object> { { (byte)245, pdata } });

        var verdict = sut.Intercept(new EventContext("r", 1, ev));
        Assert.Equal(RelayAction.Forward, verdict.Action);
        var e = w.Get(1002);
        Assert.NotNull(e);
        Assert.Equal("SpiderEnemy", e!.Kind);
        Assert.Equal(7f, e.X, 3);
        Assert.Equal(-3f, e.Z, 3);
    }
}
