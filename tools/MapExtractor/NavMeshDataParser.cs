using AssetsTools.NET;

namespace BlackIce.Tools.MapExtractor;

/// <summary>
/// Parses a Unity <c>NavMeshData</c> serialized object (class id 238) into walkable triangles.
///
/// <para>NavMeshData stores the baked navigation surface as an array of <c>m_NavMeshTiles</c>. Each tile's
/// <c>m_MeshData</c> is a binary blob produced by Unity's embedded Detour build — a <c>dtMeshHeader</c>
/// followed by the tile's vertices, polygons and (optionally) a detail sub-triangulation used only for
/// height. We triangulate the base polygons, which are the walkable surface; the detail mesh is height
/// refinement we don't need for pathing.</para>
///
/// <para><b>Verified layout (Unity 2020.3.49f1, Black Ice; tile magic <c>VAND</c> = Detour
/// <c>DT_NAVMESH_MAGIC</c> 0x444E4156, version 16), confirmed by probing the live baked asset:</b></para>
/// <code>
///   dtMeshHeader (72 bytes):
///     int    magic                      // 0x444E4156 ("VAND")
///     int    version                    // 16
///     int    x, y, layer                // tile grid coords
///     int    polyCount                  // base polygons
///     int    vertCount                  // base vertices
///     int    maxLinkCount               // (runtime links; not geometry)
///     int    detailMeshCount            // == polyCount when a detail mesh exists
///     int    detailVertCount            // extra detail vertices
///     int    detailTriCount             // detail triangles
///     float  bmin[3], bmax[3]           // tile AABB
///     float  bvQuantFactor
///   vertices : float[ vertCount * 3 ]   // x,y,z each (Unity Y-up world space)
///   polygons : dtPoly[ polyCount ]      // 32 bytes each:
///     ushort verts[6]                   // base-vertex indices (only first `vertCount` used)
///     ushort neis[6]                    // adjacency (unused here)
///     int    flagsAndArea               // poly flags/area
///     int    vertCount                  // 3..6 corners in this polygon
///   ... (links, detail meshes/verts/tris, BV tree follow — not needed for the surface)
/// </code>
///
/// <para>The header omits Detour's runtime-only <c>userId</c>/walkable-param/off-mesh fields, which is why
/// the float block (the tile AABB) begins right after the count ints. Triangulating each base polygon as a
/// fan over its <c>vertCount</c> corners yields the walkable mesh. Indices and counts are validated per
/// polygon, so a malformed tile is skipped rather than corrupting the output.</para>
/// </summary>
public static class NavMeshDataParser
{
    /// <summary>Detour tile magic, little-endian "VAND" (Unity's serialized <c>DT_NAVMESH_MAGIC</c>).</summary>
    private const int DtNavMeshMagic = ('D' << 24) | ('N' << 16) | ('A' << 8) | 'V'; // 0x444E4156

    // dtMeshHeader field byte offsets (see class doc).
    private const int OffMagic = 0;
    private const int OffPolyCount = 20;
    private const int OffVertCount = 24;
    private const int HeaderSize = 72;     // 18 * 4 bytes
    private const int DtVertsPerPoly = 6;
    private const int DtPolyStride = 32;   // verts[6] + neis[6] (u16) + flagsAndArea + vertCount (int)
    private const int OffPolyVertCount = 28; // within a dtPoly: the int vertCount

