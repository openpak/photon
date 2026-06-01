using BlackIce.Server.Core.Navigation;
using Xunit;

namespace BlackIce.Server.Tests.Navigation;

/// <summary>
/// A* pathing over the navmesh: a straight shot within a triangle, a multi-triangle corridor, and the key
/// property — a path must route THROUGH connected triangles (around a gap), never jump across a hole. All
/// synthetic; no game asset.
/// </summary>
public class NavMeshPathTests
{
    /// <summary>A 1-wide strip of N quads along +X (2N triangles), all connected: a corridor from x=0 to x=N.</summary>
    private static NavMesh Strip(int quads)
    {
        var verts = new System.Collections.Generic.List<float>();
        for (int i = 0; i <= quads; i++) { verts.AddRange(new[] { (float)i, 0f, 0f }); verts.AddRange(new[] { (float)i, 0f, 1f }); }
        // vertex index for column i: bottom = 2i, top = 2i+1
        var tris = new System.Collections.Generic.List<int>();
        for (int i = 0; i < quads; i++)
        {
            int b0 = 2 * i, t0 = 2 * i + 1, b1 = 2 * (i + 1), t1 = 2 * (i + 1) + 1;
            tris.AddRange(new[] { b0, b1, t0 });
            tris.AddRange(new[] { b1, t1, t0 });
        }
        return new NavMesh(verts.ToArray(), tris.ToArray());
    }

    [Fact]
    public void Path_within_one_triangle_is_a_direct_segment_to_goal()
    {
        var mesh = Strip(1);
        var path = NavMeshPath.Find(mesh, 0.1f, 0.5f, 0.2f, 0.5f);
        Assert.Single(path);
        Assert.Equal(0.2f, path[0].x, 2);
    }

    [Fact]
    public void Path_traverses_a_long_corridor_end_to_end()
    {
        var mesh = Strip(10);   // x from 0..10
        var path = NavMeshPath.Find(mesh, 0.5f, 0.5f, 9.5f, 0.5f);
        Assert.NotEmpty(path);
        var goal = path[^1];
        Assert.Equal(9.5f, goal.x, 1);                 // reached the far end
        // Waypoints advance monotonically in +X across the corridor (no backtracking on a straight strip).
        for (int i = 1; i < path.Count; i++)
            Assert.True(path[i].x >= path[i - 1].x - 0.6f, $"waypoint {i} went backwards: {path[i - 1].x} -> {path[i].x}");
    }

    [Fact]
    public void Disconnected_meshes_yield_no_path()
    {
        // Two separate quads with a gap between them (no shared edge) → goal unreachable from start.
        float[] verts =
        {
            0,0,0,  1,0,0,  0,0,1,  1,0,1,     // quad A near origin
            5,0,0,  6,0,0,  5,0,1,  6,0,1,     // quad B far away
        };
        int[] tris = { 0,1,2,  1,3,2,   4,5,6,  5,7,6 };
        var mesh = new NavMesh(verts, tris);
        var path = NavMeshPath.Find(mesh, 0.5f, 0.5f, 5.5f, 0.5f);
        Assert.Empty(path);   // no corridor connects the two islands
    }

    [Fact]
    public void Path_stays_on_the_mesh_height()
    {
        var mesh = Strip(5);
        var path = NavMeshPath.Find(mesh, 0.5f, 0.5f, 4.5f, 0.5f);
        Assert.All(path, wp => Assert.Equal(0f, wp.y, 3));   // flat strip → every waypoint at y=0
    }
}
