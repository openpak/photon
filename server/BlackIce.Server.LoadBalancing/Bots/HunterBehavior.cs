using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Core.Navigation;
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
/// <para><b>Idle patrol:</b> with nothing to hunt (e.g. a realm of only loot, all on cooldown), the bot
/// circles its anchor (the player / last known point) rather than freezing — so bots stay visibly alive
/// between spawns.</para>
///
/// <para><b>Navmesh-aware movement (when available):</b> if the room's map was extracted to a
/// <c>maps/&lt;name&gt;.navmesh</c> artifact, the manager hands this brain a <see cref="NavMesh"/>. Then every
/// move SNAPS to the nearest walkable point (<see cref="NavMesh.NearestPoint"/>) — no floating, no clipping
/// into hazards — adopting the mesh's surface Y, and APPROACH routes via <see cref="NavMeshPath.Find"/>
/// waypoints (around walls) instead of a straight line. The seek/attack/orbit/patrol logic is unchanged;
/// only the <em>movement</em> becomes surface-aware.</para>
///
/// <para><b>Clean-room navigation + the fallback when no mesh is present:</b> without an extracted map the
/// server owns no level mesh — the master only relays dynamic gameplay entities (loot, powerups, barrels,
/// players), never terrain/buildings/navmesh, so there is genuinely nothing on the wire to collide against.
/// In that (default) case the bot moves only toward entities with a KNOWN position the master spawned
/// (provably reachable points), keeps the player-anchored ground height, and CLIPS THROUGH static geometry —
/// exactly today's behavior. See the smart-bots and map-navmesh specs.</para>
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
    private const float PatrolRadius = 5f;     // radius of the idle-patrol circle around the anchor
    private const double PatrolSpeed = 0.6;    // radians/tick the patrol angle advances (a slow circle)
    private const float SnapCoverageRadius = 25f;  // max XZ distance the nearest navmesh point may be before we
                                                   // treat the bot as off-mesh (see SurfaceAt)
    private const float SnapVerticalTolerance = 15f;  // max |meshY - playerAnchoredY| before we treat the mesh
                                                      // as vertically misaligned and keep the anchored height
    private const float LeashRadius = 60f;     // max XZ distance a bot may stray from the nearest player — with
                                               // no live terrain to stand on, this keeps bots in the playable
                                               // area around the human instead of walking straight off the map

    private static readonly EventData[] NoActions = Array.Empty<EventData>();

    private readonly int _viewId;
    private readonly int _fleetIndex;
    private readonly float _orbitAngle;
    private readonly Random _rng;
    private NavMesh? _navMesh;   // null = no extracted map → today's player-anchor movement; swapped live by
                                 // the map auto-selector via SetNavMesh once a room's map is identified
    private float _x, _y, _z;

    private long _tick;
    private double _patrolAngle;                // advances while idle-patrolling around the anchor
    private int _targetViewId;                 // 0 = no current target
    private int _actsOnCurrent;
    private bool _navDisengagedWarned;         // warn-once when the navmesh doesn't cover the bot's region
    private float _navYOffset;                  // added to mesh surface Y to rebase the navmesh into the live
                                               // world's frame (baked level sits ~63u below); measured per room
                                               // from the player by the map auto-selector
    private readonly Dictionary<int, long> _cooldownUntil = new();

    public int Xp { get; private set; }
    public int Level { get; private set; } = 1;

    public HunterBehavior(int viewId, float startX, float startZ, float startY = 0f, int fleetIndex = 0,
                          int? seed = null, NavMesh? navMesh = null)
    {
        _viewId = viewId;
        _fleetIndex = Math.Max(0, fleetIndex);
        _orbitAngle = _fleetIndex * 2.399963f;   // golden angle → even spread of orbit slots
        _x = startX; _z = startZ; _y = startY;    // start on the safe ground height the manager anchored us to
        _rng = seed is int s ? new Random(s) : new Random();
        _navMesh = navMesh;
        // With a navmesh, the spawn anchor may be slightly off-surface (it came from a player's 201 stream,
        // not the mesh); snap onto the walkable surface immediately so we never start floating/clipping —
        // but only if the mesh actually covers the spawn point (SurfaceAt guards the coordinate-mismatch case).
        SnapToSurface();
    }

    /// <summary>Move-only fallback (no world): hold position. The brain path drives real behavior.</summary>
    public BotPositionUpdate Tick() => new(_x, _y, _z);

    /// <summary>Force the bot to a position (admin <c>summon</c>), then snap onto the navmesh if present and
    /// clear the current target so it re-evaluates from the new spot rather than walking straight back.</summary>
    public void Teleport(float x, float y, float z)
    {
        _x = x; _y = y; _z = z;
        _targetViewId = 0; _actsOnCurrent = 0;
        SnapToSurface();
    }

    /// <summary>Swap the navmesh this bot paths on — the map auto-selector calls this once a room's live map
    /// is identified (or changes). A no-op when unchanged. Clears the "navmesh doesn't cover us" warn-latch so
    /// a genuinely new map gets a fresh coverage evaluation.</summary>
    public void SetNavMesh(NavMesh? navMesh, float yOffset = 0f)
    {
        _navYOffset = yOffset;
        if (ReferenceEquals(navMesh, _navMesh)) return;
        _navMesh = navMesh;
        _navDisengagedWarned = false;
    }

    public BotStep Think(RoomWorldState world)
    {
        _tick++;
        LeashToPlayer(world);   // keep the bot in the playable area around the human (no live terrain otherwise)
        var target = ResolveTarget(world);
        if (target is not null)
        {
            double dx = target.X - _x, dz = target.Z - _z;
            double dist = Math.Sqrt(dx * dx + dz * dz);

            if (dist > AttackRange)
            {
                ApproachToward(target.X, target.Z);   // navmesh-routed when a mesh is present, straight otherwise
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

        // No actionable target. Regroup toward the PLAYER if one is known (so an idle fleet gathers on the
        // human and is easy to find), else the nearest known entity (scene prop / spawn) so the bot still
        // leaves its spot and gravitates to where the action is. Never acts on a non-target.
        _targetViewId = 0; _actsOnCurrent = 0;
        var anchor = world.Nearest(IsPlayer, _x, _z)
                     ?? world.Nearest(e => e.ViewId != _viewId, _x, _z);
        if (anchor is not null)
        {
            double dx = anchor.X - _x, dz = anchor.Z - _z;
            if (Math.Sqrt(dx * dx + dz * dz) > AttackRange)
            {
                // Regrouping toward a real player's avatar: with no navmesh, adopt their ground height — a
                // player's Y is a known-walkable height, unlike a loot/enemy spawn Y which may be
                // arbitrary/airborne. With a navmesh the surface Y is authoritative, so ApproachToward's snap
                // supersedes this (it overwrites _y from the mesh).
                if (_navMesh is null && IsPlayer(anchor)) _y = anchor.Y;
                ApproachToward(anchor.X, anchor.Z);
                return new BotStep(new BotPositionUpdate(_x, _y, _z), NoActions, $"regroup {Describe(anchor)}");
            }

            // Reached the anchor and nothing to hunt → PATROL: orbit slowly around the anchor (the player
            // or last known point) instead of freezing. Keeps bots visibly alive between loot/enemy spawns.
            // XZ-only at the bot's current ground height; radius/angle per-bot so the group circles, not stacks.
            return Patrol(anchor.X, anchor.Z);
        }

        // Truly nothing known yet (no entities observed at all): patrol around our own spot so we still move.
        return Patrol(_x, _z);
    }

    /// <summary>
    /// Idle patrol: walk a slow circle of radius <see cref="PatrolRadius"/> around (cx,cz), advancing the
    /// per-bot patrol angle each tick. Keeps a bot visibly moving (not frozen) when it has nothing to hunt,
    /// at its current ground height, in XZ only. The angle is offset by the bot's orbit slot so a group of
    /// idle bots circles the anchor at spread phases rather than overlapping.
    /// </summary>
    private BotStep Patrol(float cx, float cz)
    {
        _patrolAngle += PatrolSpeed;
        double a = _patrolAngle + _orbitAngle;
        _x = cx + PatrolRadius * (float)Math.Cos(a);
        _z = cz + PatrolRadius * (float)Math.Sin(a);
        SnapToSurface();   // keep the patrol circle on the walkable surface when a navmesh is present
        return new BotStep(new BotPositionUpdate(_x, _y, _z), NoActions, "patrol");
    }

    /// <summary>
    /// Step toward (tx,tz) by at most <see cref="StepSpeed"/> in the XZ plane.
    ///
    /// <para><b>Without a navmesh</b> (default): XZ only — the bot keeps its current ground height <c>_y</c>.
    /// The server has no level geometry, so the only height it can trust is the safe ground the manager
    /// anchored the bot to (a player's walkable Y); adopting a loot/enemy spawn's Y would float the bot into
    /// the air. Vertical changes happen only when regrouping to a player.</para>
    ///
    /// <para><b>With a navmesh:</b> after the XZ step, snap onto the walkable surface
    /// (<see cref="NavMesh.NearestPoint"/>) and adopt its Y — so the bot stays on real ground (no float, no
    /// clipping into a hazard) without needing a player as its height anchor.</para>
    /// </summary>
    private void StepToward(float tx, float tz)
    {
        double dx = tx - _x, dz = tz - _z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist > 0)
        {
            double f = Math.Min(1.0, StepSpeed / dist);
            _x += (float)(dx * f);
            _z += (float)(dz * f);
        }
        SnapToSurface();
    }

    /// <summary>
    /// Keep the bot within <see cref="LeashRadius"/> (XZ) of the nearest player. The server has no live terrain
    /// for the procedurally-assembled world (the static navmeshes don't match it — see MapSelector), so nothing
    /// otherwise stops a bot chasing a far target from walking in a straight line off the map into the void.
    /// The player stands on provably-walkable ground, so leashing to them keeps bots in the playable area.
    ///
    /// <para>Runs at the top of <see cref="Think"/> before movement: if the bot is beyond the leash it is
    /// pulled back onto the leash circle, so outward drift is capped at one step beyond the boundary while
    /// inward movement (regrouping to the player) is unaffected. No player known → no leash (pre-join).</para>
    /// </summary>
    private void LeashToPlayer(RoomWorldState world)
    {
        var p = world.Nearest(IsPlayer, _x, _z);
        if (p is null) return;
        double dx = _x - p.X, dz = _z - p.Z;
        double d = Math.Sqrt(dx * dx + dz * dz);
        if (d <= LeashRadius || d < 1e-3) return;
        double f = LeashRadius / d;
        _x = p.X + (float)(dx * f);
        _z = p.Z + (float)(dz * f);
    }

    /// <summary>Snaps the bot onto the nearest walkable point of the navmesh (adopting its surface Y) when a
    /// mesh is present AND actually covers this spot; a no-op otherwise — so the no-navmesh path (and the
    /// off-mesh fallback) keeps the player-anchored position exactly.</summary>
    private void SnapToSurface()
    {
        if (SurfaceAt(_x, _z) is { } p) { _x = p.x; _y = p.y; _z = p.z; }
    }

    /// <summary>
    /// The nearest walkable surface point to (x,z) — but ONLY if the mesh genuinely aligns with the bot's
    /// current position: the nearest point must be within <see cref="SnapCoverageRadius"/> in XZ AND within
    /// <see cref="SnapVerticalTolerance"/> in Y of the bot's player-anchored height. Returns null otherwise.
    ///
    /// <para><see cref="NavMesh.NearestPoint"/> always returns SOME point, even for a query far outside the
    /// extracted region, and matching XZ does not imply a matching vertical frame. Two real failures this
    /// guards: (1) the realm's mapped navmesh is a different level — nearest point is hundreds of units away in
    /// XZ (the summon snap-back); (2) the navmesh footprint matches the level but its Y frame is offset — the
    /// surface sits tens of units below the live floor, so snapping drops the bot underground (level12's mesh
    /// Y is ≈[-90,-21] while the player stands near 0). Either way we disengage and keep the player-anchored
    /// position, which is a known-good walkable height. The navmesh is thus a best-effort enhancement: used
    /// only where it truly fits, otherwise today's player-anchor movement.</para>
    /// </summary>
    private (float x, float y, float z)? SurfaceAt(float x, float z)
    {
        // Floor-aware: ask for the surface whose height is nearest the bot's CURRENT world Y (expressed in the
        // mesh's frame, i.e. minus the rebase offset), so in a multi-storey navmesh we snap to the bot's own
        // floor rather than an upper floor sharing the XZ.
        if (_navMesh is not { } nav || !nav.NearestPoint(x, z, _y - _navYOffset, out var p, out _)) return null;
        p.y += _navYOffset;   // rebase the mesh surface into the live world's vertical frame before any check
        float dx = p.x - x, dz = p.z - z;
        bool xzFar = dx * dx + dz * dz > SnapCoverageRadius * SnapCoverageRadius;
        bool yFar = Math.Abs(p.y - _y) > SnapVerticalTolerance;
        if (xzFar || yFar)
        {
            if (!_navDisengagedWarned)
            {
                _navDisengagedWarned = true;
                string why = xzFar
                    ? $"nearest surface {Math.Sqrt(dx * dx + dz * dz):F0}u away in XZ"
                    : $"surface Y {p.y:F0} vs bot Y {_y:F0} (Δ{Math.Abs(p.y - _y):F0}) — vertical frame mismatch";
                Log.Warn("Bots", $"navmesh does not align for bot {_viewId} at ({x:F0},{z:F0}) — {why}; " +
                                 "using player-anchored movement (wrong map/frame for this realm?)");
            }
            return null;
        }
        return p;
    }

    /// <summary>
    /// Move toward (tx,tz) for one tick, routing on the navmesh when one is present.
    ///
    /// <para><b>With a navmesh:</b> compute an A* corridor of waypoints (<see cref="NavMeshPath.Find"/>) that
    /// stays on the walkable surface and step toward the FIRST one — so the bot walks around walls/hazards
    /// instead of straight through them. If pathing yields nothing (target off-mesh / unreachable corridor),
    /// fall back to a straight surface-snapped step rather than freezing.</para>
    ///
    /// <para><b>Without a navmesh</b> (default): identical to <see cref="StepToward"/> — a straight XZ step,
    /// today's behavior.</para>
    /// </summary>
    private void ApproachToward(float tx, float tz)
    {
        // Route on the mesh only when the bot is actually ON it. If the bot is off-mesh (the navmesh doesn't
        // cover this region), NavMeshPath would snap the off-mesh start into mesh-space and walk the bot away —
        // the same coordinate-mismatch trap SurfaceAt guards. Off-mesh → straight step (left un-yanked by the
        // guarded SnapToSurface), i.e. today's player-anchored movement.
        if (_navMesh is { } nav && SurfaceAt(_x, _z) is not null)
        {
            var path = NavMeshPath.Find(nav, _x, _z, tx, tz);
            if (path.Count > 0)
            {
                var next = path[0];   // first waypoint along the walkable corridor
                StepToward(next.x, next.z);
                return;
            }
        }
        StepToward(tx, tz);   // no mesh, off-mesh, or no corridor → straight step (player-anchored)
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
            // Over-worked the only available target and nothing fresh is off cooldown. Don't re-pin to it
            // (that's the "all bots camp one enemy forever, looking stopped" bug) — cool it down too and
            // drop to the no-target path, which regroups toward the player so the fleet drifts to where the
            // action / the human is. Once a cooldown lapses, the bot re-engages.
            _cooldownUntil[cur.ViewId] = _tick + CooldownTicks;
            _targetViewId = 0; _actsOnCurrent = 0;
            return null;
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
        if (IsEnemy(target)) { actions.Add(DamageRpc(target.ViewId, 25f)); return $"attack {target.Kind}"; }
        if (IsHackNode(target)) { actions.Add(HackRpc(target.ViewId)); return $"hack {target.Kind}"; }
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
