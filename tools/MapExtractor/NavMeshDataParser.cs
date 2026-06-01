using AssetsTools.NET;

namespace BlackIce.Tools.MapExtractor;

/// <summary>
/// Parses a Unity <c>NavMeshData</c> serialized object (class id 238) into walkable triangles.
///
/// <para>NavMeshData stores the baked navigation surface as an array of <c>m_NavMeshTiles</c>. Each tile's
/// <c>m_MeshData</c> is a binary blob produced by Unity's embedded Detour build — internally a
/// <c>dtMeshHeader</c> + arrays of polygons, vertices and a "detail mesh" (the fine triangulation used for
/// height). The polygon/detail triangle layout is the part that needs verification against a real baked
/// asset (see the GAP note in <see cref="ParseTileBlob"/>). The serialized field <em>structure</em> around
/// the blob (the tile array, the byte payload) is stable and read here via the type tree.</para>
/// </summary>
public static class NavMeshDataParser
{
    /// <summary>
    /// Reads every NavMeshData tile in the given base field and appends its triangles to <paramref name="sink"/>.
    /// Returns the number of tiles whose blob was parsed into at least one triangle.
    /// </summary>
    public static int ExtractTriangles(AssetTypeValueField navMeshData, MeshTriangleSet sink)
    {
        ArgumentNullException.ThrowIfNull(navMeshData);
        ArgumentNullException.ThrowIfNull(sink);

        // NavMeshData.m_NavMeshTiles : vector of NavMeshTileData. Field names match Unity's serialized layout.
        var tiles = navMeshData["m_NavMeshTiles"]["Array"];
        if (tiles.IsDummy)
            return 0;

        int parsed = 0;
        foreach (var tile in tiles)
        {
            // NavMeshTileData.m_MeshData : vector<UInt8> — the Detour tile blob.
            var meshData = tile["m_MeshData"]["Array"];
            if (meshData.IsDummy) continue;

            byte[] blob = ReadByteArray(meshData);
            if (blob.Length == 0) continue;

            int before = sink.TriangleCount;
            ParseTileBlob(blob, sink);
            if (sink.TriangleCount > before) parsed++;
        }
        return parsed;
    }

