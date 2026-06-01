using AssetsTools.NET;
using AssetsTools.NET.Extra;
using BlackIce.Server.Core.Navigation;

namespace BlackIce.Tools.MapExtractor;

/// <summary>What geometry the tool pulls from the scene.</summary>
public enum ExtractSource
{
    /// <summary>The baked <c>NavMeshData</c> walkable surface (preferred).</summary>
    NavMesh,
    /// <summary>MeshCollider/MeshFilter shared-mesh triangles (documented fallback).</summary>
    Colliders
}

public sealed record ExtractResult(NavMesh Mesh, int SourceObjectCount, ExtractSource Source);

/// <summary>
/// Opens a Unity 2020.3 serialized scene with AssetsTools.NET and produces a server <see cref="NavMesh"/>.
/// Keeps all AssetsTools interaction in one place; the actual triangle decoding lives in the per-source
/// parsers so each can be reasoned about and unit-tested.
///
/// <para><b>Stripped type tree.</b> The game ships a RELEASE Unity build with the serialized type tree
/// removed, so AssetsTools.NET can locate objects by class id but cannot read their fields without an
/// external class database. We supply one via a <c>classdata.tpk</c> class package (auto-discovered, or
/// passed with <c>--classdata</c>): <see cref="AssetsManager.LoadClassPackage(string)"/> then
/// <see cref="AssetsManager.LoadClassDatabaseFromPackage(string)"/> pinned to the file's Unity version.</para>
///
/// <para><b>Where the baked navmesh lives.</b> The <c>levelN</c> scene holds only <c>NavMeshSettings</c>;
/// the actual baked <c>NavMeshData</c> sits in the companion <c>sharedassetsN.assets</c>. We resolve it
/// through the scene's <c>NavMeshSettings.m_NavMeshData</c> PPtr (loading dependencies), and fall back to
/// scanning loaded dependency files for any NavMeshData if the PPtr can't be followed.</para>
/// </summary>
public sealed class SceneNavMeshExtractor : IDisposable
{
    private readonly AssetsManager _am = new();

    /// <summary>
    /// Loads <paramref name="scenePath"/> and extracts a navmesh using the requested source.
    /// </summary>
    /// <param name="classDataPath">Optional path to a <c>classdata.tpk</c> class package. The game's
    /// stripped build needs it to resolve field layouts. If null, a bundled/default copy is auto-discovered
    /// (see <see cref="ResolveClassDataPath"/>).</param>
    public ExtractResult Extract(string scenePath, ExtractSource source, string? classDataPath = null)
    {
        if (!File.Exists(scenePath))
            throw new FileNotFoundException($"scene file not found: {scenePath}", scenePath);

        string? tpk = ResolveClassDataPath(classDataPath, scenePath);
        if (tpk is not null)
            _am.LoadClassPackage(tpk);

        // Load with dependencies so the scene's NavMeshSettings PPtr into sharedassets resolves, and so the
        // collider/mesh PPtrs can be followed across files.
        var scene = _am.LoadAssetsFile(scenePath, loadDeps: true);
        if (scene is null)
            throw new InvalidDataException($"'{scenePath}' is not a readable Unity serialized assets file.");

        // If the file has no embedded type tree (the stripped release build), pin the class database from the
        // package so GetBaseField can resolve fields.
        if (scene.file.Metadata.TypeTreeEnabled == false)
        {
            if (_am.ClassPackage is null)
                throw new InvalidDataException(
                    "this scene has no embedded type tree (stripped release build) and no classdata.tpk was " +
                    "found. Provide one with --classdata <path>, or place classdata.tpk under the tool's " +
                    ".classdata/ folder.");
            _am.LoadClassDatabaseFromPackage(scene.file.Metadata.UnityVersion);
        }

        var sink = new MeshTriangleSet();
        int sourceCount = source switch
        {
            ExtractSource.NavMesh => ExtractNavMesh(scene, sink),
            ExtractSource.Colliders => ColliderMeshParser.ExtractTriangles(_am, scene, sink),
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };

        var verts = sink.Vertices;
        var tris = sink.Triangles;
        if (tris.Length == 0)
            throw new InvalidDataException(
                source == ExtractSource.NavMesh
                    ? "no triangles extracted from NavMeshData. The scene may have no baked navmesh reachable " +
                      "from its NavMeshSettings, or no companion sharedassets was loaded. Try " +
                      "'--source colliders' as a fallback."
                    : "no triangles extracted from MeshCollider/MeshFilter meshes in this scene.");

        // Pass neighbors=null so NavMesh rebuilds edge adjacency from shared (welded) vertices.
        return new ExtractResult(new NavMesh(verts, tris), sourceCount, source);
    }

    /// <summary>
    /// Extracts triangles from the baked NavMeshData reachable from the scene. Prefers the
    /// <c>NavMeshSettings.m_NavMeshData</c> PPtr (the precise, scene-specific asset); falls back to scanning
    /// every loaded dependency file for NavMeshData objects.
    /// </summary>
    private int ExtractNavMesh(AssetsFileInstance scene, MeshTriangleSet sink)
    {
        int objects = 0;

        // 1) Precise: follow the scene's NavMeshSettings -> NavMeshData PPtr.
        foreach (var info in scene.file.GetAssetsOfType(AssetClassID.NavMeshSettings))
        {
            var settings = _am.GetBaseField(scene, info, AssetReadFlags.None);
            var ptr = settings?["m_NavMeshData"];
            if (ptr is null || ptr.IsDummy) continue;

            var ext = _am.GetExtAsset(scene, ptr, onlyGetInfo: false, AssetReadFlags.None);
            if (ext.baseField is null) continue;
            NavMeshDataParser.ExtractTriangles(ext.baseField, sink);
            objects++;
        }
        if (objects > 0) return objects;

        // 2) Fallback: scan all loaded files (scene + dependencies) for NavMeshData objects directly.
        foreach (var file in _am.Files)
        {
            foreach (var info in file.file.GetAssetsOfType(AssetClassID.NavMeshData))
            {
                var nav = _am.GetBaseField(file, info, AssetReadFlags.None);
                if (nav is null) continue;
                NavMeshDataParser.ExtractTriangles(nav, sink);
                objects++;
            }
        }
        return objects;
    }

    /// <summary>
    /// Resolves the classdata.tpk to use: the explicit <paramref name="explicitPath"/> if given and present,
    /// otherwise the first existing default location. Defaults are searched next to the tool (a gitignored
    /// <c>.classdata/classdata.tpk</c>), next to the scene, and via the <c>BLACKICE_CLASSDATA_TPK</c> env var.
    /// The .tpk is third-party tooling data (not game data) and is intentionally not committed.
    /// </summary>
    internal static string? ResolveClassDataPath(string? explicitPath, string scenePath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;

        foreach (string? candidate in EnumerateDefaultClassDataPaths(scenePath))
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;

        return null;
    }

    private static IEnumerable<string?> EnumerateDefaultClassDataPaths(string scenePath)
    {
        yield return Environment.GetEnvironmentVariable("BLACKICE_CLASSDATA_TPK");

        // Next to the running tool (bin/...): walk up to find a .classdata folder shipped beside the project.
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            yield return Path.Combine(dir, ".classdata", "classdata.tpk");
            yield return Path.Combine(dir, "classdata.tpk");
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        // Next to the scene file (operator dropped a tpk beside the game data).
        string? sceneDir = Path.GetDirectoryName(Path.GetFullPath(scenePath));
        if (sceneDir is not null)
            yield return Path.Combine(sceneDir, "classdata.tpk");
    }

    public void Dispose() => _am.UnloadAll(unloadClassData: true);
}
