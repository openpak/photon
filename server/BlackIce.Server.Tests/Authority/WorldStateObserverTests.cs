using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

public class WorldStateObserverTests
{
    private static EventData SpawnDestroyEvent(byte code, int viewId)
    {
        var pdata = new Dictionary<object, object> { { (byte)7, viewId } };   // KeyViewId
        return new EventData(code, new Dictionary<byte, object> { { (byte)245, pdata } });
    }

    private static EventContext Ctx(EventData ev, string room = "r", int actor = 2) => new(room, actor, ev);

    [Fact]
    public void Instantiation_event_marks_entity_alive()
    {
        var world = new RoomWorldState();
        var sut = new WorldStateObserver(world);
        var verdict = sut.Intercept(Ctx(SpawnDestroyEvent(202, 11)));
        Assert.Equal(RelayAction.Forward, verdict.Action);
        Assert.True(world.IsAlive(11));
    }

    [Fact]
    public void Destroy_event_marks_entity_dead()
    {
        var world = new RoomWorldState();
        var sut = new WorldStateObserver(world);
        sut.Intercept(Ctx(SpawnDestroyEvent(202, 11)));
        sut.Intercept(Ctx(SpawnDestroyEvent(204, 11)));
        Assert.False(world.IsAlive(11));
    }

    [Fact]
    public void Unrelated_event_changes_nothing_and_forwards()
    {
        var world = new RoomWorldState();
        var sut = new WorldStateObserver(world);
        // A position event (201) is not a spawn/destroy: the observer ignores it.
        var ev = new EventData(201, new Dictionary<byte, object>());
        var verdict = sut.Intercept(Ctx(ev));
        Assert.Equal(RelayAction.Forward, verdict.Action);
        Assert.Equal(0, world.Count);
    }

    [Fact]
    public void Spawn_without_resolvable_viewId_is_ignored()
    {
        var world = new RoomWorldState();
        var sut = new WorldStateObserver(world);
        // 202 with no PData/viewId: best-effort read fails, entity stays unknown (fail-open).
        var ev = new EventData(202, new Dictionary<byte, object>());
        sut.Intercept(Ctx(ev));
        Assert.Equal(0, world.Count);
    }
}
