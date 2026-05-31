using System.Collections.Generic;
using System.Linq;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests.Bots;

public class GameActionsTests
{
    private static PlayerBot Bot(int actor) => new(actor, new BotIdentityGenerator(1).Next());

    [Fact]
    public void Script_covers_legitimate_and_cheating_actions()
    {
        var script = GameActions.Script(Bot(10000));
        Assert.Contains(script, a => !a.Cheat);
        Assert.Contains(script, a => a.Cheat);
        Assert.Contains(script, a => a.Label.Contains("headshot"));
        Assert.Contains(script, a => a.Label.Contains("event-flood") && a.Events.Count > 100);
    }

    [Fact]
    public void Over_max_damage_action_decodes_to_an_over_threshold_hit()
    {
        var script = GameActions.Script(Bot(10000));
        var cheat = script.First(a => a.Label.Contains("over-max-damage")).Events[0];
        var info = PunRpcInfo.From(cheat);
        Assert.True(info.HasValue);
        Assert.True(info!.Value.DamageValue > 100_000f);
    }

    [Fact]
    public void Headshot_action_sets_the_weakpoint_bit_in_the_damage_packet()
    {
        var script = GameActions.Script(Bot(10000));
        var hs = script.First(a => a.Label.Contains("headshot")).Events[0];
        var info = PunRpcInfo.From(hs);
        Assert.True(info!.Value.IsHeadshot(39));   // WeakPoint bit lives in DamagePacket byte 39
    }

    [Fact]
    public void Cheat_actions_are_flagged_when_run_through_the_authority_chain()
    {
        // Drive the whole script through the production interceptors (detection-only) and confirm the
        // deliberate cheats raise flags while the legit actions pass.
        var bot = Bot(10000);
        var opt = new BlackIce.Server.Core.AnticheatOptions { HeadshotFlagOffset = 39, MaxHeadshotsPerWindow = 3, MaxHitsPerWindow = 20, MaxEventsPerWindow = 50 };
        var dmg = new DamageValidationInterceptor(opt.MaxDamagePerHit);
        var move = new MovementValidationInterceptor(opt.MaxSpeedUnitsPerSecond, opt.MaxTeleportDistance);
        var view = new ViewOwnershipInterceptor();
        var hits = new HitRateInterceptor(opt);
        var events = new EventRateInterceptor(opt);

        foreach (var action in GameActions.Script(bot))
            foreach (var ev in action.Events)
            {
                var ctx = new EventContext("co-op", bot.Actor, ev);
                dmg.Intercept(ctx); move.Intercept(ctx); view.Intercept(ctx); hits.Intercept(ctx); events.Intercept(ctx);
            }

        Assert.True(dmg.FlaggedCount >= 1, "over-max / NaN damage should be flagged");
        Assert.True(move.FlaggedCount >= 1, "teleport / NaN position should be flagged");
        Assert.True(view.FlaggedCount >= 1, "view-spoof should be flagged");
        Assert.True(hits.FlaggedCount >= 1, "hit/headshot floods should be flagged");
        Assert.True(events.FlaggedCount >= 1, "event flood should be flagged");
    }
}
