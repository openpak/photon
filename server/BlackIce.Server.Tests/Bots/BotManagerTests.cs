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
    public void CountIn_tracks_bots_per_room()
    {
        var mgr = new BotManager();
        Assert.Equal(0, mgr.CountIn("co-op"));

        mgr.Spawn(Session(), new BotIdentityGenerator(seed: 1).Next());
        mgr.Spawn(Session(), new BotIdentityGenerator(seed: 2).Next());
        Assert.Equal(2, mgr.CountIn("co-op"));          // both Session()s are room "co-op"
        Assert.Equal(0, mgr.CountIn("other-room"));
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

    [Fact]
    public void Bot_avatar_202_carries_a_spawn_position_so_the_client_does_not_render_it_at_origin()
    {
        // Regression: bots used to omit the 202 position, so PUN spawned every avatar at world origin and
        // 10 bots merged into one stack. PlayerBot.Spawn must now put a Vector3 position (key 1) in the 202.
        var session = Session();
        var human = RealPeer(out var raised); session.Join(1, human);

        new PlayerBot(5, new BotIdentityGenerator(seed: 1).Next()).Spawn(session, x: 7f, y: 0f, z: -3f);

        var inst = raised.Find(r => r.ev.Code == PhotonCodes.PunEvent.Instantiation).ev;
        Assert.NotNull(inst);
        var pdata = Assert.IsAssignableFrom<IDictionary<object, object>>(inst.Parameters[PhotonCodes.Param.Data]);
        Assert.True(pdata.ContainsKey(PhotonCodes.InstantiationKey.Position), "202 must carry a position (key 1)");
        var pos = Assert.IsType<PhotonCustomData>(pdata[PhotonCodes.InstantiationKey.Position]);
        Assert.Equal(PhotonCodes.CustomType.Vector3, pos.Code);
        float x = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(pos.Data.AsSpan(0, 4));
        float z = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(pos.Data.AsSpan(8, 4));
        Assert.Equal(7f, x, 3);
        Assert.Equal(-3f, z, 3);
    }

    [Fact]
    public void Auto_spawned_bots_get_distinct_spawn_positions()
    {
        // The manager's spiral placement gives each bot a different spot, so they don't visually stack.
        var session = Session();
        var human = RealPeer(out var raised); session.Join(1, human);
        var mgr = new BotManager();
        mgr.Spawn(session, new BotIdentityGenerator(seed: 1).Next());
        mgr.Spawn(session, new BotIdentityGenerator(seed: 2).Next());

        var positions = new List<(float, float)>();
        foreach (var (ev, _) in raised)
            if (ev.Code == PhotonCodes.PunEvent.Instantiation
                && ev.Parameters[PhotonCodes.Param.Data] is IDictionary<object, object> pd
                && pd.TryGetValue(PhotonCodes.InstantiationKey.Position, out var p) && p is PhotonCustomData v)
            {
                float x = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(v.Data.AsSpan(0, 4));
                float z = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(v.Data.AsSpan(8, 4));
                positions.Add((x, z));
            }
        Assert.Equal(2, positions.Count);
        Assert.NotEqual(positions[0], positions[1]);   // distinct, not stacked
    }
}
