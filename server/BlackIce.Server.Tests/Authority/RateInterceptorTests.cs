using System.Buffers.Binary;
using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

public class RateInterceptorTests
{
    // A damage RPC for view 1001; optionally stamps the WeakPoint (bit1) and/or Crit (bit0) flag into
    // the DamagePacket at the given byte offset, mirroring Black Ice's "combined" bitfield.
    private static EventData DamageRpc(float dmg, int headshotOffset = -1, int critOffset = -1)
    {
        var b = new byte[41];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), dmg);
        if (headshotOffset >= 0) b[headshotOffset] |= 0x02;   // WeakPoint
        if (critOffset >= 0) b[critOffset] |= 0x01;           // Crit
        return new EventData(200, new()
        {
            { 245, new Dictionary<object, object>
                {
                    { (byte)0, 1001 },                                  // viewId (owner = actor 1)
                    { (byte)3, "TakeDamage" },
                    { (byte)4, new object[] { new PhotonCustomData(68, b) } },
                } },
        });
    }

    private static EventData Rpc(int viewId) => new(200, new()
    {
        { 245, new Dictionary<object, object> { { (byte)0, viewId }, { (byte)3, "Move" }, { (byte)4, new object[0] } } },
    });

    // --- SlidingWindowCounter --------------------------------------------------------------------

    [Fact]
    public void Window_counts_and_sums_within_and_evicts_outside()
    {
        var w = new SlidingWindowCounter(System.TimeSpan.FromSeconds(10));
        var t0 = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        w.Add(t0, 5);
        w.Add(t0.AddSeconds(1), 5);
        Assert.Equal(2, w.Count(t0.AddSeconds(1)));
        Assert.Equal(10, w.Sum(t0.AddSeconds(1)));
        // 11s after the first sample, the first has aged out.
        Assert.Equal(1, w.Count(t0.AddSeconds(11)));
        Assert.Equal(5, w.Sum(t0.AddSeconds(11)));
    }

    // --- EventRateInterceptor --------------------------------------------------------------------

    [Fact]
    public void Event_flood_is_flagged_but_forwarded_when_not_enforcing()
    {
        var opt = new AnticheatOptions { MaxEventsPerWindow = 3, RateWindowSeconds = 60 };
        var i = new EventRateInterceptor(opt);
        for (int n = 0; n < 4; n++) i.Intercept(new EventContext("co-op", 1, Rpc(1001)));
        Assert.True(i.FlaggedCount >= 1);
        Assert.Equal(RelayAction.Forward, i.Intercept(new EventContext("co-op", 1, Rpc(1001))).Action);
    }

    [Fact]
    public void Event_flood_drops_when_enforcing()
    {
        var opt = new AnticheatOptions { MaxEventsPerWindow = 2, RateWindowSeconds = 60, Enforce = true };
        var i = new EventRateInterceptor(opt);
        i.Intercept(new EventContext("co-op", 1, Rpc(1001)));
        i.Intercept(new EventContext("co-op", 1, Rpc(1001)));
        Assert.Equal(RelayAction.Drop, i.Intercept(new EventContext("co-op", 1, Rpc(1001))).Action);  // 3rd > 2
    }

    [Fact]
    public void Event_rate_is_tracked_per_actor()
    {
        var opt = new AnticheatOptions { MaxEventsPerWindow = 2, RateWindowSeconds = 60 };
        var i = new EventRateInterceptor(opt);
        for (int n = 0; n < 3; n++) i.Intercept(new EventContext("co-op", 1, Rpc(1001)));   // actor 1 floods
        i.Intercept(new EventContext("co-op", 2, Rpc(2001)));                                // actor 2 is fine
        Assert.Equal(1, i.FlaggedCount);   // only actor 1's over-threshold event flagged
    }

    // --- HitRateInterceptor ----------------------------------------------------------------------

    [Fact]
    public void Too_many_hits_in_window_is_flagged()
    {
        var opt = new AnticheatOptions { MaxHitsPerWindow = 3, MaxDamagePerWindow = float.MaxValue, RateWindowSeconds = 60 };
        var i = new HitRateInterceptor(opt);
        for (int n = 0; n < 4; n++) i.Intercept(new EventContext("co-op", 1, DamageRpc(10f)));
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Cumulative_damage_in_window_is_flagged()
    {
        var opt = new AnticheatOptions { MaxHitsPerWindow = 1000, MaxDamagePerWindow = 100f, RateWindowSeconds = 60 };
        var i = new HitRateInterceptor(opt);
        i.Intercept(new EventContext("co-op", 1, DamageRpc(60f)));   // 60
        i.Intercept(new EventContext("co-op", 1, DamageRpc(60f)));   // 120 > 100 -> flagged
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Non_finite_damage_is_flagged()
    {
        var i = new HitRateInterceptor(new AnticheatOptions { RateWindowSeconds = 60 });
        i.Intercept(new EventContext("co-op", 1, DamageRpc(float.NaN)));
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Too_many_headshots_in_window_is_flagged_when_offset_configured()
    {
        var opt = new AnticheatOptions
        {
            MaxHitsPerWindow = 1000, MaxDamagePerWindow = float.MaxValue,
            MaxHeadshotsPerWindow = 2, HeadshotFlagOffset = 4, RateWindowSeconds = 60,
        };
        var i = new HitRateInterceptor(opt);
        for (int n = 0; n < 3; n++) i.Intercept(new EventContext("co-op", 1, DamageRpc(10f, headshotOffset: 4)));
        Assert.Equal(1, i.FlaggedCount);   // 3rd headshot > 2
    }

    [Fact]
    public void Headshot_mask_isolates_the_weakpoint_bit_from_crit()
    {
        // offset 39, mask 0x02 (WeakPoint): crit-only hits (bit0) must NOT count as headshots.
        var opt = new AnticheatOptions
        {
            MaxHitsPerWindow = 1000, MaxDamagePerWindow = float.MaxValue,
            MaxHeadshotsPerWindow = 1, HeadshotFlagOffset = 39, HeadshotFlagMask = 0x02, RateWindowSeconds = 60,
        };
        var i = new HitRateInterceptor(opt);
        for (int n = 0; n < 5; n++) i.Intercept(new EventContext("co-op", 1, DamageRpc(10f, critOffset: 39)));   // crit bit only
        Assert.Equal(0, i.FlaggedCount);   // crit != weakpoint under mask 0x02

        for (int n = 0; n < 3; n++) i.Intercept(new EventContext("co-op", 1, DamageRpc(10f, headshotOffset: 39)));  // weakpoint bit
        Assert.True(i.FlaggedCount >= 1);
    }

    [Fact]
    public void Headshots_not_checked_without_an_offset()
    {
        var opt = new AnticheatOptions { MaxHitsPerWindow = 1000, MaxDamagePerWindow = float.MaxValue, MaxHeadshotsPerWindow = 1, RateWindowSeconds = 60 };
        var i = new HitRateInterceptor(opt);
        for (int n = 0; n < 5; n++) i.Intercept(new EventContext("co-op", 1, DamageRpc(10f, headshotOffset: 4)));
        Assert.Equal(0, i.FlaggedCount);   // HeadshotFlagOffset null -> inert
    }

    // --- ViewOwnershipInterceptor ----------------------------------------------------------------

    [Fact]
    public void Acting_on_own_view_is_allowed()
    {
        var i = new ViewOwnershipInterceptor();
        var v = i.Intercept(new EventContext("co-op", 1, Rpc(1001)));   // owner 1001/1000 = 1 == sender
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Equal(0, i.FlaggedCount);
    }

    [Fact]
    public void Acting_on_another_actors_view_is_flagged()
    {
        var i = new ViewOwnershipInterceptor();
        i.Intercept(new EventContext("co-op", 1, Rpc(2001)));   // owner 2, sender 1 -> mismatch
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Scene_objects_block_zero_are_allowed_for_anyone()
    {
        var i = new ViewOwnershipInterceptor();
        i.Intercept(new EventContext("co-op", 3, Rpc(5)));   // owner 0 (scene) -> allowed
        Assert.Equal(0, i.FlaggedCount);
    }

    [Fact]
    public void View_spoof_drops_when_enforcing()
    {
        var i = new ViewOwnershipInterceptor(enforce: true);
        Assert.Equal(RelayAction.Drop, i.Intercept(new EventContext("co-op", 1, Rpc(7001))).Action);
    }

    private static EventData Instantiate(int viewId) => new(202, new()
    {
        { 245, new Dictionary<object, object> { { (byte)0, "Player" }, { (byte)7, viewId } } },
    });

    [Fact]
    public void Instantiating_into_another_actors_block_is_flagged()
    {
        var i = new ViewOwnershipInterceptor();
        i.Intercept(new EventContext("co-op", 1, Instantiate(1001)));   // owner 1 == sender: ok
        i.Intercept(new EventContext("co-op", 1, Instantiate(3001)));   // owner 3 != sender 1: flagged
        Assert.Equal(1, i.FlaggedCount);
    }
}
