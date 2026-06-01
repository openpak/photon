using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.Core.Navigation;
using BlackIce.Server.LoadBalancing.Authority;
using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests.Navigation;

/// <summary>
/// The world-aware bot brain's NAVMESH-AWARE movement: when handed a <see cref="NavMesh"/> it stays on the
/// walkable surface (every emitted position samples a real height) and adopts the mesh Y; when handed none it
/// behaves exactly as today (keeps its anchored Y, straight steps). Driven against a synthetic flat strip and
/// a hand-built world-state — no game asset and no live server required.
/// </summary>
public class HunterBehaviorNavMeshTests
{
    private const int BotView = 10000001;

    /// <summary>
    /// A flat walkable strip on the XZ plane at a NON-ZERO height (y = 5), running along +X from x=0 to
    /// x=segments*step, width 0..4 in Z. The non-zero Y lets us prove the bot ADOPTS the mesh height rather
    /// than keeping its start Y. Two triangles per segment, sharing edges so A* can route the length.
    /// </summary>
    private static NavMesh FlatStrip(int segments = 12, float step = 2f, float y = 5f)
    {
        var verts = new List<float>();
        for (int i = 0; i <= segments; i++)
        {
            float x = i * step;
            verts.AddRange(new[] { x, y, 0f });   // bottom edge (z=0)
            verts.AddRange(new[] { x, y, 4f });   // top edge   (z=4)
        }
        var tris = new List<int>();
        for (int i = 0; i < segments; i++)
        {
            int b0 = i * 2, t0 = i * 2 + 1, b1 = (i + 1) * 2, t1 = (i + 1) * 2 + 1;
            tris.AddRange(new[] { b0, b1, t0 });   // lower triangle of the quad
            tris.AddRange(new[] { t0, b1, t1 });   // upper triangle of the quad
        }
        return new NavMesh(verts.ToArray(), tris.ToArray());
    }

    private static RoomWorldState World(params (int view, string kind, float x, float z)[] entities)
    {
        var w = new RoomWorldState();
        foreach (var (view, kind, x, z) in entities) w.ObserveSpawn(view, kind, x, 0f, z);
        return w;
    }

    [Fact]
    public void With_a_navmesh_every_move_stays_on_the_walkable_surface()
    {
        var mesh = FlatStrip();
        // Start near one end, target an enemy far along the strip so the bot keeps approaching for many ticks.
        var bot = new HunterBehavior(BotView, startX: 1f, startZ: 2f, startY: 5f, seed: 1, navMesh: mesh);
        var world = World((1002, "SpiderEnemy", 22f, 2f));

        for (int i = 0; i < 15; i++)
        {
            var step = bot.Think(world);
            // Every emitted position must lie ON the mesh (SampleHeight non-null) — no float, no off-surface.
            float? h = mesh.SampleHeight(step.Position.X, step.Position.Z);
            Assert.True(h is not null,
                $"tick {i}: bot left the navmesh at ({step.Position.X},{step.Position.Z})");
            // And it must sit AT the surface height (y=5), not its arbitrary start/anchor Y.
            Assert.Equal(5f, step.Position.Y, precision: 3);
        }
    }

    [Fact]
    public void With_a_navmesh_the_bot_makes_progress_toward_a_distant_target()
    {
        var mesh = FlatStrip();
        var bot = new HunterBehavior(BotView, startX: 1f, startZ: 2f, startY: 5f, seed: 1, navMesh: mesh);
        var world = World((1002, "SpiderEnemy", 22f, 2f));

        float startX = bot.Think(world).Position.X;
        for (int i = 0; i < 8; i++) bot.Think(world);
        float laterX = bot.Think(world).Position.X;
        Assert.True(laterX > startX + 2f,
            $"expected the navmesh bot to advance toward the target: {startX} -> {laterX}");
    }

