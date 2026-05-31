using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

/// <summary>
/// Phase 3a: the damage interceptor now ACTS per the realm <see cref="AuthorityPolicy"/>. Observe/Warn
/// forward (log only); Enforce/Strict drop an over-threshold (zero-trust) outcome. Fail-open: a normal
/// hit, a non-damage event, or an unparseable RPC is forwarded.
/// </summary>
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

    private static DamageValidationInterceptor New(AuthorityStrictness level, float maxDamage = 1000f)
        => new(maxDamage, new AuthorityPolicy(level), new ViolationTracker(int.MaxValue, TimeSpan.FromHours(1)));

    [Fact]
    public void Observe_forwards_even_when_damage_is_absurd()
    {
        var i = New(AuthorityStrictness.Observe);
        var v = i.Intercept(new EventContext("co-op", 1, DamageRpc(999999f)));
        Assert.Equal(RelayAction.Forward, v.Action);
    }

    [Fact]
    public void Warn_forwards_but_counts_over_threshold_damage()
    {
        var i = New(AuthorityStrictness.Warn);
        var v = i.Intercept(new EventContext("co-op", 1, DamageRpc(5000f)));
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Enforce_drops_over_threshold_damage()
    {
        var i = New(AuthorityStrictness.Enforce);
        var v = i.Intercept(new EventContext("co-op", 1, DamageRpc(5000f)));
        Assert.Equal(RelayAction.Drop, v.Action);
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Enforce_forwards_legitimate_damage()
    {
        var i = New(AuthorityStrictness.Enforce);
        var v = i.Intercept(new EventContext("co-op", 1, DamageRpc(50f)));
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Equal(0, i.FlaggedCount);
    }

    [Fact]
    public void Flags_count_increments_only_for_over_threshold_damage()
    {
        var i = New(AuthorityStrictness.Warn);
        i.Intercept(new EventContext("co-op", 1, DamageRpc(50f)));     // fine
        i.Intercept(new EventContext("co-op", 1, DamageRpc(5000f)));   // flagged
        i.Intercept(new EventContext("co-op", 1, DamageRpc(20f)));     // fine
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Non_damage_events_pass_without_flagging()
    {
        var i = New(AuthorityStrictness.Enforce);
        var v = i.Intercept(new EventContext("co-op", 1, new EventData(201, new() { { 245, "pos" } })));
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Equal(0, i.FlaggedCount);
    }
}
