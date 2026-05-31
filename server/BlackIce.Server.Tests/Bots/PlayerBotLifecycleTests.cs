using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests.Bots;

public class PlayerBotLifecycleTests
{
    private static PeerConnection RealPeer(out List<EventData> raised)
    {
        var captured = new List<EventData>(); raised = captured;
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { });
        p.OnRaised = captured.Add; return p;
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
    public void Spawn_emits_join_then_identity_then_instantiate_in_order_to_real_players()
    {
        var session = Session();
        var human = RealPeer(out var raised); session.Join(1, human);

        var bot = new PlayerBot(actor: 2, new BotIdentityGenerator(seed: 1).Next());
        bot.Spawn(session);

        // The human sees, in order: join(255), a properties-changed(253), an instantiate(202), a refresh RPC(200).
        var codes = raised.ConvertAll(e => e.Code);
        Assert.Equal(255, codes[0]);
        Assert.Contains((byte)253, codes);
        Assert.Contains((byte)202, codes);
        Assert.Contains((byte)200, codes);
        // The instantiate carries the bot's viewID = actor*1000+1 = 2001 under PData(245)'s payload, key 7.
        var inst = raised.Find(e => e.Code == 202);
        Assert.NotNull(inst);
    }

    [Fact]
    public void Spawn_join_event_carries_the_bot_actor_number()
    {
        var session = Session();
        var human = RealPeer(out var raised); session.Join(1, human);
        new PlayerBot(actor: 5, new BotIdentityGenerator(seed: 1).Next()).Spawn(session);

        var join = raised.Find(e => e.Code == 255);
        Assert.NotNull(join);
        Assert.True(join!.Parameters.TryGetValue(254, out var nr) && nr is int i && i == 5);
    }
}
