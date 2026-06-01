using BlackIce.Photon;
using BlackIce.Server.LoadBalancing.Authority;
using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests.Bots;

/// <summary>
/// The world-aware playerbot brain: it reads the room's shadow world-state and seeks/acts on real targets.
/// Driven against a hand-built <see cref="RoomWorldState"/> — no live server needed.
/// </summary>
public class HunterBehaviorTests
{
    private const int BotView = 10000001;

    private static RoomWorldState World(params (int view, string kind, float x, float z)[] entities)
    {
        var w = new RoomWorldState();
        foreach (var (view, kind, x, z) in entities) w.ObserveSpawn(view, kind, x, 0f, z);
        return w;
    }

    private static (int view, float? dmg, string? method) DecodeRpc(EventData ev)
    {
        var info = PunRpcInfo.From(ev);
        return info is { } i ? (i.ViewId, i.DamageValue, i.Method) : (0, null, null);
    }

    [Fact]
    public void No_entities_at_all_patrols_without_acting()
    {
        // With nothing known the bot patrols (circles its spot) rather than freezing — but never acts.
        var bot = new HunterBehavior(BotView, startX: 3, startZ: 4, seed: 1);
        var step = bot.Think(new RoomWorldState());
        Assert.Empty(step.Actions);
        Assert.Equal("patrol", step.Label);
    }

    [Fact]
    public void Patrol_keeps_the_bot_moving_across_ticks()
    {
        // The original "stops after ~30s" bug: a bot with nothing to hunt must keep moving, not freeze.
        var bot = new HunterBehavior(BotView, startX: 0, startZ: 0, seed: 1);
        var p1 = bot.Think(new RoomWorldState()).Position;
        var p2 = bot.Think(new RoomWorldState()).Position;
        bool moved = System.Math.Abs(p1.X - p2.X) > 0.01f || System.Math.Abs(p1.Z - p2.Z) > 0.01f;
        Assert.True(moved, $"patrol should move the bot between ticks: ({p1.X},{p1.Z}) -> ({p2.X},{p2.Z})");
    }

    [Fact]
    public void With_no_actionable_target_regroups_toward_nearest_known_entity()
    {
        // A lone player avatar (not a valid combat/hack/loot target) is the only known entity: the bot
        // should DRIFT toward it (leave its spawn point) rather than idle in place — cold-start behavior.
        var bot = new HunterBehavior(BotView, startX: 0, startZ: 0, seed: 1);
        var step = bot.Think(World((2001, "Player", 100, 0)));
        Assert.Empty(step.Actions);                 // never acts on a non-target
        Assert.Contains("regroup", step.Label);
        Assert.True(step.Position.X > 0, $"expected the bot to move toward the player, got x={step.Position.X}");
    }

    [Fact]
    public void Approaches_a_distant_enemy_without_acting()
    {
        var bot = new HunterBehavior(BotView, 0, 0, seed: 1);
        var step = bot.Think(World((1002, "SpiderEnemy", 100, 0)));   // far along +X
        Assert.Empty(step.Actions);                                   // too far to act
        Assert.Contains("approach", step.Label);
        Assert.True(step.Position.X > 0 && step.Position.X <= 6.01f,  // moved toward, bounded by StepSpeed
            $"expected a bounded step toward the enemy, got x={step.Position.X}");
    }

    [Fact]
    public void Attacks_an_enemy_in_range_with_a_TakeDamage_rpc()
    {
        var bot = new HunterBehavior(BotView, 0, 0, seed: 1);
        var step = bot.Think(World((1002, "CrabEnemy", 2, 0)));       // within attack range
        var rpc = Assert.Single(step.Actions);
        var (view, dmg, method) = DecodeRpc(rpc);
        Assert.Equal(1002, view);
        Assert.Equal("TakeDamage", method);
        Assert.NotNull(dmg);
        Assert.True(dmg > 0);
        Assert.Contains("attack", step.Label);
    }

    [Fact]
    public void Prefers_an_enemy_over_loot_even_if_loot_is_closer()
    {
        var bot = new HunterBehavior(BotView, 0, 0, seed: 1);
        var step = bot.Think(World((9, "XPGem", 1, 0), (1002, "SpiderEnemy", 3, 0)));
        // Enemy is the priority target; within range it attacks the enemy (view 1002), not the gem.
        var (view, _, method) = DecodeRpc(Assert.Single(step.Actions));
        Assert.Equal(1002, view);
        Assert.Equal("TakeDamage", method);
    }

    [Fact]
    public void Hacks_a_link_node_in_range()
    {
        var bot = new HunterBehavior(BotView, 0, 0, seed: 1);
        var step = bot.Think(World((1007, "Link", 2, 0)));
        var (view, _, method) = DecodeRpc(Assert.Single(step.Actions));
        Assert.Equal(1007, view);
        Assert.Equal("SetupHack", method);
        Assert.Contains("hack", step.Label);
    }

    [Fact]
    public void Loots_a_cube_in_range()
    {
        var bot = new HunterBehavior(BotView, 0, 0, seed: 1);
        var step = bot.Think(World((9, "NetworkLootCube", 1, 1)));
        var (view, _, method) = DecodeRpc(Assert.Single(step.Actions));
        Assert.Equal(9, view);
        Assert.Equal("GetLock", method);
        Assert.Contains("loot", step.Label);
    }

    [Fact]
    public void Never_acts_on_a_player_or_itself()
    {
        // Players are never attacked/hacked/looted. With only players around (one of them itself), the bot
        // emits no action — it may regroup toward the other player, but never an action RPC.
        var bot = new HunterBehavior(BotView, 0, 0, seed: 1);
        var step = bot.Think(World((BotView, "Player", 1, 0), (2001, "Player", 50, 0)));
        Assert.Empty(step.Actions);
        Assert.DoesNotContain("attack", step.Label);
        Assert.DoesNotContain("hack", step.Label);
        Assert.DoesNotContain("loot", step.Label);
    }

    [Fact]
    public void Gains_xp_and_levels_up_emitting_a_buff_rpc()
    {
        var bot = new HunterBehavior(BotView, 0, 0, seed: 1);
        var world = World((1002, "SpiderEnemy", 1, 0));   // in range, stays alive → repeated attacks
        EventData? buff = null;
        for (int i = 0; i < 12 && buff is null; i++)      // XpPerAction=10, XpPerLevel=100 → ~10 hits to level
        {
            var step = bot.Think(world);
            foreach (var ev in step.Actions)
                if (PunRpcInfo.From(ev)?.Method == "AddBuffRPC") buff = ev;
        }
        Assert.True(bot.Level >= 2, $"expected the bot to reach level 2, got {bot.Level}");
        Assert.NotNull(buff);   // a progression RPC fired on level-up
    }

    [Fact]
    public void Retargets_after_the_current_target_dies()
    {
        var bot = new HunterBehavior(BotView, 0, 0, seed: 1);
        var world = World((1002, "SpiderEnemy", 1, 0), (1003, "CrabEnemy", 2, 0));
        var first = DecodeRpc(Assert.Single(bot.Think(world).Actions));
        world.ObserveDestroy(first.view);                // master kills the bot's current target
        var second = DecodeRpc(Assert.Single(bot.Think(world).Actions));
        Assert.NotEqual(first.view, second.view);        // switched to the surviving enemy
    }
}
