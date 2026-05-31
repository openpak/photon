using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing.Authority;

namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>
/// A world-aware playerbot brain. Each think it reads the room's authoritative <see cref="RoomWorldState"/>,
/// picks the nearest real target the master client has spawned — an <b>enemy</b> to kill, a <b>Link</b> to
/// hack, or <b>loot</b> to grab (in that priority) — steps toward it in bounded increments, and once in range
/// emits the matching captured RPC. Resolved interactions accrue pseudo-XP; crossing a level threshold emits
/// a progression RPC (so the bot visibly "gets stronger"). With no known target it <b>holds position</b>
/// rather than wandering blindly into geometry.
///
/// <para><b>Clean-room navigation ("collision"):</b> the server owns no level mesh, so true physics collision
/// is impossible. Instead the bot only ever moves toward positions the master has <em>spawned things at</em> —
/// provably valid, reachable in-world points — and holds when it has none. That keeps bots on the graph of
/// real points without any level geometry. See docs/superpowers/specs/2026-05-31-smart-bots-design.md.</para>
///
/// <para>Deterministic given a seed; fully unit-testable against a hand-built world-state (no live server).</para>
/// </summary>
public sealed class HunterBehavior : IBotBrain
{
    // Tunables (units are game-world units; cadence is per maintenance tick ~1 Hz).
    private const float StepSpeed = 6f;       // max distance moved toward a target per tick
    private const float AttackRange = 5f;     // within this XZ distance the bot acts instead of moving
    private const int XpPerAction = 10;       // pseudo-XP gained per resolved interaction
    private const int XpPerLevel = 100;       // XP needed to advance a level

    private static readonly EventData[] NoActions = Array.Empty<EventData>();

    private readonly int _viewId;
    private readonly Random _rng;
    private float _x, _y, _z;
    private int _targetViewId;                 // 0 = no current target

    public int Xp { get; private set; }
    public int Level { get; private set; } = 1;

    public HunterBehavior(int viewId, float startX, float startZ, int? seed = null)
    {
        _viewId = viewId;
        _x = startX; _z = startZ; _y = 0f;
        _rng = seed is int s ? new Random(s) : new Random();
    }

    /// <summary>Move-only fallback (no world): hold position. The brain path drives real behavior.</summary>
    public BotPositionUpdate Tick() => new(_x, _y, _z);

    public BotStep Think(RoomWorldState world)
    {
        var target = ResolveTarget(world);
        if (target is not null)
        {
            _targetViewId = target.ViewId;
            double dx = target.X - _x, dz = target.Z - _z;
            double dist = Math.Sqrt(dx * dx + dz * dz);

            if (dist > AttackRange)
            {
                StepToward(target);
                return new BotStep(new BotPositionUpdate(_x, _y, _z), NoActions, $"approach {Describe(target)}");
            }

            // In range: act on the target, accrue XP, and maybe level up.
            var actions = new List<EventData>();
            string label = ActOn(target, actions);
            Xp += XpPerAction;
            if (Xp >= Level * XpPerLevel)
            {
                Level++;
                actions.Add(LevelUpRpc());
                label += $" +level{Level}";
            }
            return new BotStep(new BotPositionUpdate(_x, _y, _z), actions, label);
        }

        // No actionable target. Rather than sit at the spawn point (possibly inside geometry), drift toward
        // the nearest KNOWN entity — a player's avatar, scene prop, anything the master has spawned — since
        // those are real, reachable in-world points. This is the cold-start behavior that gets bots out of
        // origin and toward where the action is, without any level geometry (waypoint-on-spawns). Never acts.
        _targetViewId = 0;
        var anchor = world.Nearest(e => e.ViewId != _viewId, _x, _z);
        if (anchor is not null)
        {
            double dx = anchor.X - _x, dz = anchor.Z - _z;
            if (Math.Sqrt(dx * dx + dz * dz) > AttackRange)
            {
                StepToward(anchor);
                return new BotStep(new BotPositionUpdate(_x, _y, _z), NoActions, $"regroup {Describe(anchor)}");
            }
        }
        return new BotStep(new BotPositionUpdate(_x, _y, _z), NoActions, "idle");   // truly nothing known
    }