    [Fact]
    public void With_a_navmesh_an_in_range_attack_still_fires_the_same_rpc()
    {
        // The navmesh changes MOVEMENT only — the seek/attack logic is unchanged. An enemy in range is still
        // attacked with a TakeDamage RPC, and the acting position remains on the surface.
        var mesh = FlatStrip();
        var bot = new HunterBehavior(BotView, startX: 1f, startZ: 2f, startY: 5f, seed: 1, navMesh: mesh);
        var step = bot.Think(World((1002, "CrabEnemy", 3f, 2f)));   // within attack range of the start
        var rpc = Assert.Single(step.Actions);
        var info = PunRpcInfo.From(rpc);
        Assert.NotNull(info);
        Assert.Equal("TakeDamage", info!.Value.Method);
        Assert.NotNull(mesh.SampleHeight(step.Position.X, step.Position.Z));   // acted from a walkable point
        Assert.Equal(5f, step.Position.Y, precision: 3);
    }

    [Fact]
    public void Teleport_far_off_the_mesh_keeps_the_bot_there_instead_of_snapping_onto_it()
    {
        // Regression for the summon snap-back: when the realm's navmesh covers a region far from where the
        // player actually is (wrong map / different arena), a bot summoned to the player must STAY at the
        // summon spot — the post-teleport surface snap must disengage rather than yank the bot onto the
        // distant mesh (the bug that made summoned bots snap right back).
        var mesh = FlatStrip();                          // covers x∈[0,24], z∈[0,4], y=5
        var bot = new HunterBehavior(BotView, startX: 1f, startZ: 2f, startY: 5f, seed: 1, navMesh: mesh);
        bot.Teleport(500f, 12f, 500f);                   // summoned far outside the mesh region
        var p = bot.Tick();                              // what the manager would broadcast next
        Assert.True(System.Math.Abs(p.X - 500f) < 1f && System.Math.Abs(p.Z - 500f) < 1f,
            $"bot was yanked off its summon spot onto the far mesh: ({p.X},{p.Z})");
        Assert.Equal(12f, p.Y, precision: 3);            // kept the player-anchored height, not the mesh y=5

        // And an idle Think keeps it near the summon spot (patrol radius), never back at the mesh.
        var step = bot.Think(new RoomWorldState());
        double dist = System.Math.Sqrt(System.Math.Pow(step.Position.X - 500f, 2) + System.Math.Pow(step.Position.Z - 500f, 2));
        Assert.True(dist < PatrolRadiusBound, $"idle bot drifted off the summon spot toward the mesh: moved {dist:F1}u");
    }

    // Patrol radius (5) plus a small slack — an idle off-mesh bot circles its summon spot, it doesn't return to the mesh.
    private const double PatrolRadiusBound = 8.0;

    [Fact]
    public void Teleport_onto_the_mesh_still_snaps_to_the_surface()
    {
        // The positive half: when the summon spot IS on the mesh, the bot still adopts the surface height —
        // the coverage gate only disengages when the mesh genuinely doesn't cover the spot.
        var mesh = FlatStrip();
        var bot = new HunterBehavior(BotView, startX: 1f, startZ: 2f, startY: 0f, seed: 1, navMesh: mesh);
        bot.Teleport(10f, 99f, 2f);                      // on the strip in XZ, wrong Y
        var p = bot.Tick();
        Assert.Equal(5f, p.Y, precision: 3);             // snapped to the mesh surface height
    }

    [Fact]
    public void Without_a_navmesh_movement_is_unchanged_keeping_the_anchored_height()
    {
        // The graceful-fallback contract: no navmesh → exactly today's behavior. The bot keeps its anchored
        // Y (it never adopts a mesh height because there is none) and steps straight toward the enemy. This
        // mirrors HunterBehaviorTests.Approaches_a_distant_enemy_without_acting with an explicit Y assertion.
        var bot = new HunterBehavior(BotView, startX: 0f, startZ: 0f, startY: 7f, seed: 1);   // navMesh: null
        var step = bot.Think(World((1002, "SpiderEnemy", 100f, 0f)));
        Assert.Empty(step.Actions);                       // too far to act
        Assert.Contains("approach", step.Label);
        Assert.Equal(7f, step.Position.Y, precision: 3);  // anchored Y preserved — no surface to snap to
        Assert.True(step.Position.X > 0f && step.Position.X <= 6.01f,
            $"expected a bounded straight step toward the enemy, got x={step.Position.X}");
    }
}
