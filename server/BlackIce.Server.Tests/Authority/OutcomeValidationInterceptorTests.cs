using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

public class OutcomeValidationInterceptorTests
{
    // PunRpcInfo.From reads: target viewId at RPC key 0, method at key 3, args at key 4 (a DamagePacket
    // custom type, code 68, whose first 4 bytes are a big-endian float). Mirror that exact shape.
    private static EventData OutcomeEvent(int targetViewId, float damage = 50f, string method = "TakeDamage")
    {
        var dmg = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(dmg, damage);
        var rpc = new Dictionary<object, object>
        {
            { (byte)0, targetViewId },                                  // RpcKey.ViewId (target)
            { (byte)3, method },                                        // RpcKey.MethodName
            { (byte)4, new object[] { new PhotonCustomData(68, dmg) } },// RpcKey.Args: DamagePacket
        };
        return new EventData(200, new Dictionary<byte, object> { { (byte)245, rpc } });
    }

    private static EventContext Ctx(EventData ev, string room = "r", int actor = 9) => new(room, actor, ev);

    private static OutcomeValidationInterceptor Sut(RoomWorldState world, bool enforce) =>
        new(world, new IOutcomeRule[] { new DeadTargetOutcomeRule() }, enforce);

    private static RoomWorldState WorldWithDead(int viewId)
    {
        var world = new RoomWorldState();
        world.ObserveSpawn(viewId);
        world.ObserveDestroy(viewId);
        return world;
    }

    [Fact]
    public void Non_outcome_event_is_forwarded()
    {
        var sut = Sut(new RoomWorldState(), enforce: true);
        var ev = new EventData(201, new Dictionary<byte, object>());   // not a 200 RPC
        Assert.Equal(RelayAction.Forward, sut.Intercept(Ctx(ev)).Action);
    }

    [Fact]
    public void Outcome_to_alive_target_is_forwarded()
    {
        var world = new RoomWorldState();
        world.ObserveSpawn(5);
        var sut = Sut(world, enforce: true);
        Assert.Equal(RelayAction.Forward, sut.Intercept(Ctx(OutcomeEvent(5))).Action);
    }

    [Fact]
    public void Outcome_to_unknown_target_is_forwarded_failopen()
    {
        var sut = Sut(new RoomWorldState(), enforce: true);   // never observed view 5
        Assert.Equal(RelayAction.Forward, sut.Intercept(Ctx(OutcomeEvent(5))).Action);
    }

    [Fact]
    public void Dead_target_detection_only_forwards_but_counts()
    {
        var sut = Sut(WorldWithDead(5), enforce: false);   // detection-only: log + tally, still forward
        var verdict = sut.Intercept(Ctx(OutcomeEvent(5)));
        Assert.Equal(RelayAction.Forward, verdict.Action);
        Assert.Equal(1, sut.FlaggedCount);
    }

    [Fact]
    public void Dead_target_under_enforce_is_dropped()
    {
        var sut = Sut(WorldWithDead(5), enforce: true);
        Assert.Equal(RelayAction.Drop, sut.Intercept(Ctx(OutcomeEvent(5))).Action);
        Assert.Equal(1, sut.FlaggedCount);
    }

    [Fact]
    public void Observer_then_validator_through_chain_drops_damage_after_death()
    {
        // End-to-end ordering: the observer marks view 5 dead from a 204, then a later 200 aimed at view 5
        // is rejected by the outcome validator. Mirrors the production ServerAuthorityInterceptor wiring.
        var world = new RoomWorldState();
        var chain = new InterceptorChain(new IEventInterceptor[]
        {
            new WorldStateObserver(world),
            new OutcomeValidationInterceptor(world, new IOutcomeRule[] { new DeadTargetOutcomeRule() }, enforce: true),
            new PassthroughInterceptor(),
        });

        var spawn = new EventData(202, new Dictionary<byte, object> { { (byte)245, new Dictionary<object, object> { { (byte)7, 5 } } } });
        var destroy = new EventData(204, new Dictionary<byte, object> { { (byte)245, new Dictionary<object, object> { { (byte)7, 5 } } } });

        Assert.Equal(RelayAction.Forward, chain.Run(Ctx(spawn)).Action);
        Assert.Equal(RelayAction.Forward, chain.Run(Ctx(OutcomeEvent(5))).Action);   // alive: allowed
        Assert.Equal(RelayAction.Forward, chain.Run(Ctx(destroy)).Action);
        Assert.Equal(RelayAction.Drop, chain.Run(Ctx(OutcomeEvent(5))).Action);      // dead: rejected
    }
}