    /// <summary>
    /// Decodes one Detour navmesh tile blob into triangles and appends them to <paramref name="sink"/>.
    ///
    /// <para>// GAP: the exact byte layout of Unity 2020.3's serialized Detour tile (header field order,
    /// endianness, whether the detail-mesh sub-triangulation or the base polygons are authoritative for the
    /// walkable surface) must be confirmed against a real baked <c>NavMeshData</c> from the game — we cannot
    /// do that here without the game asset. The conservative, well-documented decode below mirrors the public
    /// Recast/Detour <c>dtMeshHeader</c> + <c>dtPoly</c> + <c>dtPolyDetail</c> layout that Unity is built on;
    /// it is isolated in this one method so a single verified tweak (field offsets / counts) makes it
    /// correct without touching the rest of the pipeline. Until verified, prefer the
    /// <c>--source colliders</c> fallback for ground truth.</para>
    /// </summary>
    private static void ParseTileBlob(byte[] blob, MeshTriangleSet sink)
    {
        // Detour serializes little-endian on the platforms this game ships for. We read defensively and bail
        // out (rather than throw) on any inconsistency so a single malformed tile can't abort the whole map.
        try
        {
            var r = new BlobReader(blob);

            // --- dtMeshHeader (Recast/Detour reference layout) -------------------------------------------
            // int magic; int version; int x; int y; int layer; uint userId;
            // int polyCount; int vertCount; int maxLinkCount;
            // int detailMeshCount; int detailVertCount; int detailTriCount;
            // ... (BV tree / off-mesh connection counts, bounds, walkable params follow)
            int magic = r.Int();
            int version = r.Int();
            // DT_NAVMESH_MAGIC = 'DNAV' (0x444E4156). If it doesn't match, this isn't the layout we expect.
            const int DtNavMeshMagic = ('D' << 24) | ('N' << 16) | ('A' << 8) | 'V';
            if (magic != DtNavMeshMagic && magic != ReverseBytes(DtNavMeshMagic))
                return; // GAP: unknown header — needs live-asset verification of the real magic/version.
            _ = version;
            r.Int(); r.Int(); r.Int();        // x, y, layer
            r.UInt();                          // userId
            int polyCount = r.Int();
            int vertCount = r.Int();
            r.Int();                           // maxLinkCount
            int detailMeshCount = r.Int();
            int detailVertCount = r.Int();
            int detailTriCount = r.Int();
            r.Int();                           // bvNodeCount
            r.Int();                           // offMeshConCount
            r.Int();                           // offMeshBase
            float walkableHeight = r.Float();  // walkable params (unused for geometry)
            float walkableRadius = r.Float();
            float walkableClimb = r.Float();
            _ = (walkableHeight, walkableRadius, walkableClimb);
            // bmin[3], bmax[3], bvQuantFactor
            float bminX = r.Float(), bminY = r.Float(), bminZ = r.Float();
            r.Float(); r.Float(); r.Float();   // bmax
            r.Float();                         // bvQuantFactor

            if (polyCount <= 0 || vertCount <= 0 || vertCount > 1_000_000 || polyCount > 1_000_000)
                return; // implausible counts -> wrong layout; defer to verification.

            // --- vertices (float x,y,z per vert) --------------------------------------------------------
            var verts = new float[vertCount * 3];
            for (int i = 0; i < verts.Length; i++) verts[i] = r.Float();
            _ = (bminX, bminY, bminZ);

            // --- detail vertices (float x,y,z) ----------------------------------------------------------
            var detailVerts = new float[Math.Max(0, detailVertCount) * 3];
            for (int i = 0; i < detailVerts.Length; i++) detailVerts[i] = r.Float();

            // --- polygons (dtPoly): ushort firstLink; ushort[6] verts; ushort[6] neis; ushort flags;
            //                        byte vertCount; byte areaAndType ---------------------------------------
            const int DtVertsPerPoly = 6;
            var polyVerts = new ushort[polyCount][];
            var polyVcount = new int[polyCount];
            for (int p = 0; p < polyCount; p++)
            {
                r.UShort(); // firstLink
                var pv = new ushort[DtVertsPerPoly];
                for (int k = 0; k < DtVertsPerPoly; k++) pv[k] = r.UShort();
                for (int k = 0; k < DtVertsPerPoly; k++) r.UShort(); // neis
                r.UShort(); // flags
                polyVcount[p] = r.Byte();
                r.Byte();   // areaAndType
                polyVerts[p] = pv;
            }

            // --- detail meshes (dtPolyDetail): uint vertBase; uint triBase; byte vertCount; byte triCount --
            var detVertBase = new int[Math.Max(detailMeshCount, 0)];
            var detTriBase = new int[Math.Max(detailMeshCount, 0)];
            var detTriCount = new int[Math.Max(detailMeshCount, 0)];
            for (int d = 0; d < detailMeshCount; d++)
            {
                detVertBase[d] = (int)r.UInt();
                detTriBase[d] = (int)r.UInt();
                r.Byte();                     // detail vert count (per poly) — not needed for triangulation
                detTriCount[d] = r.Byte();
            }

            // --- detail triangles (4 bytes each: vertA, vertB, vertC, edgeFlags) -------------------------
            // Each index < poly.vertCount references a base poly vertex; >= references a detail vertex.
            var detailTris = new byte[Math.Max(detailTriCount, 0) * 4];
            for (int i = 0; i < detailTris.Length; i++) detailTris[i] = r.Byte();

            // Emit the detail triangulation (the actual walkable surface) when present; otherwise fan the
            // base polygons. Resolve each index against poly base verts or detail verts per Detour rules.
            if (detailMeshCount == polyCount && detailTriCount > 0)
            {
                for (int p = 0; p < polyCount; p++)
                {
                    int baseTri = detTriBase[p], nTri = detTriCount[p], pvc = polyVcount[p];
                    for (int t = 0; t < nTri; t++)
                    {
                        int o = (baseTri + t) * 4;
                        if (o + 2 >= detailTris.Length) break;
                        var a = ResolveDetailVertex(detailTris[o], p, pvc, polyVerts, verts, detVertBase[p], detailVerts);
                        var b = ResolveDetailVertex(detailTris[o + 1], p, pvc, polyVerts, verts, detVertBase[p], detailVerts);
                        var c = ResolveDetailVertex(detailTris[o + 2], p, pvc, polyVerts, verts, detVertBase[p], detailVerts);
                        if (a is { } av && b is { } bv && c is { } cv) sink.AddTriangle(av, bv, cv);
                    }
                }
            }
            else
            {
                // Fallback within a tile: triangulate base polygons as fans.
                for (int p = 0; p < polyCount; p++)
                {
                    int pvc = polyVcount[p];
                    for (int k = 2; k < pvc; k++)
                    {
                        var a = BaseVertex(polyVerts[p][0], verts);
                        var b = BaseVertex(polyVerts[p][k - 1], verts);
                        var c = BaseVertex(polyVerts[p][k], verts);
                        if (a is { } av && b is { } bv && c is { } cv) sink.AddTriangle(av, bv, cv);
                    }
                }
            }
        }
        catch
        {
            // GAP: any decode mismatch means the assumed layout is wrong for this build; skip the tile rather
            // than crash. A verified layout tweak removes these skips. Colliders mode is the reliable fallback.
        }
    }