    /// <summary>Step toward <paramref name="target"/> by at most <see cref="StepSpeed"/> (waypoint-on-spawns).</summary>
    private void StepToward(RoomWorldState.Entity target)
    {
        double dx = target.X - _x, dz = target.Z - _z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist <= 0) { _y = target.Y; return; }
        double f = Math.Min(1.0, StepSpeed / dist);
        _x += (float)(dx * f);
        _z += (float)(dz * f);
        _y = target.Y;   // match the target's height so airborne entities are reachable
    }

    /// <summary>Keep the current actionable target if still alive; otherwise pick the nearest enemy, then
    /// Link, then loot. Returns null when nothing actionable is known (caller then regroups or idles).</summary>
    private RoomWorldState.Entity? ResolveTarget(RoomWorldState world)
    {
        if (_targetViewId != 0 && world.Get(_targetViewId) is { Alive: true } current && IsTargetable(current))
            return current;

        return world.Nearest(IsEnemy, _x, _z)
            ?? world.Nearest(IsHackNode, _x, _z)
            ?? world.Nearest(IsLoot, _x, _z);
    }

    private string ActOn(RoomWorldState.Entity target, List<EventData> actions)
    {
        if (IsEnemy(target))   { actions.Add(DamageRpc(target.ViewId, 25f)); return $"attack {target.Kind}"; }
        if (IsHackNode(target)){ actions.Add(HackRpc(target.ViewId));        return $"hack {target.Kind}"; }
        actions.Add(LootRpc(target.ViewId));
        return $"loot {target.Kind}";
    }

    // --- target classification (by observed prefab name) -----------------------------------------

    private bool IsTargetable(RoomWorldState.Entity e) =>
        e.ViewId != _viewId && (IsEnemy(e) || IsHackNode(e) || IsLoot(e));

    private static bool IsEnemy(RoomWorldState.Entity e) => e.Kind is { } k && k.Contains("Enemy", StringComparison.OrdinalIgnoreCase);
    private static bool IsHackNode(RoomWorldState.Entity e) => string.Equals(e.Kind, "Link", StringComparison.OrdinalIgnoreCase);
    private static bool IsLoot(RoomWorldState.Entity e) =>
        e.Kind is { } k && (k.Equals("NetworkLootCube", StringComparison.OrdinalIgnoreCase)
                            || k.Equals("XPGem", StringComparison.OrdinalIgnoreCase)
                            || k.Contains("Powerup", StringComparison.OrdinalIgnoreCase));

    private static string Describe(RoomWorldState.Entity e) => e.Kind ?? $"view{e.ViewId}";

    // --- RPC builders (captured wire shapes; see GameActions / live-verification) -----------------

    private static EventData DamageRpc(int targetView, float damage) =>
        Rpc(targetView, "TakeDamage", new object[] { targetView, DamageData.BuildPacket(damage) });

    private EventData HackRpc(int targetView) =>
        // SetupHack(nodeHp, sourcePos, range, a, b, power) — the bot supplies its own position as the source.
        Rpc(targetView, "SetupHack", new object[] { 100, Vec3(_x, _y, _z), 30f, 0, 0, 50f });

    private static EventData LootRpc(int targetView) => Rpc(targetView, "GetLock", Array.Empty<object>());

    private EventData LevelUpRpc() =>
        // AddBuffRPC(buffId, stacks, duration, a, magnitude, b) on the bot's own avatar — a visible "got stronger".
        Rpc(_viewId, "AddBuffRPC", new object[] { 1, Level, 30f, 0, 1.5f, 0 });

    private static EventData Rpc(int viewId, string method, object[] args) =>
        new(PhotonCodes.PunEvent.Rpc, new()
        {
            { PhotonCodes.Param.Code, PhotonCodes.PunEvent.Rpc },
            { PhotonCodes.Param.Data, new Dictionary<object, object>
                {
                    { PhotonCodes.RpcKey.ViewId, viewId },
                    { PhotonCodes.RpcKey.MethodName, method },
                    { PhotonCodes.RpcKey.Args, args },
                } },
        });

    private static PhotonCustomData Vec3(float x, float y, float z)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        return new PhotonCustomData(PhotonCodes.CustomType.Vector3, b);
    }
}
