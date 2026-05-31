using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests.Bots;

public class BotManagerTests
{
    private static PeerConnection RealPeer(out List<(EventData ev, bool unreliable)> raised)
    {
        var captured = new List<(EventData, bool)>(); raised = captured;
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { });
        p.OnRaisedClassified = (ev, u) => captured.Add((ev, u)); return p;
    }
    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }
    private static RoomSession Session() =>
        new("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));

    [Fact]
    public void Spawned_bot_gets_a_high_non_colliding_actor_number()
    {
        var mgr = new BotManager();
        var bot = mgr.Spawn(Session(), new BotIdentityGenerator(seed: 1).Next());
        Assert.True(bot.Actor >= 10000, "bot actors live in a high range that can't collide with real actors");
    }

    [Fact]
    public void RequestSpawn_defers_actual_spawn_until_the_next_tick()
    {
        var session = Session();
        var human = RealPeer(out var raised); session.Join(1, human);
        var mgr = new BotManager();

        // RequestSpawn must NOT touch the relay or _bots on the calling (console) thread.
        mgr.RequestSpawn(session, new BotIdentityGenerator(seed: 1).Next());
        Assert.Empty(raised);

        // The deferred spawn runs on the listener thread when Tick() drains the queue: the bot's
        // join/instantiate relay reaches the real member, proving the queued path spawns the bot.
        mgr.Tick();
        Assert.NotEmpty(raised);
        // And the spawned bot then moves (201) on subsequent/same ticks.
        Assert.Contains(raised, r => r.ev.Code == 201 && r.unreliable);
    }

    [Fact]
    public void Tick_relays_an_unreliable_position_event_for_the_bot()
    {
        var session = Session();
        var human = RealPeer(out var raised); session.Join(1, human);
        var mgr = new BotManager();
        mgr.Spawn(session, new BotIdentityGenerator(seed: 1).Next());
        raised.Clear();

        mgr.Tick();

        // A position event (201) for the bot, sent unreliably (like real movement).
        Assert.Contains(raised, r => r.ev.Code == 201 && r.unreliable);
    }
}
