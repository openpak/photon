using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

public class RoomWorldStateTests
{
    [Fact]
    public void Unknown_entity_is_not_known_and_liveness_is_null()
    {
        var world = new RoomWorldState();
        Assert.False(world.Knows(42));
        Assert.Null(world.IsAlive(42));   // null = never observed (fail-open signal)
        Assert.Null(world.Get(42));
    }

    [Fact]
    public void ObserveSpawn_marks_entity_known_and_alive()
    {
        var world = new RoomWorldState();
        world.ObserveSpawn(7);
        Assert.True(world.Knows(7));
        Assert.True(world.IsAlive(7));
    }

    [Fact]
    public void ObserveDestroy_marks_entity_known_but_not_alive()
    {
        var world = new RoomWorldState();
        world.ObserveSpawn(7);
        world.ObserveDestroy(7);
        Assert.True(world.Knows(7));
        Assert.False(world.IsAlive(7));
    }

    [Fact]
    public void ObserveDestroy_without_prior_spawn_is_known_dead()
    {
        // A destroy we see before any spawn still establishes the entity as known-dead, so a later
        // outcome aimed at it is rejected rather than fail-open.
        var world = new RoomWorldState();
        world.ObserveDestroy(99);
        Assert.True(world.Knows(99));
        Assert.False(world.IsAlive(99));
    }

    [Fact]
    public void Respawn_of_recycled_viewId_revives_entity()
    {
        var world = new RoomWorldState();
        world.ObserveSpawn(7);
        world.ObserveDestroy(7);
        world.ObserveSpawn(7);   // viewId recycled by a new instantiation
        Assert.True(world.IsAlive(7));
    }

    [Fact]
    public void Count_reflects_distinct_entities()
    {
        var world = new RoomWorldState();
        world.ObserveSpawn(1);
        world.ObserveSpawn(2);
        world.ObserveSpawn(1);   // same viewId again: still one entity
        Assert.Equal(2, world.Count);
    }
}
