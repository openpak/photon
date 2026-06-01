using BlackIce.Server.Core.Navigation;
using BlackIce.Tools.MapExtractor;

namespace MapExtractor.Tests;

/// <summary>
/// The triangle accumulator is the seam between "geometry from a Unity object" and "the server's NavMesh".
/// It must weld coincident corners so that triangles sharing a world edge share a vertex index — which is
/// what lets NavMesh rebuild adjacency. These tests use synthetic geometry (no game asset, no AssetsTools)
/// and assert the result round-trips through the real BNAV writer/reader and yields working adjacency.
/// </summary>
public sealed class MeshTriangleSetTests
{
    [Fact]
    public void WeldsSharedCornersAcrossTriangles()
    {
        var set = new MeshTriangleSet();
        // Two triangles forming a unit quad on the XZ plane (y=0), sharing edge (1,0,0)-(0,0,1).
        set.AddTriangle((0, 0, 0), (1, 0, 0), (0, 0, 1));
        set.AddTriangle((1, 0, 0), (1, 0, 1), (0, 0, 1));

        // Quad has 4 unique corners despite 6 supplied -> welding worked.
        Assert.Equal(4, set.VertexCount);
        Assert.Equal(2, set.TriangleCount);
    }

    [Fact]
    public void DropsDegenerateTriangles()
    {
        var set = new MeshTriangleSet();
        set.AddTriangle((0, 0, 0), (0, 0, 0), (1, 0, 1)); // two identical corners
        Assert.Equal(0, set.TriangleCount);
    }

    [Fact]
    public void ProducesAdjacentNavMeshThatRoundTripsThroughBnav()
    {
        var set = new MeshTriangleSet();
        set.AddTriangle((0, 0, 0), (1, 0, 0), (0, 0, 1));
        set.AddTriangle((1, 0, 0), (1, 0, 1), (0, 0, 1));

        // Build a NavMesh (adjacency rebuilt from shared verts), write & read it via the server's BNAV format.
        var mesh = new NavMesh(set.Vertices, set.Triangles);
        using var ms = new MemoryStream();
        NavMeshFile.Write(ms, mesh);
        ms.Position = 0;
        var loaded = NavMeshFile.Read(ms);

        Assert.Equal(4, loaded.VertexCount);
        Assert.Equal(2, loaded.TriangleCount);

        // The two triangles share an edge, so each must have exactly one interior neighbor.
        int interiorEdges = 0;
        for (int t = 0; t < loaded.TriangleCount; t++)
            for (int e = 0; e < 3; e++)
                if (loaded.Neighbor(t, e) >= 0) interiorEdges++;
        Assert.Equal(2, interiorEdges); // one shared edge, counted from both sides
    }

    [Fact]
    public void AddIndexedMeshMatchesTriangleByTriangle()
    {
        // Indexed quad: 4 verts, 2 triangles.
        float[] verts = { 0, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1 };
        int[] idx = { 0, 1, 2, 1, 3, 2 };
        var set = new MeshTriangleSet();
        set.AddIndexedMesh(verts, idx);
        Assert.Equal(4, set.VertexCount);
        Assert.Equal(2, set.TriangleCount);
    }

    [Fact]
    public void AddIndexedMeshSkipsOutOfRangeIndices()
    {
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        int[] idx = { 0, 1, 2, 0, 1, 99 }; // second triangle references a missing vertex
        var set = new MeshTriangleSet();
        set.AddIndexedMesh(verts, idx);
        Assert.Equal(1, set.TriangleCount);
    }
}
