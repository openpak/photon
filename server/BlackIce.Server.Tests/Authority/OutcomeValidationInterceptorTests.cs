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
            { (byte)0, targetViewId },                                  // RpcViewId (target)
            { (byte)3, method },                                        // RpcMethodName
            { (byte)4, new object[] { new PhotonCustomData(68, dmg) } },// RpcArgs: DamagePacket
        };
        return new EventData(200, new Dictionary<byte, object> { { (byte)245, rpc } });
    }

    private static EventContext Ctx(EventData ev, string room = "r", int actor = 9) => new(room, actor, ev);

    private static OutcomeValidationInterceptor Sut(RoomWorldState world, AuthorityStrictness level, out ViolationTracker vt)
    {
        vt = new ViolationTracker(int.MaxValue, TimeSpan.FromMinutes(5));
        return new OutcomeValidationInterceptor(
            world, new IOutcomeRule[] { new DeadTargetOutcomeRule() }, new AuthorityPolicy(level), vt);
    }

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
        var sut = Sut(new RoomWorldState(), AuthorityStrictness.Enforce, out _);
        var ev = new EventData(201, new Dictionary<byte, object>());   // not a 200 RPC
        Assert.Equal(RelayAction.Forward, sut.Intercept(Ctx(ev)).Action);
    }

    [Fact]
    public void Outcome_to_alive_target_is_forwarded()
    {
        var world = new RoomWorldState();
        world.ObserveSpawn(5);
        var sut = Sut(world, AuthorityStrictness.Enforce, out _);
        Assert.Equal(RelayAction.Forward, sut.Intercept(Ctx(OutcomeEvent(5))).Action);
    }

    [Fact]
    public void Outcome_to_unknown_target_is_forwarded_failopen()
    {
        var sut = Sut(new RoomWorldState(), AuthorityStrictness.Enforce, out _);   // never observed view 5
        Assert.Equal(RelayAction.Forward, sut.Intercept(Ctx(OutcomeEvent(5))).Action);
    }

    [Fact]
    public void Dead_target_under_observe_forwards_and_does_not_count()
    {
        var sut = Sut(WorldWithDead(5), AuthorityStrictness.Observe, out var vt);
        var verdict = sut.Intercept(Ctx(OutcomeEvent(5)));
        Assert.Equal(RelayAction.Forward, verdict.Action);   // Observe is a pure no-op
        Assert.Equal(0, sut.FlaggedCount);
        Assert.Equal(0, vt.CountFor("r", 9));
    }

    [Fact]
    public void Dead_target_under_warn_forwards_but_counts()
    {
        var sut = Sut(WorldWithDead(5), AuthorityStrictness.Warn, out var vt);
        var verdict = sut.Intercept(Ctx(OutcomeEvent(5)));
        Assert.Equal(RelayAction.Forward, verdict.Action);   // Warn: log + tally, still forward
        Assert.Equal(1, sut.FlaggedCount);
        Assert.Equal(1, vt.CountFor("r", 9));
    }

    [Fact]
    public void Dead_target_under_enforce_is_dropped()
    {
        var sut = Sut(WorldWithDead(5), AuthorityStrictness.Enforce, out _);
        Assert.Equal(RelayAction.Drop, sut.Intercept(Ctx(OutcomeEvent(5))).Action);
    }

    [Fact]
    public void Dead_target_under_strict_is_dropped_and_flagged()
    {
        var sut = Sut(WorldWithDead(5), AuthorityStrictness.Strict, out var vt);
        Assert.Equal(RelayAction.Drop, sut.Intercept(Ctx(OutcomeEvent(5))).Action);
        Assert.Equal(1, vt.CountFor("r", 9));
    }

    [Fact]
    public void Observer_then_validator_through_chain_drops_damage_after_death()
    {
        // End-to-end ordering: the observer (first) marks view 5 dead from a 204, then a later 200 aimed
        // at view 5 is rejected by the outcome validator. Mirrors the production chain wiring.
        var world = new RoomWorldState();
        var vt = new ViolationTracker(int.MaxValue, TimeSpan.FromMinutes(5));
        var chain = new InterceptorChain(new IEventInterceptor[]
        {
            new WorldStateObserver(world),
            new OutcomeValidationInterceptor(world, new IOutcomeRule[] { new DeadTargetOutcomeRule() },
                                             new AuthorityPolicy(AuthorityStrictness.Enforce), vt),
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
