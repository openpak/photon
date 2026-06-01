using BlackIce.Photon;
using BlackIce.Server.LoadBalancing.Authority;
using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests.Bots;

/// <summary>
/// Fleet behaviour added to fix "all bots stack on one point / swarm origin": targets with no KNOWN
/// position are never chased, bots at different fleet indices fan out across distinct targets, and a bot
/// rotates off an over-worked target when another exists.
/// </summary>
public class HunterBehaviorFleetTests
{
    private const int BotView = 10000001;

    private static (int view, float? dmg, string? method) Decode(EventData ev)
    {
        var i = PunRpcInfo.From(ev);
        return i is { } x ? (x.ViewId, x.DamageValue, x.Method) : (0, null, null);
    }

    private static RoomWorldState World(params (int view, string kind, float x, float z)[] entities)
    {
        var w = new RoomWorldState();
        foreach (var (view, kind, x, z) in entities) w.ObserveSpawn(view, kind, x, 0f, z);
        return w;
    }

    [Fact]
    public void Lone_over_worked_enemy_does_not_pin_the_bot_forever()
    {
        // Regression for "all bots camp one enemy and look stopped": a single in-range enemy that never dies.
        // After MaxActsPerTarget the bot must STOP re-pinning to it (cool it down + fall through), not keep
        // attacking it every tick. With a player present, the bot then regroups toward the player.
        var w = World((1002, "SpiderEnemy", 1, 0), (2001, "Player", 40, 0));
        var bot = new HunterBehavior(BotView, 0, 0, fleetIndex: 0, seed: 1);

        int attacks = 0; bool regrouped = false;
        for (int i = 0; i < 30; i++)
        {
            var step = bot.Think(w);
            foreach (var ev in step.Actions) if (Decode(ev).method == "TakeDamage") attacks++;
            if (step.Label.Contains("regroup")) regrouped = true;
        }
        // It attacks a bounded number of times (a few bursts as cooldown lapses), NOT all 30 ticks.
        Assert.True(attacks < 30, $"bot pinned the lone enemy every tick ({attacks}/30) — the camp bug");
        Assert.True(regrouped, "bot never regrouped toward the player while the enemy was on cooldown");
    }

    [Fact]
    public void Stays_leashed_to_the_player_instead_of_walking_off_to_infinity()
    {
        // Regression for "bots walked in a straight axis off into the air far away": with no live terrain
        // (the procedural world matches no static navmesh), a bot chasing a far target must stay in the
        // playable area around the player rather than marching to the void.
        var w = World((2001, "Player", 0, 0), (1002, "SpiderEnemy", 500, 0));   // enemy way out past the map
        var bot = new HunterBehavior(BotView, 0, 0, fleetIndex: 0, seed: 1);
        BotStep step = bot.Think(w);
        for (int i = 0; i < 50; i++) step = bot.Think(w);
        double dist = System.Math.Sqrt(step.Position.X * step.Position.X + step.Position.Z * step.Position.Z);
        Assert.True(dist < 70, $"bot strayed {dist:F0}u from the player — past the leash (walking into the void)");
    }

    [Fact]
    public void Teleport_moves_the_bot_and_drops_its_target()
    {
        var w = World((1002, "SpiderEnemy", 2, 0));
        var bot = new HunterBehavior(BotView, 0, 0, fleetIndex: 0, seed: 1);
        bot.Think(w);                       // acquire the enemy
        bot.Teleport(100, 0, 100);          // summoned far away
        var step = bot.Think(w);
        // After teleport it is at the new spot (not snapped back), and re-evaluates from there.
        Assert.True(System.Math.Abs(step.Position.X - 2) > 50, "teleported bot should not be back at the old enemy");
    }

    [Fact]
    public void Entity_with_unknown_position_is_not_chased()
    {
        // A 202 arrived with a kind but NO position (HasPosition stays false). The bot must not path to a
        // phantom (0,0,0) — it never acts on / chases the unknown-position entity (it patrols instead of
        // swarming origin). The key assertion is "no action and not chasing 0,0,0", not the exact label.
        var w = new RoomWorldState();
        w.ObserveSpawn(1002, "SpiderEnemy");   // kind known, position unknown
        var bot = new HunterBehavior(BotView, startX: 7, startZ: 7, seed: 1);
        var step = bot.Think(w);
        Assert.Empty(step.Actions);
        Assert.DoesNotContain("approach", step.Label);   // not chasing the unknown-position enemy
        // Patrol keeps it near its own spot (radius ~5), never near phantom origin far away.
        double distFromStart = System.Math.Sqrt(System.Math.Pow(step.Position.X - 7, 2) + System.Math.Pow(step.Position.Z - 7, 2));
        Assert.True(distFromStart < 12, $"bot should stay near its spot, not chase origin; moved {distFromStart:F1}");
    }