    /// <summary>
    /// Reads every NavMeshData tile in the given base field and appends its triangles to <paramref name="sink"/>.
    /// Returns the number of tiles whose blob was parsed into at least one triangle.
    /// </summary>
    public static int ExtractTriangles(AssetTypeValueField navMeshData, MeshTriangleSet sink)
    {
        ArgumentNullException.ThrowIfNull(navMeshData);
        ArgumentNullException.ThrowIfNull(sink);

        // Diagnostic (gated): dump the NavMeshData's transform + first tile AABB so we can see whether the
        // baked navmesh sits in a frame offset from the runtime world (it does for level12: surface ≈65u below
        // the live floor). Set BLACKICE_DUMP_NAVMESH_TRANSFORM=1 to enable. Read-only; no effect on output.
        if (Environment.GetEnvironmentVariable("BLACKICE_DUMP_NAVMESH_TRANSFORM") == "1")
            DumpTransform(navMeshData);

        // NavMeshData.m_NavMeshTiles : vector of NavMeshTileData (each with an m_MeshData byte blob).
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
            if (blob.Length < HeaderSize) continue;

            int before = sink.TriangleCount;
            ParseTileBlob(blob, sink);
            if (sink.TriangleCount > before) parsed++;
        }
        return parsed;
    }

    /// <summary>
    /// Decodes one Detour navmesh tile blob into triangles (base-polygon fan) and appends them to
    /// <paramref name="sink"/>. Validates the magic, counts and per-polygon corner indices; any inconsistency
    /// causes the tile to be skipped rather than throwing, so one bad tile can't abort the whole map.
    /// </summary>
    internal static void ParseTileBlob(byte[] blob, MeshTriangleSet sink)
    {
        try
        {
            // The platforms this game ships for are little-endian; BitConverter matches at runtime.
            int magic = BitConverter.ToInt32(blob, OffMagic);
            if (magic != DtNavMeshMagic)
                return; // not the Detour tile layout we verified.

            int polyCount = BitConverter.ToInt32(blob, OffPolyCount);
            int vertCount = BitConverter.ToInt32(blob, OffVertCount);
            if (polyCount <= 0 || vertCount <= 0 || polyCount > 1_000_000 || vertCount > 1_000_000)
                return;

            // --- base vertices (float x,y,z per vert) ---------------------------------------------------
            long vertsEnd = (long)HeaderSize + (long)vertCount * 3 * 4;
            if (vertsEnd > blob.Length) return;

            var verts = new float[vertCount * 3];
            for (int i = 0; i < verts.Length; i++)
                verts[i] = BitConverter.ToSingle(blob, HeaderSize + i * 4);

            // --- base polygons (dtPoly) -----------------------------------------------------------------
            long polysEnd = vertsEnd + (long)polyCount * DtPolyStride;
            if (polysEnd > blob.Length) return;

            int polyBase = (int)vertsEnd;
            Span<int> idx = stackalloc int[DtVertsPerPoly];
            for (int p = 0; p < polyCount; p++)
            {
                int o = polyBase + p * DtPolyStride;
                int pvc = BitConverter.ToInt32(blob, o + OffPolyVertCount);
                if (pvc < 3 || pvc > DtVertsPerPoly) continue; // off-mesh links / malformed poly

                // Read the polygon's corner indices and resolve them to positions up front.
                bool valid = true;
                for (int k = 0; k < pvc; k++)
                {
                    int vi = BitConverter.ToUInt16(blob, o + k * 2);
                    if (vi >= vertCount) { valid = false; break; }
                    idx[k] = vi;
                }
                if (!valid) continue;

                // Fan-triangulate the convex polygon (corners 0, k-1, k).
                var a = Vertex(idx[0], verts);
                for (int k = 2; k < pvc; k++)
                    sink.AddTriangle(a, Vertex(idx[k - 1], verts), Vertex(idx[k], verts));
            }
        }
        catch
        {
            // A truncated/malformed tile is skipped; the rest of the map still extracts.
        }
    }

    /// <summary>Diagnostic: print the NavMeshData transform fields and the first tile's AABB, to reveal a
    /// frame offset between the baked navmesh and the runtime world. Best-effort; any missing field is skipped.</summary>
    private static void DumpTransform(AssetTypeValueField navMeshData)
    {
        try
        {
            string V3(string name)
            {
                var f = navMeshData[name];
                return f is null || f.IsDummy ? $"{name}=<absent>"
                    : $"{name}=({f["x"].AsFloat:F2},{f["y"].AsFloat:F2},{f["z"].AsFloat:F2}" +
                      (f["w"].IsDummy ? ")" : $",{f["w"].AsFloat:F2})");
            }
            Console.Error.WriteLine($"[probe] NavMeshData {V3("m_Position")} {V3("m_Rotation")}");
            var tiles = navMeshData["m_NavMeshTiles"]["Array"];
            if (!tiles.IsDummy)
                foreach (var tile in tiles)
                {
                    var md = tile["m_MeshData"]["Array"];
                    if (md.IsDummy) continue;
                    byte[] b = ReadByteArray(md);
                    if (b.Length < HeaderSize || BitConverter.ToInt32(b, OffMagic) != DtNavMeshMagic) continue;
                    float bminY = BitConverter.ToSingle(b, 44 + 4), bmaxY = BitConverter.ToSingle(b, 56 + 4);
                    Console.Error.WriteLine($"[probe] tile bmin.y={bminY:F2} bmax.y={bmaxY:F2} " +
                                            $"(verts={BitConverter.ToInt32(b, OffVertCount)})");
                    break;   // first tile is enough to see the frame
                }
        }
        catch { /* probe is best-effort */ }
    }

    private static (float, float, float) Vertex(int vIdx, float[] verts)
    {
        int o = vIdx * 3;
        return (verts[o], verts[o + 1], verts[o + 2]);
    }

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
}
