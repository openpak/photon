using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Bots;
using BlackIce.Server.LoadBalancing.Plugins;
using Xunit;

namespace BlackIce.Server.Tests.Commands;

/// <summary>Covers the bot-control console verbs (count/spawn/despawn/smart) and the underlying
/// <see cref="BotManager.Despawn"/> the despawn verb queues.</summary>
public class BotControlCommandsTests
{
    private static (CommandRegistry reg, RoomRegistry rooms, BotManager bots, AdminActionQueue admin) Setup()
    {
        var rooms = new RoomRegistry();
        var bots = new BotManager();
        var admin = new AdminActionQueue();
        var reg = new CommandRegistry().Register(
            new BotControlCommands(rooms, bots, admin, new BotIdentityGenerator()));
        return (reg, rooms, bots, admin);
    }

    // A real peer that records the (event, unreliable) pairs the relay fans to it — same rig as BotManagerTests.
    private static PeerConnection RealPeer()
    {
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { });
        p.OnRaisedClassified = (_, _) => { };
        return p;
    }
    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    [Fact]
    public void Botsmart_on_sets_the_smart_flag()
    {
        var (reg, _, bots, _) = Setup();
        Assert.False(bots.Smart);

        Assert.True(reg.TryExecute("botsmart on", PlayerLevel.Console, out var o));
        Assert.True(bots.Smart);
        Assert.Contains("on", o);

        reg.TryExecute("botsmart off", PlayerLevel.Console, out _);
        Assert.False(bots.Smart);
    }

    [Fact]
    public void Bots_reports_the_count_for_a_created_room()
    {
        var (reg, rooms, _, _) = Setup();
        rooms.GetOrCreate("co-op");

        Assert.True(reg.TryExecute("bots co-op", PlayerLevel.Console, out var o));
        Assert.Contains("co-op", o);
        Assert.Contains("0 bot(s)", o);
    }

    [Fact]
    public void Despawn_returns_a_queued_message_for_a_known_room()
    {
        var (reg, rooms, _, _) = Setup();
        rooms.GetOrCreate("co-op");

        Assert.True(reg.TryExecute("despawn co-op", PlayerLevel.Console, out var o));
        Assert.Contains("queued despawn", o);
    }

    [Fact]
    public void BotManager_Despawn_drops_the_bot_count_and_relays_a_destroy_and_leave()
    {
        // Spawn a bot into a "co-op" session with a real member, then despawn it and assert the count drops.
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));
        session.Join(1, RealPeer());
        var bots = new BotManager();
        bots.Spawn(session, new BotIdentityGenerator(seed: 1).Next());
        Assert.Equal(1, bots.CountIn("co-op"));

        int removed = bots.Despawn("co-op");

        Assert.Equal(1, removed);
        Assert.Equal(0, bots.CountIn("co-op"));
    }

    [Fact]
    public void BotManager_Despawn_honors_the_max_limit()
    {
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));
        session.Join(1, RealPeer());
        var bots = new BotManager();
        bots.Spawn(session, new BotIdentityGenerator(seed: 1).Next());
        bots.Spawn(session, new BotIdentityGenerator(seed: 2).Next());
        Assert.Equal(2, bots.CountIn("co-op"));

        Assert.Equal(1, bots.Despawn("co-op", max: 1));   // only one removed
        Assert.Equal(1, bots.CountIn("co-op"));
    }
}
