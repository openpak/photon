using System.Buffers.Binary;
using BlackIce.Tools.MapExtractor;

namespace MapExtractor.Tests;

/// <summary>
/// Tests the Detour tile-blob decode in <see cref="NavMeshDataParser"/> against synthetic blobs built to the
/// layout verified from the game's baked NavMeshData (Unity 2020.3.49f1, tile magic "VAND", header 72 bytes,
/// 32-byte dtPoly with the corner count in the trailing int). These tests use no game asset and no
/// AssetsTools — they exercise the pure byte-decoder via a tiny tile builder, so the decode is regression
/// -locked to the real layout.
/// </summary>
public sealed class NavMeshTileBlobTests
{
    private const int DtNavMeshMagic = ('D' << 24) | ('N' << 16) | ('A' << 8) | 'V'; // 0x444E4156 "VAND"
    private const int HeaderSize = 72;
    private const int DtPolyStride = 32;

    [Fact]
    public void TriangulatesTwoQuadsIntoFourTriangles()
    {
        // Two quads (vertCount 4 each) sharing the edge (1,0,1)-(1,0,0): 2 fan-triangles per quad = 4 total.
        float[] verts =
        {
            0, 0, 0, // 0
            1, 0, 0, // 1
            1, 0, 1, // 2
            0, 0, 1, // 3
            2, 0, 0, // 4
            2, 0, 1, // 5
        };
        var polys = new[]
        {
            new ushort[] { 0, 1, 2, 3 }, // quad A
            new ushort[] { 1, 4, 5, 2 }, // quad B (shares edge 1-2 with A)
        };

        var sink = new MeshTriangleSet();
        NavMeshDataParser.ParseTileBlob(BuildTile(verts, polys), sink);

        Assert.Equal(4, sink.TriangleCount);
        // 6 source corners; quads share the edge 1-2, so welding yields the 6 distinct positions.
        Assert.Equal(6, sink.VertexCount);
    }

    [Fact]
    public void TriangulatesTriangleAndHexagon()
    {
        // One triangle (3 corners -> 1 tri) and one hexagon (6 corners -> 4 fan tris) = 5 triangles.
        float[] verts =
        {
            0, 0, 0,  1, 0, 0,  0, 0, 1,                 // triangle: 0,1,2
            5, 0, 0,  6, 0, 0,  6, 0, 1,                 // hex: 3,4,5
            6, 0, 2,  5, 0, 2,  4, 0, 1,                 //      6,7,8
        };
        var polys = new[]
        {
            new ushort[] { 0, 1, 2 },
            new ushort[] { 3, 4, 5, 6, 7, 8 },
        };

        var sink = new MeshTriangleSet();
        NavMeshDataParser.ParseTileBlob(BuildTile(verts, polys), sink);

        Assert.Equal(1 + 4, sink.TriangleCount);
    }

    [Fact]
    public void SkipsTileWithWrongMagic()
    {
        byte[] blob = BuildTile(new float[] { 0, 0, 0, 1, 0, 0, 0, 0, 1 }, new[] { new ushort[] { 0, 1, 2 } });
        BinaryPrimitives.WriteInt32LittleEndian(blob, 0xDEAD); // corrupt the magic

        var sink = new MeshTriangleSet();
        NavMeshDataParser.ParseTileBlob(blob, sink);

        Assert.Equal(0, sink.TriangleCount); // unknown layout -> skipped, not thrown
    }

    [Fact]
    public void SkipsPolygonReferencingOutOfRangeVertex()
    {
        // A valid triangle plus a polygon that references a vertex index >= vertCount: the bad poly is dropped.
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 0, 1 }; // 3 verts (indices 0..2)
        var polys = new[]
        {
            new ushort[] { 0, 1, 2 },   // valid
            new ushort[] { 0, 1, 9 },   // index 9 is out of range
        };

        var sink = new MeshTriangleSet();
        NavMeshDataParser.ParseTileBlob(BuildTile(verts, polys), sink);

        Assert.Equal(1, sink.TriangleCount);
    }

    [Fact]
    public void DoesNotThrowOnTruncatedBlob()
    {
        byte[] full = BuildTile(new float[] { 0, 0, 0, 1, 0, 0, 0, 0, 1 }, new[] { new ushort[] { 0, 1, 2 } });
        byte[] truncated = full[..(HeaderSize + 6)]; // header + a partial vertex

        var sink = new MeshTriangleSet();
        NavMeshDataParser.ParseTileBlob(truncated, sink); // must not throw
        Assert.Equal(0, sink.TriangleCount);
    }

    /// <summary>
    /// Builds a Detour tile blob in the verified layout: 72-byte header (magic, version, x/y/layer,
    /// polyCount, vertCount, maxLinkCount, detailMeshCount, detailVertCount, detailTriCount, then 7 floats),
    /// the vertex floats, then one 32-byte dtPoly per polygon with the corner count in the trailing int.
    /// Detail/link/BV data is omitted — the decoder only reads the base polygons.
    /// </summary>
    private static byte[] BuildTile(float[] verts, ushort[][] polys)
    {
        int vertCount = verts.Length / 3;
        int polyCount = polys.Length;
        byte[] blob = new byte[HeaderSize + verts.Length * 4 + polyCount * DtPolyStride];

        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(0), DtNavMeshMagic);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4), 16);          // version
        // x, y, layer at 8,12,16 left zero.
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(20), polyCount);  // OffPolyCount
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(24), vertCount);  // OffVertCount
        // maxLinkCount/detail counts and the 7 trailing floats are unused by the decoder.

        int pos = HeaderSize;
        foreach (float f in verts)
        {
            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(pos), f);
            pos += 4;
        }

        foreach (var poly in polys)
        {
            int o = pos;
            for (int k = 0; k < poly.Length && k < 6; k++)
                BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(o + k * 2), poly[k]); // verts[6]
            // neis[6] (o+12..o+24) and flagsAndArea (o+24..o+28) left zero.
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(o + 28), poly.Length);     // poly vertCount
            pos += DtPolyStride;
        }

        return blob;
    }
}