    [Fact]
    public void Once_a_position_is_known_the_entity_becomes_a_target()
    {
        var w = new RoomWorldState();
        w.ObserveSpawn(1002, "SpiderEnemy");                       // no position yet
        w.RecordPosition(1002, 2, 0, 0, new System.DateTime(2026, 1, 1));   // a 201 fills it in
        var bot = new HunterBehavior(BotView, 0, 0, seed: 1);
        var step = bot.Think(w);
        var (view, _, method) = Decode(Assert.Single(step.Actions));
        Assert.Equal(1002, view);
        Assert.Equal("TakeDamage", method);
    }

    [Fact]
    public void Bots_at_different_fleet_indices_pick_different_enemies()
    {
        // Two enemies; bot index 0 takes the nearest, bot index 1 takes the next — they fan out.
        RoomWorldState World()
        {
            var w = new RoomWorldState();
            w.ObserveSpawn(1002, "SpiderEnemy", 2, 0, 0);
            w.ObserveSpawn(1003, "CrabEnemy", 3, 0, 0);
            return w;
        }
        var bot0 = new HunterBehavior(BotView, 0, 0, fleetIndex: 0, seed: 1);
        var bot1 = new HunterBehavior(BotView + 1000, 0, 0, fleetIndex: 1, seed: 2);

        var (v0, _, _) = Decode(Assert.Single(bot0.Think(World()).Actions));
        var (v1, _, _) = Decode(Assert.Single(bot1.Think(World()).Actions));
        Assert.NotEqual(v0, v1);   // distinct targets, not stacked on one
    }

    [Fact]
    public void Rotates_off_an_over_worked_target_when_another_exists()
    {
        var w = new RoomWorldState();
        w.ObserveSpawn(1002, "SpiderEnemy", 1, 0, 0);
        w.ObserveSpawn(1003, "SpiderEnemy", 2, 0, 0);
        var bot = new HunterBehavior(BotView, 0, 0, fleetIndex: 0, seed: 1);

        var hits = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < 20; i++)
            foreach (var ev in bot.Think(w).Actions)
                if (Decode(ev).method == "TakeDamage") hits.Add(Decode(ev).view);

        Assert.True(hits.Count >= 2, $"expected the bot to rotate across both enemies, hit {hits.Count}");
    }

    [Fact]
    public void Bot_keeps_its_ground_height_and_does_not_float_to_an_airborne_target()
    {
        // Loot at Y=50 (airborne/arbitrary spawn height). The bot, anchored on ground at Y=2, must NOT
        // adopt the loot's Y — it stays at its ground height while moving in XZ. (Fixes "bots in the air".)
        var w = new RoomWorldState();
        w.ObserveSpawn(9, "XPGem", 100, 50, 0);   // far in XZ, high in Y
        var bot = new HunterBehavior(BotView, startX: 0, startZ: 0, startY: 2f, fleetIndex: 0, seed: 1);
        var step = bot.Think(w);
        Assert.Equal(2f, step.Position.Y, 3);      // ground height preserved
        Assert.True(step.Position.X > 0, "should still move toward the loot in XZ");
    }

    [Fact]
    public void Bot_adopts_a_player_ground_height_when_regrouping()
    {
        // No actionable target, only a player avatar at a known-walkable Y. Regrouping, the bot adopts the
        // player's Y (a trustworthy ground height) so it ends up on the same level as the player.
        var w = new RoomWorldState();
        w.ObserveSpawn(2001, "Player", 100, 8, 0);
        var bot = new HunterBehavior(BotView, startX: 0, startZ: 0, startY: 0f, fleetIndex: 0, seed: 1);
        var step = bot.Think(w);
        Assert.Contains("regroup", step.Label);
        Assert.Equal(8f, step.Position.Y, 3);
    }

    [Fact]
    public void In_range_the_bot_orbits_rather_than_standing_on_the_target_center()
    {
        // The bot ends its turn offset from the enemy (its orbit slot), not exactly on the enemy's position,
        // so a group of bots rings the target instead of stacking into one point.
        var w = new RoomWorldState();
        w.ObserveSpawn(1002, "SpiderEnemy", 0, 0, 0);   // enemy at origin
        var bot = new HunterBehavior(BotView, 1, 0, fleetIndex: 0, seed: 1);
        var step = bot.Think(w);
        Assert.NotEmpty(step.Actions);   // it acted (in range)
        double d = System.Math.Sqrt(step.Position.X * step.Position.X + step.Position.Z * step.Position.Z);
        Assert.True(d > 0.5, $"expected the bot to orbit off-center, distance from enemy was {d:F2}");
    }
}
