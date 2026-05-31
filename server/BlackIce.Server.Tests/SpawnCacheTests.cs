using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

/// <summary>
/// Late-joiner spawn cache: PUN sends instantiation events (code 202) with EventCaching.AddToRoomCache
/// so the Photon server can REPLAY them to clients that join later. Our relay must mirror that — cache
/// each 202 keyed by viewID (latest wins), evict on destroy (204), and replay to a newly joined actor.
/// </summary>
public class SpawnCacheTests
{
    private static PeerConnection Peer(out List<EventData> raised)
    {
        var captured = new List<EventData>();
        raised = captured;
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0),
                                   new NullHandler(), (_, _) => { });
        p.OnRaised = captured.Add;
        return p;
    }

    private sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    private static RoomSession NewSession() =>
        new("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));

    /// <summary>Builds a PUN instantiation (202) event whose PData(245) hashtable carries the viewID at key 7.</summary>
    private static EventData Spawn(int viewId, string prefab = "Player") =>
        new(202, new() { { 245, new Dictionary<object, object> { { (byte)0, prefab }, { (byte)7, viewId } } } });

    [Fact]
    public void Cached_spawn_is_replayed_to_a_late_joiner()
    {
        var session = NewSession();
        var a = Peer(out _); session.Join(1, a);

        session.RelayFrom(1, Spawn(viewId: 1001));

        var b = Peer(out var bRaised); session.Join(2, b);
        session.ReplayCacheTo(2);

        var ev = Assert.Single(bRaised);
        Assert.Equal(202, ev.Code);
    }

    [Fact]
    public void Replay_goes_only_to_the_new_actor_not_existing_actors()
    {
        var session = NewSession();
        var a = Peer(out var aRaised); session.Join(1, a);

        session.RelayFrom(1, Spawn(viewId: 1001));   // sender does not get its own spawn back
        Assert.Empty(aRaised);

        var b = Peer(out _); session.Join(2, b);
        session.ReplayCacheTo(2);

        // Existing actor 1 receives no duplicate from the replay.
        Assert.Empty(aRaised);
    }

    [Fact]
    public void Second_spawn_for_same_viewId_replaces_the_first()
    {
        var session = NewSession();
        var a = Peer(out _); session.Join(1, a);

        session.RelayFrom(1, Spawn(viewId: 1001, prefab: "PlayerOld"));
        session.RelayFrom(1, Spawn(viewId: 1001, prefab: "PlayerNew"));

        var b = Peer(out var bRaised); session.Join(2, b);
        session.ReplayCacheTo(2);

        var ev = Assert.Single(bRaised);   // one entry per viewID, latest wins
        var pdata = Assert.IsType<Dictionary<object, object>>(ev.Parameters[245]);
        Assert.Equal("PlayerNew", pdata[(byte)0]);
    }

    [Fact]
    public void Multiple_distinct_spawns_replay_in_insertion_order()
    {
        var session = NewSession();
        var a = Peer(out _); session.Join(1, a);

        session.RelayFrom(1, Spawn(viewId: 1001, prefab: "First"));
        session.RelayFrom(1, Spawn(viewId: 1002, prefab: "Second"));

        var b = Peer(out var bRaised); session.Join(2, b);
        session.ReplayCacheTo(2);

        Assert.Equal(2, bRaised.Count);
        Assert.Equal("First", ((Dictionary<object, object>)bRaised[0].Parameters[245])[(byte)0]);
        Assert.Equal("Second", ((Dictionary<object, object>)bRaised[1].Parameters[245])[(byte)0]);
    }

    [Fact]
    public void Destroy_204_evicts_the_cached_spawn_so_it_is_not_replayed()
    {
        var session = NewSession();
        var a = Peer(out _); session.Join(1, a);

        session.RelayFrom(1, Spawn(viewId: 1001));
        // PUN destroy (204) references the viewID in its PData(245) payload.
        var destroy = new EventData(204, new() { { 245, new Dictionary<object, object> { { (byte)0, 1001 } } } });
        session.RelayFrom(1, destroy);

        var b = Peer(out var bRaised); session.Join(2, b);
        session.ReplayCacheTo(2);

        Assert.Empty(bRaised);   // the destroyed object is not replayed
    }

    [Fact]
    public void Spawn_without_resolvable_viewId_is_still_cached_and_replayed()
    {
        var session = NewSession();
        var a = Peer(out _); session.Join(1, a);

        // 202 whose PData has no viewID key 7 — must still replay (under a synthetic key), not be dropped.
        session.RelayFrom(1, new EventData(202, new() { { 245, new Dictionary<object, object> { { (byte)0, "Orphan" } } } }));

        var b = Peer(out var bRaised); session.Join(2, b);
        session.ReplayCacheTo(2);

        Assert.Single(bRaised);
    }

    [Fact]
    public void Newcomer_gets_a_spawn_exactly_once_across_live_relay_and_replay()
    {
        // Reproduces the Join->Replay double-spawn window: a live 202 for a viewID is relayed to the
        // now-member newcomer AND that same viewID is also in the cache, so a naive ReplayCacheTo would
        // deliver it a second time -> double instantiate of one viewID on the client.
        var session = NewSession();
        var a = Peer(out _); session.Join(1, a);

        session.RelayFrom(1, Spawn(viewId: 1001));        // actor 1 spawns; cached

        var b = Peer(out var bRaised); session.Join(2, b);
        session.RelayFrom(1, Spawn(viewId: 1001));        // a SECOND live relay of the SAME viewID reaches member 2
        session.ReplayCacheTo(2);                         // replay must not re-deliver viewId 1001 to actor 2

        Assert.Single(bRaised, e => e.Code == 202);       // exactly once total across live + replay
    }

    [Fact]
    public void Cached_before_join_replays_to_newcomer_exactly_once()
    {
        var session = NewSession();
        var a = Peer(out _); session.Join(1, a);

        session.RelayFrom(1, Spawn(viewId: 1001));        // cached before actor 2 exists

        var b = Peer(out var bRaised); session.Join(2, b);
        session.ReplayCacheTo(2);

        Assert.Single(bRaised, e => e.Code == 202);       // delivered once via replay
    }

    [Fact]
    public void Delivered_set_is_cleared_on_leave_so_a_rejoin_gets_the_spawn_again()
    {
        var session = NewSession();
        var a = Peer(out _); session.Join(1, a);
        session.RelayFrom(1, Spawn(viewId: 1001));

        var b = Peer(out var bRaised); session.Join(2, b);
        session.ReplayCacheTo(2);
        Assert.Single(bRaised, e => e.Code == 202);

        // Actor 2 leaves and rejoins (e.g. reconnect): the fresh peer must receive the spawn again.
        session.Leave(2);
        var b2 = Peer(out var b2Raised); session.Join(2, b2);
        session.ReplayCacheTo(2);
        Assert.Single(b2Raised, e => e.Code == 202);
    }

    [Fact]
    public void Late_joiner_through_handler_receives_existing_spawns()
    {
        var db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "co-op", IsEnabled = true });
        db.Context.SaveChanges();
        using (db)
        {
            var reg = new RoomRegistry();
            var h = new GameServerHandler("s", reg, allowAnonymousLan: true, realms: new RealmService(db.Context));

            var a = Peer(out _);
            h.OnOperationRequest(a, new OperationRequest(226, new() { { 255, "co-op" } }));   // actor 1 joins

            // Actor 1 instantiates a networked object (202) via OpRaiseEvent.
            h.OnOperationRequest(a, new OperationRequest(253, new()
            {
                { 244, (byte)202 },
                { 245, new Dictionary<object, object> { { (byte)0, "Player" }, { (byte)7, 1001 } } },
            }));

            // Actor 2 joins AFTER the spawn — its peer must receive the cached 202.
            var b = Peer(out var bRaised);
            h.OnOperationRequest(b, new OperationRequest(226, new() { { 255, "co-op" } }));

            Assert.Contains(bRaised, e => e.Code == 202);
        }
    }
}
