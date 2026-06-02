using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BlackIce.Server.Core;
using BlackIce.Server.Core.Navigation;
using BlackIce.Server.LoadBalancing.Authority;

namespace BlackIce.Server.LoadBalancing.Navigation;

/// <summary>
/// Auto-detects which extracted map a room is playing, and the vertical offset between the baked navmesh and
/// the live world — both inferred from live player positions, since Black Ice sends no map id over Photon.
///
/// <para><b>Why an offset, not just a match.</b> The Co-op map is level12, but its baked navmesh sits ~63u
/// below the live floor (a uniform coordinate shift applied when the game places the level at runtime;
/// confirmed by cross-checking learned walkable cells against level12 geometry). So a candidate is scored by
/// XZ coverage of the player's trajectory, and for the covered candidates we measure the per-sample Y offset
/// (playerY − meshSurfaceY). A candidate is committed only when that offset is <b>consistent</b> (low std):
/// a real level shifted by a uniform amount has tight offsets; a coincidental XZ overlap with a different
/// level has scattered ones. The committed map's median offset is then applied to rebase its navmesh.</para>
///
/// <para>Map bounding boxes overlap near the scene origin, so a single point is ambiguous — the trajectory +
/// offset-consistency test disambiguates. Null until enough samples converge (then bots use player-anchored
/// movement), so an unknown/procedural level degrades gracefully.</para>
/// </summary>
public sealed class MapSelector
{
    /// <summary>A player is "over" a candidate when the nearest surface point is within this XZ distance.</summary>
    private const float CoverageRadius = 25f;
    private const float CoverageRadiusSq = CoverageRadius * CoverageRadius;

    /// <summary>Max std of a candidate's measured Y offsets to accept it — a uniformly-shifted real level is
    /// tight; a coincidental overlap with an unrelated level is scattered. Generous enough for multi-floor
    /// sampling noise, tight enough to reject a wrong level.</summary>
    private const float OffsetConsistencyStd = 25f;
    private const int MaxOffsetSamples = 256;

    private readonly IReadOnlyList<(string name, NavMesh mesh)> _candidates;
    private readonly int _minSamples;

    private sealed class RoomScore
    {
        public readonly Dictionary<string, int> Hits = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<float>> Offsets = new(StringComparer.OrdinalIgnoreCase);
        public int Samples;
        public string? Chosen;
        public float ChosenYOffset;
    }
    private readonly ConcurrentDictionary<string, RoomScore> _rooms = new(StringComparer.OrdinalIgnoreCase);

    public MapSelector(NavMeshRegistry registry, int minSamples = 20)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _candidates = registry.AllMaps().Select(kv => (kv.Key, kv.Value)).ToList();
        _minSamples = Math.Max(1, minSamples);
        if (_candidates.Count > 0)
            Log.Info("Maps", $"map auto-select armed with {_candidates.Count} candidate map(s)");
    }

    public int CandidateCount => _candidates.Count;

    /// <summary>Feed a room's world-state: each known-position player avatar is one sample, scored (XZ coverage
    /// + Y offset) against the candidate maps. Call once per room per tick (not per bot).</summary>
    public void Observe(string room, RoomWorldState world)
    {
        if (_candidates.Count == 0) return;
        var rs = _rooms.GetOrAdd(room, _ => new RoomScore());

        foreach (var e in world.Alive())
        {
            if (!e.HasPosition || !string.Equals(e.Kind, "Player", StringComparison.OrdinalIgnoreCase)) continue;
            rs.Samples++;
            foreach (var (name, mesh) in _candidates)
            {
                if (!mesh.ContainsXZ(e.X, e.Z, CoverageRadius)) continue;       // cheap bbox pre-filter
                if (!mesh.NearestPoint(e.X, e.Z, out var p, out _)) continue;
                float dx = p.x - e.X, dz = p.z - e.Z;
                if (dx * dx + dz * dz > CoverageRadiusSq) continue;             // genuinely XZ-covered
                rs.Hits[name] = rs.Hits.GetValueOrDefault(name) + 1;
                var list = rs.Offsets.TryGetValue(name, out var l) ? l : (rs.Offsets[name] = new List<float>());
                if (list.Count < MaxOffsetSamples) list.Add(e.Y - p.y);        // playerY − meshSurfaceY
            }
        }

        Recommit(room, rs);
    }

    /// <summary>The auto-detected navmesh for <paramref name="room"/>, or null until a map converges.</summary>
    public NavMesh? Resolve(string room)
    {
        if (!_rooms.TryGetValue(room, out var rs) || rs.Chosen is null) return null;
        foreach (var (name, mesh) in _candidates)
            if (string.Equals(name, rs.Chosen, StringComparison.OrdinalIgnoreCase)) return mesh;
        return null;
    }

    /// <summary>The Y offset to add to the chosen map's surface so it lines up with the live world (0 if none).</summary>
    public float ResolveYOffset(string room) => _rooms.TryGetValue(room, out var rs) ? rs.ChosenYOffset : 0f;

    /// <summary>The chosen map name for a room (diagnostics/console), or null if undecided.</summary>
    public string? ChosenMap(string room) => _rooms.TryGetValue(room, out var rs) ? rs.Chosen : null;

    private void Recommit(string room, RoomScore rs)
    {
        if (rs.Samples < _minSamples || rs.Hits.Count == 0) return;

        string? bestName = null; int bestHits = 0; float bestOffset = 0f;
        foreach (var (name, hits) in rs.Hits)
        {
            if (hits * 2 < rs.Samples) continue;                                   // must cover a majority of samples
            if (!rs.Offsets.TryGetValue(name, out var offs) || offs.Count < 3) continue;
            var (median, std) = MedianStd(offs);
            if (std > OffsetConsistencyStd) continue;                              // scattered offset → wrong level
            if (hits > bestHits) { bestHits = hits; bestName = name; bestOffset = median; }
        }
        if (bestName is null) return;

        bool changed = !string.Equals(bestName, rs.Chosen, StringComparison.OrdinalIgnoreCase);
        bool recalibrated = !changed && Math.Abs(bestOffset - rs.ChosenYOffset) > 5f;
        if (changed || recalibrated)
        {
            var prev = rs.Chosen;
            rs.Chosen = bestName; rs.ChosenYOffset = bestOffset;
            Log.Info("Maps", $"room \"{room}\" map = {bestName} (Y offset {bestOffset:F0}u, {bestHits}/{rs.Samples} samples)" +
                             (prev is null ? "" : recalibrated ? " — recalibrated" : $" — changed from {prev}"));
        }
    }

    private static (float median, float std) MedianStd(List<float> values)
    {
        var s = values.ToList();
        s.Sort();
        float median = s[s.Count / 2];
        float mean = 0f; foreach (var x in s) mean += x; mean /= s.Count;
        float var = 0f; foreach (var x in s) { float d = x - mean; var += d * d; } var /= s.Count;
        return (median, MathF.Sqrt(var));
    }
}
