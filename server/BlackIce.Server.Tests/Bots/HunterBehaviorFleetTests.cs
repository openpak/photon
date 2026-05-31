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

    [Fact]
    public void Entity_with_unknown_position_is_not_chased()
    {
        // A 202 arrived with a kind but NO position (HasPosition stays false). The bot must not path to a
        // phantom (0,0,0) — with nothing else known it idles in place rather than swarming origin.
        var w = new RoomWorldState();
        w.ObserveSpawn(1002, "SpiderEnemy");   // kind known, position unknown
        var bot = new HunterBehavior(BotView, startX: 7, startZ: 7, seed: 1);
        var step = bot.Think(w);
        Assert.Empty(step.Actions);
        Assert.Equal("idle", step.Label);
        Assert.Equal(7f, step.Position.X, 3);
        Assert.Equal(7f, step.Position.Z, 3);
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
