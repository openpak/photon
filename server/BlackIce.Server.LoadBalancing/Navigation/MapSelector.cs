using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BlackIce.Server.Core;
using BlackIce.Server.Core.Navigation;
using BlackIce.Server.LoadBalancing.Authority;

namespace BlackIce.Server.LoadBalancing.Navigation;

/// <summary>
/// Auto-detects which extracted map a room is actually playing, from the live player positions — the
/// clean-room stand-in for a client-sent map id (Black Ice sends none over Photon). The server holds every
/// extracted navmesh; for each room it scores the candidates by how many observed player positions land on
/// that map's real surface (not just its bounding box), and the highest-scoring map is the room's map.
///
/// <para><b>Why a trajectory, not a point.</b> Map bounding boxes overlap (several levels straddle the
/// origin), so a single position is ambiguous. A path of positions is not: only the level whose walkable
/// surface is genuinely under the player accrues hits across the whole trajectory. The disjoint geometry of
/// the real maps makes the score converge on one map within a few seconds of movement.</para>
///
/// <para><b>Cheap once settled.</b> Scoring runs a bounded nearest-point test only on candidates whose bbox
/// contains the sample (a handful), and only while a room is still learning or its chosen map stops covering
/// the players (a map change). A settled room costs one bbox check per tick.</para>
/// </summary>
public sealed class MapSelector
{
    /// <summary>A player position is "on" a candidate map when the nearest surface point is within this XZ
    /// distance — matches the bot's own coverage gate, so a chosen map is one the bots can actually path on.</summary>
    private const float CoverageRadius = 25f;
    private const float CoverageRadiusSq = CoverageRadius * CoverageRadius;

    private readonly IReadOnlyList<(string name, NavMesh mesh)> _candidates;
    private readonly int _minSamples;

    private sealed class RoomScore
    {
        public readonly Dictionary<string, int> Hits = new(StringComparer.OrdinalIgnoreCase);
        public int Samples;
        public string? Chosen;
    }
    private readonly ConcurrentDictionary<string, RoomScore> _rooms = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="registry">Loads every extracted navmesh (the candidate maps).</param>
    /// <param name="minSamples">Player position samples a room must accumulate before a map is committed —
    /// guards against a lucky early hit while the player is mid-air/loading.</param>
    public MapSelector(NavMeshRegistry registry, int minSamples = 20)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _candidates = registry.AllMaps().Select(kv => (kv.Key, kv.Value)).ToList();
        _minSamples = Math.Max(1, minSamples);
        if (_candidates.Count > 0)
            Log.Info("Maps", $"map auto-select armed with {_candidates.Count} candidate map(s)");
    }

    /// <summary>The number of candidate maps loaded.</summary>
    public int CandidateCount => _candidates.Count;

    /// <summary>Feed a room's current world-state: every known-position player avatar is one sample scored
    /// against the candidate maps. Call once per room per tick (not per bot).</summary>
    public void Observe(string room, RoomWorldState world)
    {
        if (_candidates.Count == 0) return;
        var rs = _rooms.GetOrAdd(room, _ => new RoomScore());

        foreach (var e in world.Alive())
        {
            if (!e.HasPosition || !string.Equals(e.Kind, "Player", StringComparison.OrdinalIgnoreCase)) continue;

            // Fast path: once a map is chosen and still covers this player, just keep counting it — no need to
            // re-test all candidates every tick.
            if (rs.Chosen is { } chosen && Covers(chosen, e.X, e.Z))
            {
                rs.Samples++;
                rs.Hits[chosen] = rs.Hits.GetValueOrDefault(chosen) + 1;
                continue;
            }

            rs.Samples++;
            foreach (var (name, mesh) in _candidates)
            {
                if (!mesh.ContainsXZ(e.X, e.Z, CoverageRadius)) continue;       // cheap bbox pre-filter
                if (!OnSurface(mesh, e.X, e.Z)) continue;                       // precise: real surface under the player
                rs.Hits[name] = rs.Hits.GetValueOrDefault(name) + 1;
            }
        }

        Recommit(room, rs);
    }

    /// <summary>The auto-detected navmesh for <paramref name="room"/>, or null until enough samples have
    /// converged on a map (room still learning, no players seen, or no extracted map matches — e.g. a
    /// procedural/unknown level). Null cleanly falls the bots back to player-anchored movement.</summary>
    public NavMesh? Resolve(string room)
    {
        if (!_rooms.TryGetValue(room, out var rs) || rs.Chosen is null) return null;
        foreach (var (name, mesh) in _candidates)
            if (string.Equals(name, rs.Chosen, StringComparison.OrdinalIgnoreCase)) return mesh;
        return null;
    }

    /// <summary>The chosen map name for a room (for diagnostics/console), or null if undecided.</summary>
    public string? ChosenMap(string room) => _rooms.TryGetValue(room, out var rs) ? rs.Chosen : null;

    private void Recommit(string room, RoomScore rs)
    {
        if (rs.Samples < _minSamples || rs.Hits.Count == 0) return;
        var best = rs.Hits.Aggregate((a, b) => b.Value > a.Value ? b : a);
        // Require the leader to actually dominate (a clear majority of samples) so noise doesn't flip the pick.
        if (best.Value * 2 < rs.Samples) return;
        if (!string.Equals(best.Key, rs.Chosen, StringComparison.OrdinalIgnoreCase))
        {
            var prev = rs.Chosen;
            rs.Chosen = best.Key;
            Log.Info("Maps", $"room \"{room}\" map = {best.Key} ({best.Value}/{rs.Samples} player samples on it)" +
                             (prev is null ? "" : $" — changed from {prev}"));
        }
    }

    private bool Covers(string mapName, float x, float z)
    {
        foreach (var (name, mesh) in _candidates)
            if (string.Equals(name, mapName, StringComparison.OrdinalIgnoreCase))
                return mesh.ContainsXZ(x, z, CoverageRadius) && OnSurface(mesh, x, z);
        return false;
    }

    private static bool OnSurface(NavMesh mesh, float x, float z)
    {
        if (!mesh.NearestPoint(x, z, out var p, out _)) return false;
        float dx = p.x - x, dz = p.z - z;
        return dx * dx + dz * dz <= CoverageRadiusSq;
    }
}