    private static (float, float, float)? ResolveDetailVertex(
        byte idx, int poly, int polyVcount, ushort[][] polyVerts, float[] verts, int detVertBase, float[] detailVerts)
    {
        if (idx < polyVcount)
            return BaseVertex(polyVerts[poly][idx], verts);
        int dv = (detVertBase + (idx - polyVcount)) * 3;
        if (dv + 2 >= detailVerts.Length) return null;
        return (detailVerts[dv], detailVerts[dv + 1], detailVerts[dv + 2]);
    }

    private static (float, float, float)? BaseVertex(ushort vIdx, float[] verts)
    {
        int o = vIdx * 3;
        if (o + 2 >= verts.Length) return null;
        return (verts[o], verts[o + 1], verts[o + 2]);
    }

    private static int ReverseBytes(int v) =>
        (int)(((uint)v >> 24) | (((uint)v >> 8) & 0xFF00) | (((uint)v << 8) & 0xFF0000) | ((uint)v << 24));

    /// <summary>Reads a <c>vector&lt;UInt8&gt;</c> value field into a byte array (fast path via AsByteArray).</summary>
    private static byte[] ReadByteArray(AssetTypeValueField arrayField)
    {
        // AssetsTools exposes byte arrays directly when the element type is UInt8.
        try
        {
            var direct = arrayField.AsByteArray;
            if (direct is { Length: > 0 }) return direct;
        }
        catch { /* fall through to element-wise read */ }

        var list = new List<byte>(arrayField.Children.Count);
        foreach (var e in arrayField) list.Add(e.AsByte);
        return list.ToArray();
    }

    /// <summary>Minimal little-endian sequential reader over the tile blob.</summary>
    private sealed class BlobReader
    {
        private readonly byte[] _b;
        private int _p;
        public BlobReader(byte[] b) => _b = b;
        public int Int() { int v = BitConverter.ToInt32(_b, _p); _p += 4; return v; }
        public uint UInt() { uint v = BitConverter.ToUInt32(_b, _p); _p += 4; return v; }
        public float Float() { float v = BitConverter.ToSingle(_b, _p); _p += 4; return v; }
        public ushort UShort() { ushort v = BitConverter.ToUInt16(_b, _p); _p += 2; return v; }
        public byte Byte() => _b[_p++];
    }
}
