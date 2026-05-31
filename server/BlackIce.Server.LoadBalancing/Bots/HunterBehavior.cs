using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing.Authority;

namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>
/// A world-aware playerbot brain. Each think it reads the room's authoritative <see cref="RoomWorldState"/>,
/// picks a real target the master client has spawned — an <b>enemy</b> to kill, a <b>Link</b> to hack, or
/// <b>loot</b> to grab (in that priority) — moves toward it, and once in range emits the matching captured
/// RPC. Resolved interactions accrue pseudo-XP; crossing a level threshold emits a progression RPC.
///
/// <para><b>Fleet behavior (so 10 bots don't look like 1):</b></para>
/// <list type="bullet">
/// <item><b>Fan-out</b> — each bot targets the <see cref="RoomWorldState.NearestRanked"/> entity at its own
/// fleet index, so bots spread across distinct targets instead of all swarming the single nearest.</item>
/// <item><b>Orbit</b> — in range, a bot stands at its own angular slot a short radius from the target
/// (not the target's exact center), so a group rings the target rather than stacking into one capsule.</item>
/// <item><b>Rotate</b> — after acting on a target for a while it cools that target down and moves to another
/// (when one exists), so bots patrol the arena instead of freezing on one item forever. The game never
/// removes a looted/killed thing from the bot's view, so without this they'd spam one target indefinitely.</item>
/// </list>
///
/// <para><b>Clean-room navigation:</b> the server owns no level mesh, so bots only ever move toward entities
/// with a KNOWN position the master spawned (provably reachable) — never to an unknown/defaulted (0,0,0) —
/// and hold when nothing is known. True physics collision is impossible server-side. See the smart-bots spec.</para>
///
/// <para>Deterministic given a seed + fleet index; fully unit-testable against a hand-built world-state.</para>
/// </summary>
public sealed class HunterBehavior : IBotBrain
{
    private const float StepSpeed = 6f;        // max distance moved toward a target per tick
    private const float AttackRange = 5f;      // within this XZ distance to the target the bot acts
    private const float OrbitRadius = 2.5f;    // where the bot stands while acting (< AttackRange so it still acts)
    private const int XpPerAction = 10;        // pseudo-XP per resolved interaction
    private const int XpPerLevel = 100;        // XP to advance a level
    private const int MaxActsPerTarget = 3;    // after this many acts, rotate to another target (if one exists)
    private const long CooldownTicks = 10;     // how long a rotated-off target stays deprioritized

    private static readonly EventData[] NoActions = Array.Empty<EventData>();
    private static readonly Func<RoomWorldState.Entity, bool> AnyEntity = static _ => true;

    private readonly int _viewId;
    private readonly int _fleetIndex;
    private readonly float _orbitAngle;
    private readonly Random _rng;
    private float _x, _y, _z;

    private long _tick;
    private int _targetViewId;                 // 0 = no current target
    private int _actsOnCurrent;
    private readonly Dictionary<int, long> _cooldownUntil = new();

    public int Xp { get; private set; }
    public int Level { get; private set; } = 1;

    public HunterBehavior(int viewId, float startX, float startZ, float startY = 0f, int fleetIndex = 0, int? seed = null)
    {
        _viewId = viewId;
        _fleetIndex = Math.Max(0, fleetIndex);
        _orbitAngle = _fleetIndex * 2.399963f;   // golden angle → even spread of orbit slots
        _x = startX; _z = startZ; _y = startY;    // start on the safe ground height the manager anchored us to
        _rng = seed is int s ? new Random(s) : new Random();
    }

    /// <summary>Move-only fallback (no world): hold position. The brain path drives real behavior.</summary>
    public BotPositionUpdate Tick() => new(_x, _y, _z);

    public BotStep Think(RoomWorldState world)
    {
        _tick++;
        var target = ResolveTarget(world);
        if (target is not null)
        {
            double dx = target.X - _x, dz = target.Z - _z;
            double dist = Math.Sqrt(dx * dx + dz * dz);

            if (dist > AttackRange)
            {
                StepToward(target.X, target.Z);
                return new BotStep(new BotPositionUpdate(_x, _y, _z), NoActions, $"approach {Describe(target)}");
            }

            // In range: take the bot's own orbit slot around the target (so bots ring it, not stack), then act.
            float ox = target.X + OrbitRadius * (float)Math.Cos(_orbitAngle);
            float oz = target.Z + OrbitRadius * (float)Math.Sin(_orbitAngle);
            StepToward(ox, oz);

            var actions = new List<EventData>();
            string label = ActOn(target, actions);
            _actsOnCurrent++;
            Xp += XpPerAction;
            if (Xp >= Level * XpPerLevel)
            {
                Level++;
                actions.Add(LevelUpRpc());
                label += $" +level{Level}";
            }
            return new BotStep(new BotPositionUpdate(_x, _y, _z), actions, label);
        }

        // No actionable target. Drift toward the nearest KNOWN entity (a player avatar, scene prop — any real
        // spawn point) so the bot leaves its spawn and gravitates to where the action is, rather than sitting
        // (possibly inside geometry). Never acts on a non-target.
        _targetViewId = 0; _actsOnCurrent = 0;
        var anchor = world.Nearest(e => e.ViewId != _viewId, _x, _z);
        if (anchor is not null)
        {
            double dx = anchor.X - _x, dz = anchor.Z - _z;
            if (Math.Sqrt(dx * dx + dz * dz) > AttackRange)
            {
                // Regrouping toward a real player's avatar: adopt their ground height — a player's Y is a
                // known-walkable height, unlike a loot/enemy spawn Y which may be arbitrary/airborne.
                if (IsPlayer(anchor)) _y = anchor.Y;
                StepToward(anchor.X, anchor.Z);
                return new BotStep(new BotPositionUpdate(_x, _y, _z), NoActions, $"regroup {Describe(anchor)}");
            }
        }
        return new BotStep(new BotPositionUpdate(_x, _y, _z), NoActions, "idle");   // truly nothing known
    }

