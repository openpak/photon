using System.Collections.Concurrent;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Per-room authoritative <em>shadow</em> of entity existence — the foundation for zero-trust outcome
/// validation (Phase 3b), lag-comp rollback (Phase 3c), and world-aware playerbots. It is deliberately
/// <b>best-effort</b>: it only knows entities the server has observed being instantiated (PUN event 202)
/// and not yet destroyed (PUN event 204). Anything it has never seen is "unknown", which authority callers
/// MUST treat as fail-open (can't judge, never punish) — the server does not observe every entity (a
/// player's own local objects and pre-existing world geometry are legitimately absent).
///
/// <para>Beyond existence/alive, it records each entity's <see cref="Entity.Kind"/> (the instantiated prefab
/// name) and last-known position, fed from the 202 payload and ongoing 201 serialize stream.
/// <see cref="Entity.HasPosition"/> distinguishes a <em>known</em> position from the default — a 202 may
/// arrive without a position (some prefabs stream it later via 201), and a bot must NOT treat the (0,0,0)
/// default as a real target location, or every bot swarms the world origin.</para>
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
        /// <summary>The instantiated prefab name (e.g. "SpiderEnemy", "Link", "NetworkLootCube"), or null
        /// if the spawn was observed without a resolvable name.</summary>
        public string? Kind { get; internal set; }
        /// <summary>Last-known position, from the 202 spawn payload and any subsequent 201 serialize.</summary>
        public float X { get; internal set; }
        public float Y { get; internal set; }
        public float Z { get; internal set; }
        /// <summary>True once a REAL position has been observed (202 key-1 Vec3 or a 201 update). While false,
        /// X/Y/Z are just the default and must not be used as a navigation target.</summary>
        public bool HasPosition { get; internal set; }
        public Entity(int viewId) => ViewId = viewId;
    }

    private readonly ConcurrentDictionary<int, Entity> _entities = new();
    private readonly WorldSnapshotHistory _history;

    public RoomWorldState(int positionHistoryCapacity = 64) => _history = new WorldSnapshotHistory(positionHistoryCapacity);

    /// <summary>The per-entity position history used for lag-comp rewind (Phase 3c).</summary>
    public WorldSnapshotHistory History => _history;

    /// <summary>
    /// Record an ACCEPTED position sample for lag-comp rewind (apply-after-validate — callers pass only
    /// positions the movement validator accepted, never a snap-corrected teleport target). Also refreshes
    /// the entity's last-known position (marking it known) used for bot navigation.
    /// </summary>
    public void RecordPosition(int viewId, float x, float y, float z, DateTime t)
    {
        _history.Record(viewId, x, y, z, t);
        if (_entities.TryGetValue(viewId, out var e)) { e.X = x; e.Y = y; e.Z = z; e.HasPosition = true; }
    }

    /// <summary>Rewind: where was <paramref name="viewId"/> at time <paramref name="t"/> (interpolated/clamped)?</summary>
    public bool TryPositionAt(int viewId, DateTime t, out (float x, float y, float z) pos) =>
        _history.TryPositionAt(viewId, t, out pos);

    /// <summary>Record that an entity now exists and is alive (PUN 202). A recycled viewId is revived.
    /// Existence-only overload (no payload understanding) — keeps the authority observer's old behavior.</summary>
    public void ObserveSpawn(int viewId) =>
        _entities.AddOrUpdate(viewId, id => new Entity(id), (_, e) => { e.Alive = true; return e; });

    /// <summary>Record a spawn with its prefab kind but NO known position (the 202 carried no Vec3). The
    /// entity is tracked and classifiable, but <see cref="Entity.HasPosition"/> stays false so bots won't
    /// navigate to a phantom (0,0,0). A later 201 (via <see cref="RecordPosition"/>) fills the position in.</summary>
    public void ObserveSpawn(int viewId, string? kind) =>
        _entities.AddOrUpdate(viewId,
            id => new Entity(id) { Kind = kind },
            (_, e) => { e.Alive = true; if (kind is not null) e.Kind = kind; return e; });

    /// <summary>Record a spawn with its prefab kind and a KNOWN world position (from the 202 key-1 Vec3), so
    /// bots can classify and navigate to it. A recycled viewId is revived and its kind/position refreshed.</summary>
    public void ObserveSpawn(int viewId, string? kind, float x, float y, float z) =>
        _entities.AddOrUpdate(viewId,
            id => new Entity(id) { Kind = kind, X = x, Y = y, Z = z, HasPosition = true },
            (_, e) => { e.Alive = true; if (kind is not null) e.Kind = kind; e.X = x; e.Y = y; e.Z = z; e.HasPosition = true; return e; });

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

    /// <summary>Snapshot of all currently-alive tracked entities. Safe to enumerate (a copy).</summary>
    public IReadOnlyList<Entity> Alive()
    {
        var list = new List<Entity>();
        foreach (var e in _entities.Values) if (e.Alive) list.Add(e);
        return list;
    }

    /// <summary>
    /// The nearest ALIVE entity with a KNOWN position (by XZ distance from <paramref name="fromX"/>,
    /// <paramref name="fromZ"/>) matching <paramref name="predicate"/>, or null if none. Entities whose
    /// position has never been observed are skipped — a bot cannot path to an unknown location. Y is ignored
    /// for distance. Ties resolve to the lowest viewId for determinism.
    /// </summary>
    public Entity? Nearest(Func<Entity, bool> predicate, float fromX, float fromZ)
    {
        Entity? best = null;
        double bestDist = double.MaxValue;
        foreach (var e in _entities.Values)
        {
            if (!e.Alive || !e.HasPosition || !predicate(e)) continue;
            double dx = e.X - fromX, dz = e.Z - fromZ;
            double d = dx * dx + dz * dz;
            if (d < bestDist || (d == bestDist && (best is null || e.ViewId < best.ViewId)))
            {
                bestDist = d;
                best = e;
            }
        }
        return best;
    }

    /// <summary>
    /// The Nth-nearest (0-based <paramref name="rank"/>) alive, known-position entity matching the predicate,
    /// or null if fewer than rank+1 exist. Lets a fleet of bots fan out across distinct targets (bot i takes
    /// rank i) instead of all stacking on the single nearest one.
    /// </summary>
    public Entity? NearestRanked(Func<Entity, bool> predicate, float fromX, float fromZ, int rank)
    {
        var matches = new List<Entity>();
        foreach (var e in _entities.Values)
            if (e.Alive && e.HasPosition && predicate(e)) matches.Add(e);
        if (matches.Count == 0) return null;
        matches.Sort((a, b) =>
        {
            double da = Sq(a, fromX, fromZ), db = Sq(b, fromX, fromZ);
            int c = da.CompareTo(db);
            return c != 0 ? c : a.ViewId.CompareTo(b.ViewId);
        });
        return matches[Math.Min(rank, matches.Count - 1)];

        static double Sq(Entity e, float x, float z) { double dx = e.X - x, dz = e.Z - z; return dx * dx + dz * dz; }
    }
}
