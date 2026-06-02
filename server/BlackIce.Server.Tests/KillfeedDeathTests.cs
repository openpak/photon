using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Plugins;
using Xunit;

namespace BlackIce.Server.Tests;

public class KillfeedDeathTests
{
    // A KilledPlayerRemote RPC sent by shortcut index 32, victim = pawn viewId of `victimActor`.
    private static EventData Death(int victimActor) => new(200, new()
    {
        { 245, new Dictionary<object, object>
            {
                { (byte)5, (byte)32 },                                  // KilledPlayerRemote
                { (byte)4, new object[] { victimActor * 1000 + 1 } },   // victim pawn viewId
            } },
    });

    private static (KillfeedInterceptor i, KillfeedState s, KillBus bus) Make()
    {
        var s = new KillfeedState { On = true };
        var bus = new KillBus();
        return (new KillfeedInterceptor(s, bus), s, bus);
    }

    [Fact]
    public void A_death_rpc_publishes_a_DeathNotice_and_announces()
    {
        var (i, _, bus) = Make();
        DeathNotice? seen = null;
        bus.Died += n => seen = n;

        var v = i.Intercept(new EventContext("co-op", 1, Death(victimActor: 6)));

        Assert.Equal(RelayAction.Originate, v.Action);     // original death RPC + an announcement
        Assert.Single(v.Originated);
        Assert.NotNull(seen);
        Assert.Equal(6, seen!.Value.Victim);
    }

    [Fact]
    public void A_repeat_death_for_an_already_dead_victim_is_debounced()
    {
        var (i, _, bus) = Make();
        int notices = 0;
        bus.Died += _ => notices++;

        i.Intercept(new EventContext("co-op", 1, Death(6)));
        i.Intercept(new EventContext("co-op", 1, Death(6)));   // repeat before any reset

        Assert.Equal(1, notices);
    }

    [Fact]
    public void Non_death_rpcs_are_forwarded_untouched()
    {
        var (i, _, _) = Make();
        var v = i.Intercept(new EventContext("co-op", 1, new EventData(201, new() { { 245, "pos" } })));
        Assert.Equal(RelayAction.Forward, v.Action);
    }

    [Fact]
    public void Off_by_default_forwards_everything()
    {
        var s = new KillfeedState();                 // On == false
        var i = new KillfeedInterceptor(s, new KillBus());
        Assert.Equal(RelayAction.Forward, i.Intercept(new EventContext("co-op", 1, Death(6))).Action);
    }

    [Fact]
    public void A_room_reset_clears_the_dead_set_so_the_victim_can_die_again()
    {
        var (i, s, bus) = Make();
        int notices = 0;
        bus.Died += _ => notices++;

        i.Intercept(new EventContext("co-op", 1, Death(6)));
        s.ClearDead("co-op");                          // round reset
        i.Intercept(new EventContext("co-op", 1, Death(6)));

        Assert.Equal(2, notices);
    }
}
