using BlackIce.Server.Core.Navigation;
using BlackIce.Tools.MapExtractor;

// MapExtractor — offline, clean-room. Reads a Unity 2020.3 serialized scene (the game's Black Ice_Data/levelN)
// and writes a BNAV *.navmesh artifact the server can load. The TOOL is original code and is committed; its
// OUTPUT is game-derived and must never be committed — OutputPathGuard refuses to write into the git tree.

return Run(args);

static int Run(string[] args)
{
    var parsed = CliArgs.Parse(args);
    if (parsed is null)
    {
        PrintUsage();
        return 2; // non-zero, but no crash, for "no/invalid args".
    }

    // Clean-room gate: never let the artifact land in a tracked path.
    string? repoRoot = OutputPathGuard.FindRepoRoot(Directory.GetCurrentDirectory());
    if (!OutputPathGuard.IsAllowed(parsed.OutputPath, repoRoot, out string reason))
    {
        Console.Error.WriteLine($"error: {reason}");
        return 3;
    }

    try
    {
        using var extractor = new SceneNavMeshExtractor();
        Console.Error.WriteLine($"Reading scene: {parsed.ScenePath}  (source: {parsed.Source})");
        ExtractResult result = extractor.Extract(parsed.ScenePath, parsed.Source, parsed.ClassDataPath);

        string outPath = Path.GetFullPath(parsed.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using (var fs = File.Create(outPath))
            NavMeshFile.Write(fs, result.Mesh);

        Console.Error.WriteLine(
            $"Wrote {outPath}\n" +
            $"  map name : {parsed.MapName ?? Path.GetFileNameWithoutExtension(parsed.ScenePath)}\n" +
            $"  source   : {result.Source} ({result.SourceObjectCount} source object(s))\n" +
            $"  vertices : {result.Mesh.VertexCount}\n" +
            $"  triangles: {result.Mesh.TriangleCount}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
}

static void PrintUsage()
{
    Console.Error.WriteLine(
        "MapExtractor — extract a Unity scene's baked navmesh into a BNAV .navmesh artifact (offline, clean-room).\n" +
        "\n" +
        "Usage:\n" +
        "  dotnet run --project tools/MapExtractor -- <scene-file> <out.navmesh> [options]\n" +
        "\n" +
        "Arguments:\n" +
        "  <scene-file>   Path to a Unity 2020.3 serialized scene (e.g. \"Black Ice_Data/level13\").\n" +
        "  <out.navmesh>  Output artifact path. MUST be outside the repo or under the gitignored maps/ dir.\n" +
        "\n" +
        "Options:\n" +
        "  --map-name <X>      Logical map name recorded in tool output (default: scene file name).\n" +
        "  --source <S>        Geometry source: 'navmesh' (default, baked NavMeshData) or 'colliders'\n" +
        "                      (MeshCollider/MeshFilter fallback).\n" +
        "  --classdata <path>  Optional classdata.tpk for scenes saved without an embedded type tree.\n" +
        "\n" +
        "Clean-room: the .navmesh output is game-derived and must never be committed; the tool refuses to\n" +
        "write it into the git tree (write under maps/ or to an absolute path outside the repo).");
}

/// <summary>Parsed command-line arguments, or null when usage should be shown.</summary>
internal sealed record CliArgs(
    string ScenePath, string OutputPath, string? MapName, ExtractSource Source, string? ClassDataPath)
{
    public static CliArgs? Parse(string[] args)
    {
        var positional = new List<string>();
        string? mapName = null, classData = null;
        var source = ExtractSource.NavMesh;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    return null;
                case "--map-name":
                    if (++i >= args.Length) return null;
                    mapName = args[i];
                    break;
                case "--source":
                    if (++i >= args.Length) return null;
                    source = args[i].ToLowerInvariant() switch
                    {
                        "navmesh" => ExtractSource.NavMesh,
                        "colliders" or "collider" => ExtractSource.Colliders,
                        _ => (ExtractSource)(-1)
                    };
                    if ((int)source == -1) return null;
                    break;
                case "--classdata":
                    if (++i >= args.Length) return null;
                    classData = args[i];
                    break;
                default:
                    if (args[i].StartsWith('-')) return null; // unknown flag
                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count != 2) return null;
        return new CliArgs(positional[0], positional[1], mapName, source, classData);
    }
}
