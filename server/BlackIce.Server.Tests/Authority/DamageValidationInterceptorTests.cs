using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace BlackIce.Server.Tests.Authority;

public class DamageValidationInterceptorTests
{
    private static EventData DamageRpc(float dmg)
    {
        var b = new byte[41]; BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), dmg);
        return new EventData(200, new()
        {
            { 245, new Dictionary<object, object>
                {
                    { (byte)3, "TakeDamage" },
                    { (byte)4, new object[] { new PhotonCustomData(68, b) } },
                } },
        });
    }

    [Fact]
    public void Always_forwards_even_when_damage_is_absurd()
    {
        var i = new DamageValidationInterceptor(maxDamage: 1000f);
        var v = i.Intercept(new EventContext("co-op", 1, DamageRpc(999999f)));
        Assert.Equal(RelayAction.Forward, v.Action);       // log-only phase: never drops
    }

    [Fact]
    public void Flags_count_increments_only_for_over_threshold_damage()
    {
        var i = new DamageValidationInterceptor(maxDamage: 1000f);
        i.Intercept(new EventContext("co-op", 1, DamageRpc(50f)));     // fine
        i.Intercept(new EventContext("co-op", 1, DamageRpc(5000f)));   // flagged
        i.Intercept(new EventContext("co-op", 1, DamageRpc(20f)));     // fine
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Non_damage_events_pass_without_flagging()
    {
        var i = new DamageValidationInterceptor(maxDamage: 1000f);
        var v = i.Intercept(new EventContext("co-op", 1, new EventData(201, new() { { 245, "pos" } })));
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Equal(0, i.FlaggedCount);
    }

    [Fact]
    public void Over_threshold_damage_drops_when_enforcing()
    {
        var i = new DamageValidationInterceptor(maxDamage: 1000f, enforce: true);
        Assert.Equal(RelayAction.Forward, i.Intercept(new EventContext("co-op", 1, DamageRpc(50f))).Action);
        Assert.Equal(RelayAction.Drop, i.Intercept(new EventContext("co-op", 1, DamageRpc(9999f))).Action);
    }

    [Fact]
    public void Non_finite_damage_is_flagged()
    {
        var i = new DamageValidationInterceptor(maxDamage: 1000f);
        i.Intercept(new EventContext("co-op", 1, DamageRpc(float.PositiveInfinity)));
        Assert.Equal(1, i.FlaggedCount);
    }
}
