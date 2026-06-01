using System.IO;
using BlackIce.Server.Core.Navigation;
using BlackIce.Server.LoadBalancing.Authority;
using BlackIce.Server.LoadBalancing.Navigation;
using Xunit;

namespace BlackIce.Server.Tests.Navigation;

/// <summary>
/// The map auto-selector: with no client-sent map id, the server infers which extracted map a room is playing
/// from the live player positions. Tested against two spatially-disjoint synthetic maps written to a temp
/// maps dir — the selector must converge on the map whose surface is actually under the player, and stay null
/// when no map matches (procedural/unknown level → graceful fallback to player-anchored movement).
/// </summary>
public class MapSelectorTests
{
    private const int MinSamples = 20;

    /// <summary>A flat square patch centered at (cx,cz) at height y, half-extent <paramref name="half"/>.</summary>
    private static NavMesh Patch(float cx, float cz, float y = 0f, float half = 15f)
    {
        float[] verts =
        {
            cx - half, y, cz - half,   // 0
            cx + half, y, cz - half,   // 1
            cx - half, y, cz + half,   // 2
            cx + half, y, cz + half,   // 3
        };
        int[] tris = { 0, 1, 2, 1, 3, 2 };
        return new NavMesh(verts, tris);
    }

    private static string WriteMaps(params (string name, NavMesh mesh)[] maps)
    {
        var dir = Path.Combine(Path.GetTempPath(), "blackice-mapsel-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        foreach (var (name, mesh) in maps)
        {
            using var fs = File.Create(Path.Combine(dir, name + ".navmesh"));
            NavMeshFile.Write(fs, mesh);
        }
        return dir;
    }

    private static void ObservePlayerAt(MapSelector sel, string room, float x, float y, float z, int times)
    {
        var world = new RoomWorldState();
        world.ObserveSpawn(2001, "Player", x, y, z);
        for (int i = 0; i < times; i++) sel.Observe(room, world);
    }

    [Fact]
    public void Converges_on_the_map_whose_surface_is_under_the_player()
    {
        // mapNear covers the origin; mapFar is 400u away. A player standing at the origin must resolve to mapNear.
        var dir = WriteMaps(("mapNear", Patch(0, 0)), ("mapFar", Patch(0, -400)));
        var sel = new MapSelector(new NavMeshRegistry(dir), MinSamples);
        Assert.Equal(2, sel.CandidateCount);

        Assert.Null(sel.Resolve("room"));               // undecided before enough samples
        ObservePlayerAt(sel, "room", 0, 0, 0, MinSamples + 5);

        Assert.Equal("mapNear", sel.ChosenMap("room"));
        Assert.NotNull(sel.Resolve("room"));
    }

    [Fact]
    public void No_matching_map_leaves_the_room_unresolved()
    {
        // Both maps are far from where the player actually is → nothing matches (the procedural/unknown-level
        // case). Resolve stays null so bots fall back to player-anchored movement rather than a wrong map.
        var dir = WriteMaps(("mapA", Patch(0, -400)), ("mapB", Patch(0, 400)));
        var sel = new MapSelector(new NavMeshRegistry(dir), MinSamples);

        ObservePlayerAt(sel, "room", 0, 0, 0, MinSamples + 5);   // player at origin; neither map covers it

        Assert.Null(sel.ChosenMap("room"));
        Assert.Null(sel.Resolve("room"));
    }

    [Fact]
    public void Distinct_rooms_resolve_independently()
    {
        var dir = WriteMaps(("mapNear", Patch(0, 0)), ("mapFar", Patch(0, -400)));
        var sel = new MapSelector(new NavMeshRegistry(dir), MinSamples);

        ObservePlayerAt(sel, "roomA", 0, 0, 0, MinSamples + 5);          // on mapNear
        ObservePlayerAt(sel, "roomB", 0, 0, -400, MinSamples + 5);       // on mapFar

        Assert.Equal("mapNear", sel.ChosenMap("roomA"));
        Assert.Equal("mapFar", sel.ChosenMap("roomB"));
    }

    [Fact]
    public void No_candidate_maps_is_a_clean_no_op()
    {
        var sel = new MapSelector(new NavMeshRegistry(WriteMaps()), MinSamples);   // empty dir
        Assert.Equal(0, sel.CandidateCount);
        ObservePlayerAt(sel, "room", 0, 0, 0, MinSamples + 5);
        Assert.Null(sel.Resolve("room"));
    }
}
