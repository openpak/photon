using System.Collections.Concurrent;
using System.Text;
using BlackIce.Server.Core;
using BlackIce.Server.Core.Navigation;

namespace BlackIce.Server.LoadBalancing.Navigation;

/// <summary>
/// Per-room <see cref="WalkableMap"/>s, loaded from and saved to the maps directory, so the walkable model
/// learned from real players' movement accumulates across sessions. A room's map is created on first access
/// (loading <c>walkable-&lt;slug&gt;.bwlk</c> if present, else empty). The artifacts are runtime-derived and
/// gitignored (<c>*.bwlk</c>), alongside the extracted navmeshes.
/// </summary>
public sealed class WalkableMapRegistry
{
    private readonly string _dir;
    private readonly float _cellSize;
    private readonly ConcurrentDictionary<string, WalkableMap> _byRoom = new(System.StringComparer.OrdinalIgnoreCase);

    public WalkableMapRegistry(string? mapsDir = null, float cellSize = 3f)
    {
        if (string.IsNullOrWhiteSpace(mapsDir))
            _dir = Path.Combine(System.AppContext.BaseDirectory, "maps");
        else if (Path.IsPathRooted(mapsDir))
            _dir = mapsDir;
        else
            _dir = Path.Combine(System.AppContext.BaseDirectory, mapsDir);
        _cellSize = cellSize;
    }

    public string MapsDirectory => _dir;

    /// <summary>The walkable map for <paramref name="room"/>, loaded from disk on first use (or a fresh empty
    /// one). Never null — recording into a new room just starts a new map.</summary>
    public WalkableMap For(string room) => _byRoom.GetOrAdd(room, LoadOrNew);

    private WalkableMap LoadOrNew(string room)
    {
        var path = PathFor(room);
        try
        {
            if (File.Exists(path))
            {
                using var fs = File.OpenRead(path);
                var map = WalkableMap.Load(fs);
                Log.Info("Walkmap", $"loaded {map.Count} walkable cell(s) for \"{room}\" from {Path.GetFileName(path)}");
                return map;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Log.Warn("Walkmap", $"could not load walkable map for \"{room}\" ({ex.GetType().Name}); starting fresh");
        }
        return new WalkableMap(_cellSize);
    }

    /// <summary>Persists the room's walkable map. Best-effort: a write failure is logged, never thrown (a
    /// mapping problem must not take the server down).</summary>
    public bool Save(string room)
    {
        if (!_byRoom.TryGetValue(room, out var map)) return false;
        try
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(room);
            using (var fs = File.Create(path)) map.Save(fs);
            Log.Info("Walkmap", $"saved {map.Count} walkable cell(s) for \"{room}\" to {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Walkmap", $"could not save walkable map for \"{room}\" ({ex.GetType().Name})");
            return false;
        }
    }

    /// <summary>Saves every loaded room map (e.g. on shutdown).</summary>
    public void SaveAll() { foreach (var room in _byRoom.Keys) Save(room); }

    private string PathFor(string room) => Path.Combine(_dir, $"walkable-{Slug(room)}.bwlk");

    /// <summary>A filesystem-safe slug for a room name (the live name carries spaces and a Unicode em-dash).</summary>
    private static string Slug(string room)
    {
        var sb = new StringBuilder(room.Length);
        foreach (var ch in room)
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        var s = sb.ToString().Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Length == 0 ? "room" : s;
    }
}
