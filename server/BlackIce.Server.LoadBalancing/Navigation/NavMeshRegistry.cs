using System.Collections.Concurrent;
using BlackIce.Server.Core.Navigation;

namespace BlackIce.Server.LoadBalancing.Navigation;

/// <summary>
/// Loads and caches the server's walkable-surface navmeshes by map name, one per name, lazily on first
/// request. Mirrors <see cref="Authority.RoomWorldStateRegistry"/>: a process-wide DI singleton that the
/// world-aware playerbots consult so they path on the real map instead of guessing coordinates.
///
/// <para><b>Graceful absence is the whole point.</b> A contributor without an extracted map has no
/// <c>maps/&lt;name&gt;.navmesh</c> file; <see cref="For"/> returns null and the bots fall back to exactly
/// today's player-anchor behavior. Only a present-but-corrupt file throws (a real misconfiguration the
/// operator should see), via <see cref="NavMeshFile.LoadOrNull"/>.</para>
///
/// <para>The <c>maps/</c> directory is resolved relative to <see cref="System.AppContext.BaseDirectory"/>
/// (next to the running server), overridable with an absolute path or the <c>BLACKICE_MAPS_DIR</c>
/// environment variable so an operator can point at a maps folder elsewhere. The extracted artifacts are
/// game-derived and never committed (clean-room — <c>maps/</c> is gitignored).</para>
///
/// <para>Caching is by map name and never re-reads: the same <see cref="NavMesh"/> instance (or the same
/// null) is returned for every later call, so a missing file is not stat'd on every bot tick. The backing
/// map is concurrent as defense-in-depth, matching the rest of the bot/authority layer.</para>
/// </summary>
public sealed class NavMeshRegistry
{
    private readonly string _mapsDir;
    // Cache the loaded mesh AND the "loaded nothing" result, so a missing map is resolved once, not per tick.
    private readonly ConcurrentDictionary<string, NavMesh?> _byName = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Uses the <c>BLACKICE_MAPS_DIR</c> override if set, else a <c>maps/</c> folder next to the
    /// running server (<see cref="System.AppContext.BaseDirectory"/>).</summary>
    public NavMeshRegistry()
        : this(System.Environment.GetEnvironmentVariable("BLACKICE_MAPS_DIR")) { }

    /// <param name="mapsDir">The directory holding <c>&lt;name&gt;.navmesh</c> artifacts. Null/empty → the
    /// default <c>maps/</c> next to the server binary. A relative path is resolved against the binary dir.</param>
    public NavMeshRegistry(string? mapsDir)
    {
        if (string.IsNullOrWhiteSpace(mapsDir))
            _mapsDir = Path.Combine(System.AppContext.BaseDirectory, "maps");
        else if (Path.IsPathRooted(mapsDir))
            _mapsDir = mapsDir;
        else
            _mapsDir = Path.Combine(System.AppContext.BaseDirectory, mapsDir);
    }

    /// <summary>The directory navmeshes are loaded from (resolved at construction).</summary>
    public string MapsDirectory => _mapsDir;

    /// <summary>
    /// The cached navmesh for <paramref name="mapName"/> (e.g. "level13"), loading <c>maps/&lt;name&gt;.navmesh</c>
    /// on first use, or null if the name is empty or the file is absent. Throws only on a corrupt file.
    /// The result — mesh or null — is cached, so later calls never re-read.
    /// </summary>
    public NavMesh? For(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName)) return null;
        return _byName.GetOrAdd(mapName, name => NavMeshFile.LoadOrNull(Path.Combine(_mapsDir, name + ".navmesh")));
    }
}
