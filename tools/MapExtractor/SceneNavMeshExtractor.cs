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
/// parsers so each can be reasoned about (and the NavMeshData decode's GAP is contained).
/// </summary>
public sealed class SceneNavMeshExtractor : IDisposable
{
    private readonly AssetsManager _am = new();

    /// <summary>
    /// Loads <paramref name="scenePath"/> and extracts a navmesh using the requested source.
    /// </summary>
    /// <param name="classDataPath">Optional path to a <c>classdata.tpk</c> class package. Unity scene files
    /// normally embed a type tree (so this is unnecessary), but a stripped build needs the package to resolve
    /// field layouts; supplying it makes the tool robust to either case.</param>
    public ExtractResult Extract(string scenePath, ExtractSource source, string? classDataPath = null)
    {
        if (!File.Exists(scenePath))
            throw new FileNotFoundException($"scene file not found: {scenePath}", scenePath);

        // A class package is only needed when the file lacks a type tree; load it if the operator points us at
        // one, so GetBaseField can resolve fields either way.
        if (!string.IsNullOrWhiteSpace(classDataPath) && File.Exists(classDataPath))
            _am.LoadClassPackage(classDataPath);

        var scene = _am.LoadAssetsFile(scenePath, loadDeps: true);
        if (scene is null)
            throw new InvalidDataException($"'{scenePath}' is not a readable Unity serialized assets file.");

        // If the file has no embedded type tree and we have a class package, pin the matching class database.
        if (_am.ClassPackage is not null && scene.file.Metadata.TypeTreeEnabled == false)
            _am.LoadClassDatabaseFromPackage(scene.file.Metadata.UnityVersion);

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
                    ? "no triangles extracted from NavMeshData. The scene may have no baked navmesh, or the " +
                      "Detour tile layout differs from the reference decode (see the GAP note in " +
                      "NavMeshDataParser). Try '--source colliders' as a fallback."
                    : "no triangles extracted from MeshCollider/MeshFilter meshes in this scene.");

        // Pass neighbors=null so NavMesh rebuilds edge adjacency from shared (welded) vertices.
        return new ExtractResult(new NavMesh(verts, tris), sourceCount, source);
    }

    private int ExtractNavMesh(AssetsFileInstance scene, MeshTriangleSet sink)
    {
        int objects = 0;
        foreach (var info in scene.file.GetAssetsOfType(AssetClassID.NavMeshData))
        {
            var nav = _am.GetBaseField(scene, info);
            if (nav is null) continue;
            NavMeshDataParser.ExtractTriangles(nav, sink);
            objects++;
        }
        return objects;
    }

    public void Dispose() => _am.UnloadAll(unloadClassData: true);
}
