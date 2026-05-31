using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests;

/// <summary>
/// Phase 2 chaos / stress coverage: high bot counts, rapid spawn/despawn churn of the late-joiner
/// cache, and the bot fan-out path under load. Concurrency storms on the relay live in
/// <see cref="ConnectionStormTests"/>; packet edge cases in <see cref="RelayFuzzTests"/>.
/// </summary>
public class ChaosStressTests
{
    private sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    /// <summary>A real peer that counts every event it is asked to raise, bucketed by event code.</summary>
    internal static PeerConnection CountingPeer(out Dictionary<int, int> byCode)
    {
        var counts = new Dictionary<int, int>();
        byCode = counts;
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new NullHandler(), (_, _) => { });
        p.OnRaised = ev => counts[ev.Code] = counts.TryGetValue(ev.Code, out var n) ? n + 1 : 1;
        return p;
    }

    internal static RoomSession PassthroughSession() =>
        new("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));

    // ---- High bot count -----------------------------------------------------------------------

    [Fact]
    public void Thousands_of_bots_get_unique_actors_and_non_colliding_viewIds()
    {
        const int botCount = 3000;
        var session = PassthroughSession();
        var human = CountingPeer(out var counts); session.Join(1, human);

        var mgr = new BotManager();
        var gen = new BotIdentityGenerator(seed: 7);
        var actors = new HashSet<int>();
        var viewIds = new HashSet<int>();

        for (int i = 0; i < botCount; i++)
        {
            var bot = mgr.Spawn(session, gen.Next());
            Assert.True(actors.Add(bot.Actor), $"duplicate bot actor {bot.Actor}");
            Assert.True(viewIds.Add(bot.ViewId), $"duplicate bot viewId {bot.ViewId}");
            Assert.True(bot.Actor >= BotManager.BotActorBase, "bot actor below reserved base");
        }

        // Bot viewID blocks (actor*1000+1) live far above any real actor's block. A real room never
        // reaches actor 10000, so the lowest bot viewId (10000*1000+1) cannot collide with a human's.
        Assert.True(viewIds.Min() > BotManager.BotActorBase * 1000, "bot viewIds must clear the real-player range");

        // Fan-out: each bot Spawn emits join(255) + props(253) + instantiate(202) + refresh-rpc(200),
        // and the human is the sole recipient. Counts must scale exactly with bot count — no drops.
        Assert.Equal(botCount, counts.GetValueOrDefault(255));
        Assert.Equal(botCount, counts.GetValueOrDefault(253));
        Assert.Equal(botCount, counts.GetValueOrDefault(202));
        Assert.Equal(botCount, counts.GetValueOrDefault(200));
    }

    [Fact]
    public void Ticking_many_bots_relays_one_position_per_bot_per_tick_to_every_human()
    {
        const int botCount = 500, humans = 5, ticks = 10;
        var session = PassthroughSession();
        var counts = new Dictionary<int, int>[humans];
        for (int h = 0; h < humans; h++) { session.Join(h + 1, CountingPeer(out counts[h])); }

        var mgr = new BotManager();
        var gen = new BotIdentityGenerator(seed: 3);
        for (int i = 0; i < botCount; i++) mgr.Spawn(session, gen.Next());

        // Reset the spawn-event counts; measure only position events from here.
        for (int h = 0; h < humans; h++) counts[h].Clear();

        for (int t = 0; t < ticks; t++) mgr.Tick();

        // Every human sees one 201 per bot per tick. No drops, no cross-talk, no exception.
        foreach (var c in counts)
            Assert.Equal(botCount * ticks, c.GetValueOrDefault(201));
    }

    [Fact]
    public void Concurrent_RequestSpawn_from_many_threads_drains_to_unique_actors_on_tick()
    {
        // The console can RequestSpawn from its own thread while the listener thread ticks. Enqueue
        // from many threads, then drain on this single thread — actor reservation happens only in
        // Spawn (listener thread), so all actors must still be unique with none lost.
        const int threads = 12, perThread = 200;
        var session = PassthroughSession();
        var human = CountingPeer(out var counts); session.Join(1, human);
        var mgr = new BotManager();

        Parallel.For(0, threads, t =>
        {
            var gen = new BotIdentityGenerator(seed: 100 + t);
            for (int i = 0; i < perThread; i++) mgr.RequestSpawn(session, gen.Next());
        });

        // Nothing spawned yet — RequestSpawn only enqueues.
        Assert.Equal(0, counts.GetValueOrDefault(202));

        mgr.Tick();   // drains every queued spawn on this (listener) thread

        Assert.Equal(threads * perThread, counts.GetValueOrDefault(202));
    }

    // ---- Rapid spawn / despawn churn of the late-joiner cache ---------------------------------

    private static EventData Spawn(int viewId, string prefab = "Player") =>
        new(202, new() { { 245, new Dictionary<object, object> { { (byte)0, prefab }, { (byte)7, viewId } } } });

    private static EventData Destroy(int viewId) =>
        new(204, new() { { 245, new Dictionary<object, object> { { (byte)7, viewId } } } });

    [Fact]
    public void Rapid_spawn_despawn_churn_leaves_only_live_objects_in_the_replay()
    {
        var session = PassthroughSession();
        var driver = CountingPeer(out _); session.Join(1, driver);

        const int viewIdSpace = 200, rounds = 50;
        var live = new HashSet<int>();
        var rng = new Random(12345);

        // Each round, toggle a random subset of viewIds: spawn the dead ones, destroy the live ones.
        for (int r = 0; r < rounds; r++)
            for (int v = 1000; v < 1000 + viewIdSpace; v++)
                if (rng.Next(2) == 0)
                {
                    if (live.Add(v)) session.RelayFrom(1, Spawn(v));     // was dead -> spawn
                }
                else
                {
                    if (live.Remove(v)) session.RelayFrom(1, Destroy(v)); // was live -> destroy
                }

        // A late joiner must be replayed EXACTLY the set of currently-live viewIds — no resurrected
        // destroyed objects, no missing live ones, no duplicates (proves the cache + order list track
        // spawn/despawn without leaking stale entries).
        var newbie = CountingPeer(out _); session.Join(2, newbie);
        var replayed = new List<int>();
        newbie.OnRaised = ev =>
        {
            var pdata = (Dictionary<object, object>)ev.Parameters[245];
            replayed.Add((int)pdata[(byte)7]);
        };
        session.ReplayCacheTo(2);

        Assert.Equal(live.OrderBy(x => x), replayed.OrderBy(x => x));
        Assert.Equal(replayed.Count, replayed.Distinct().Count());
    }

    [Fact]
    public void Respawning_the_same_viewId_many_times_never_grows_the_replay()
    {
        var session = PassthroughSession();
        var driver = CountingPeer(out _); session.Join(1, driver);

        // Spawn/respawn the same viewId 1000 times — the cache keeps one entry (latest wins), so a
        // late joiner is replayed exactly once. Guards against the order-list / cache growing per
        // re-instantiate (memory leak + duplicate spawns on the client).
        for (int i = 0; i < 1000; i++) session.RelayFrom(1, Spawn(1001, prefab: $"Player{i}"));

        var newbie = CountingPeer(out var counts); session.Join(2, newbie);
        session.ReplayCacheTo(2);

        Assert.Equal(1, counts.GetValueOrDefault(202));
    }

    [Fact]
    public void Many_leave_rejoin_cycles_keep_replay_correct_and_do_not_leak_delivered_state()
    {
        var session = PassthroughSession();
        var driver = CountingPeer(out _); session.Join(1, driver);
        session.RelayFrom(1, Spawn(1001));
        session.RelayFrom(1, Spawn(1002));

        // An actor that reconnects 200 times must get the full world replayed fresh each time (the
        // delivered-set is cleared on Leave). If Leave failed to clear it, the 2nd rejoin would
        // replay nothing.
        for (int cycle = 0; cycle < 200; cycle++)
        {
            var peer = CountingPeer(out var counts); session.Join(2, peer);
            session.ReplayCacheTo(2);
            Assert.Equal(2, counts.GetValueOrDefault(202));
            session.Leave(2);
        }
    }
}
