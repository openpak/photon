using System.Collections.Concurrent;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Per-room authoritative <em>shadow</em> of entity existence — the foundation for zero-trust outcome
/// validation (Phase 3b) and lag-comp rollback (Phase 3c). It is deliberately <b>best-effort</b>: it only
/// knows entities the server has observed being instantiated (PUN event 202) and not yet destroyed (PUN
/// event 204). Anything it has never seen is "unknown", which callers MUST treat as fail-open (can't
/// judge, never punish) — the server does not observe every entity (a player's own local objects and
/// pre-existing world geometry are legitimately absent).
///
/// <para>This is the clean-room <em>shadow</em> half of the spec's hybrid model: it tracks existence and
/// alive/dead from facts on the wire, without reimplementing the game's HP/loot/XP formulas. Richer
/// per-outcome recompute plugs in later via <see cref="IOutcomeRule"/> without changing this type.</para>
///
/// <para>Designed to run on the single listener thread; the backing map is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> as defense-in-depth, matching the EnetPeer / authority
/// hardening elsewhere.</para>
/// </summary>
public sealed class RoomWorldState
{
    /// <summary>One tracked entity. <see cref="Alive"/> flips false on an observed destroy.</summary>
    public sealed class Entity
    {
        public int ViewId { get; }
        public bool Alive { get; internal set; } = true;
        public Entity(int viewId) => ViewId = viewId;
    }

    private readonly ConcurrentDictionary<int, Entity> _entities = new();
    private readonly WorldSnapshotHistory _history;

    public RoomWorldState(int positionHistoryCapacity = 64) => _history = new WorldSnapshotHistory(positionHistoryCapacity);

    /// <summary>The per-entity position history used for lag-comp rewind (Phase 3c).</summary>
    public WorldSnapshotHistory History => _history;

    /// <summary>
    /// Record an ACCEPTED position sample for lag-comp rewind (apply-after-validate — callers pass only
    /// positions the movement validator accepted, never a snap-corrected teleport target).
    /// </summary>
    public void RecordPosition(int viewId, float x, float y, float z, DateTime t) =>
        _history.Record(viewId, x, y, z, t);

    /// <summary>Rewind: where was <paramref name="viewId"/> at time <paramref name="t"/> (interpolated/clamped)?</summary>
    public bool TryPositionAt(int viewId, DateTime t, out (float x, float y, float z) pos) =>
        _history.TryPositionAt(viewId, t, out pos);

    /// <summary>Record that an entity now exists and is alive (PUN 202). A recycled viewId is revived.</summary>
    public void ObserveSpawn(int viewId) =>
        _entities.AddOrUpdate(viewId, id => new Entity(id), (_, e) => { e.Alive = true; return e; });

    /// <summary>Record that an entity was destroyed (PUN 204): still known, but no longer alive.</summary>
    public void ObserveDestroy(int viewId) =>
        _entities.AddOrUpdate(viewId, id => new Entity(id) { Alive = false }, (_, e) => { e.Alive = false; return e; });

    /// <summary>True if the shadow has ever observed <paramref name="viewId"/> (alive or dead).</summary>
    public bool Knows(int viewId) => _entities.ContainsKey(viewId);

    /// <summary>
    /// Tri-state liveness: <c>true</c> alive, <c>false</c> known-destroyed, <c>null</c> never observed.
    /// The <c>null</c> case is the fail-open signal — the shadow cannot vouch for an entity it never saw.
    /// </summary>
    public bool? IsAlive(int viewId) => _entities.TryGetValue(viewId, out var e) ? e.Alive : null;

    /// <summary>The tracked entity, or null if never observed.</summary>
    public Entity? Get(int viewId) => _entities.TryGetValue(viewId, out var e) ? e : null;

    /// <summary>Number of entities the shadow is tracking (alive or dead).</summary>
    public int Count => _entities.Count;
}
