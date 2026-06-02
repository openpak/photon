using System.IO;
using BlackIce.Server.Core.Navigation;
using Xunit;

namespace BlackIce.Server.Tests.Navigation;

/// <summary>
/// The NavMesh data type + queries, tested entirely with SYNTHETIC meshes — no game asset required (the
/// format is our own original code; only extracted *data* is game-derived). Covers adjacency from shared
/// edges, point-in-triangle / nearest-point / height sampling, and the .navmesh file round-trip.
/// </summary>
public class NavMeshTests
{
    // A flat 2x1 quad on the XZ plane at y=0, split into two triangles sharing the diagonal (0,2):
    //   v0(0,0,0) v1(1,0,0) v2(0,0,1) v3(1,0,1)
    //   tri0 = 0,1,2   tri1 = 1,3,2   (share edge v1-v2)
    private static NavMesh Quad()
    {
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 0, 1 };
        int[] tris = { 0, 1, 2, 1, 3, 2 };
        return new NavMesh(verts, tris);
    }

    [Fact]
    public void Adjacency_is_computed_from_shared_edges()
    {
        var mesh = Quad();
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(2, mesh.TriangleCount);
        // The two triangles share the v1-v2 edge → each lists the other as a neighbor exactly once.
        int t0Neighbors = 0, t1Neighbors = 0;
        for (int e = 0; e < 3; e++) { if (mesh.Neighbor(0, e) == 1) t0Neighbors++; if (mesh.Neighbor(1, e) == 0) t1Neighbors++; }
        Assert.Equal(1, t0Neighbors);
        Assert.Equal(1, t1Neighbors);
    }

    [Fact]
    public void Bounds_and_ContainsXZ_describe_the_mesh_extent()
    {
        var mesh = Quad();   // x∈[0,1], y=0, z∈[0,1]
        Assert.Equal((0f, 0f, 0f, 1f, 0f, 1f), mesh.Bounds);
        Assert.True(mesh.ContainsXZ(0.5f, 0.5f));     // inside
        Assert.False(mesh.ContainsXZ(5f, 5f));        // outside
        Assert.True(mesh.ContainsXZ(2f, 2f, pad: 2f)); // outside but within the pad
    }

    [Fact]
    public void NearestPoint_inside_a_triangle_returns_that_point_on_the_surface()
    {
        var mesh = Quad();
        Assert.True(mesh.NearestPoint(0.2f, 0.2f, out var p, out int tri));
        Assert.Equal(0.2f, p.x, 3);
        Assert.Equal(0.2f, p.z, 3);
        Assert.Equal(0f, p.y, 3);          // flat quad at y=0
        Assert.InRange(tri, 0, 1);
    }

    [Fact]
    public void Floor_aware_NearestPoint_picks_the_storey_nearest_the_target_height()
    {
        // Two stacked floors sharing the same XZ footprint: a ground floor at y=0 and an upper floor at y=20.
        float[] verts =
        {
            0,0,0,  1,0,0,  0,0,1,  1,0,1,        // ground (y=0): verts 0..3
            0,20,0, 1,20,0, 0,20,1, 1,20,1,       // upper  (y=20): verts 4..7
        };
        int[] tris = { 0,1,2, 1,3,2,   4,5,6, 5,7,6 };
        var mesh = new NavMesh(verts, tris);

        // A query at (0.3,0.3) sits under BOTH floors. Asking near y=1 must return the ground floor (y=0);
        // asking near y=19 must return the upper floor (y=20).
        Assert.True(mesh.NearestPoint(0.3f, 0.3f, nearY: 1f, out var low, out _));
        Assert.Equal(0f, low.y, 3);
        Assert.True(mesh.NearestPoint(0.3f, 0.3f, nearY: 19f, out var high, out _));
        Assert.Equal(20f, high.y, 3);
    }

    [Fact]
    public void SampleHeight_interpolates_a_sloped_triangle()
    {
        // One triangle sloping from y=0 at origin to y=10 at (1,_,0).
        float[] verts = { 0, 0, 0, 1, 10, 0, 0, 0, 1 };
        int[] tris = { 0, 1, 2 };
        var mesh = new NavMesh(verts, tris);
        var h = mesh.SampleHeight(0.5f, 0.1f);
        Assert.NotNull(h);
        Assert.Equal(5f, h!.Value, 1);     // halfway along the x slope ≈ 5
    }

    [Fact]
    public void SampleHeight_off_mesh_is_null()
    {
        Assert.Null(Quad().SampleHeight(50f, 50f));
    }

    [Fact]
    public void File_round_trips_a_mesh()
    {
        var mesh = Quad();
        using var ms = new MemoryStream();
        NavMeshFile.Write(ms, mesh);
        ms.Position = 0;
        var loaded = NavMeshFile.Read(ms);
        Assert.Equal(mesh.VertexCount, loaded.VertexCount);
        Assert.Equal(mesh.TriangleCount, loaded.TriangleCount);
        Assert.Equal(mesh.VertexAt(3), loaded.VertexAt(3));
        Assert.Equal(mesh.Neighbor(0, 0), loaded.Neighbor(0, 0));
        Assert.Equal(mesh.Neighbor(0, 1), loaded.Neighbor(0, 1));
        Assert.Equal(mesh.Neighbor(0, 2), loaded.Neighbor(0, 2));
    }

    [Fact]
    public void Read_rejects_a_non_navmesh_blob()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
        Assert.Throws<InvalidDataException>(() => NavMeshFile.Read(ms));
    }

    [Fact]
    public void LoadOrNull_returns_null_for_a_missing_file()
    {
        Assert.Null(NavMeshFile.LoadOrNull(Path.Combine(Path.GetTempPath(), $"no-such-{System.Guid.NewGuid():N}.navmesh")));
    }
}