    /// <summary>
    /// Step toward (tx,tz) by at most <see cref="StepSpeed"/>, in the XZ plane only — the bot keeps its
    /// current ground height <c>_y</c>. The server has no level geometry, so the only height it can trust is
    /// the safe ground the manager anchored the bot to (a player's walkable Y); adopting a loot/enemy spawn's
    /// Y would float the bot into the air. Vertical changes happen only when regrouping to a player.
    /// </summary>
    private void StepToward(float tx, float tz)
    {
        double dx = tx - _x, dz = tz - _z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist <= 0) return;
        double f = Math.Min(1.0, StepSpeed / dist);
        _x += (float)(dx * f);
        _z += (float)(dz * f);
    }

    /// <summary>
    /// Picks the bot's target: keep the current one while it's alive, targetable, and not over-worked;
    /// otherwise choose a fresh one (priority enemy → Link → loot), ranked by fleet index so bots fan out
    /// and skipping recently-rotated targets. When a target is over-worked it is only abandoned if an
    /// alternative exists — a lone enemy keeps getting hit rather than the bot going idle.
    /// </summary>
    private RoomWorldState.Entity? ResolveTarget(RoomWorldState world)
    {
        if (_targetViewId != 0 && world.Get(_targetViewId) is { Alive: true } cur && IsTargetable(cur))
        {
            if (_actsOnCurrent < MaxActsPerTarget) return cur;
            var alt = PickFresh(world, exclude: cur.ViewId);
            if (alt is not null)
            {
                _cooldownUntil[cur.ViewId] = _tick + CooldownTicks;
                _targetViewId = alt.ViewId; _actsOnCurrent = 0;
                return alt;
            }
            return cur;   // nothing else to do — keep acting on the only target
        }

        var fresh = PickFresh(world, exclude: 0);
        _targetViewId = fresh?.ViewId ?? 0;
        _actsOnCurrent = 0;
        return fresh;
    }

    /// <summary>Nearest-ranked eligible target by priority (enemy → hack → loot), excluding a viewId, the bot
    /// itself, and on-cooldown targets.</summary>
    private RoomWorldState.Entity? PickFresh(RoomWorldState world, int exclude)
    {
        bool Eligible(RoomWorldState.Entity e) => e.ViewId != _viewId && e.ViewId != exclude && !OnCooldown(e.ViewId);
        return world.NearestRanked(e => Eligible(e) && IsEnemy(e), _x, _z, _fleetIndex)
            ?? world.NearestRanked(e => Eligible(e) && IsHackNode(e), _x, _z, _fleetIndex)
            ?? world.NearestRanked(e => Eligible(e) && IsLoot(e), _x, _z, _fleetIndex);
    }

    private bool OnCooldown(int viewId) => _cooldownUntil.TryGetValue(viewId, out var t) && t > _tick;

    private string ActOn(RoomWorldState.Entity target, List<EventData> actions)
    {
        if (IsEnemy(target))    { actions.Add(DamageRpc(target.ViewId, 25f)); return $"attack {target.Kind}"; }
        if (IsHackNode(target)) { actions.Add(HackRpc(target.ViewId));        return $"hack {target.Kind}"; }
        actions.Add(LootRpc(target.ViewId));
        return $"loot {target.Kind}";
    }

    private bool IsTargetable(RoomWorldState.Entity e) =>
        e.ViewId != _viewId && (IsEnemy(e) || IsHackNode(e) || IsLoot(e));

    private static bool IsPlayer(RoomWorldState.Entity e) => string.Equals(e.Kind, "Player", StringComparison.OrdinalIgnoreCase);
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
        Rpc(targetView, "SetupHack", new object[] { 100, Vec3(_x, _y, _z), 30f, 0, 0, 50f });

    private static EventData LootRpc(int targetView) => Rpc(targetView, "GetLock", Array.Empty<object>());

    private EventData LevelUpRpc() =>
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
