using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace BlackIce.Tools.MapExtractor;

/// <summary>
/// Fallback geometry source: extracts triangles from the scene's <c>MeshCollider</c> / <c>MeshFilter</c>
/// shared meshes rather than the baked NavMeshData. These are ordinary serialized <c>Mesh</c> objects whose
/// vertex/index layout is well understood. Note: collider geometry is the raw world surface (walls,
/// ceilings) — not a walkable filter — so it is a coarser navmesh than the baked data; the spec documents
/// NavMeshData as preferred and colliders as the fallback.
///
/// <para>Every field access is guarded against dummy/missing fields: under the game's stripped type tree a
/// mesh may legitimately lack a channel table, an index buffer, or have an empty submesh. A mesh we can't
/// read is skipped, never fatal.</para>
/// </summary>
public static class ColliderMeshParser
{
    /// <summary>
    /// Walks MeshCollider (then MeshFilter) components, follows each <c>m_Mesh</c> PPtr to its <c>Mesh</c>,
    /// and appends that mesh's triangles to <paramref name="sink"/>. Returns the count of meshes that yielded
    /// geometry.
    /// </summary>
    public static int ExtractTriangles(AssetsManager am, AssetsFileInstance scene, MeshTriangleSet sink)
    {
        ArgumentNullException.ThrowIfNull(am);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sink);

        int meshes = 0;
        meshes += FollowComponents(am, scene, AssetClassID.MeshCollider, "m_Mesh", sink);
        meshes += FollowComponents(am, scene, AssetClassID.MeshFilter, "m_Mesh", sink);
        return meshes;
    }

    private static int FollowComponents(
        AssetsManager am, AssetsFileInstance scene, AssetClassID classId, string meshPtrField, MeshTriangleSet sink)
    {
        int count = 0;
        foreach (var info in scene.file.GetAssetsOfType(classId))
        {
            AssetTypeValueField? comp;
            try { comp = am.GetBaseField(scene, info, AssetReadFlags.None); }
            catch { continue; }
            if (comp is null) continue;

            var meshPtr = comp[meshPtrField];
            if (meshPtr.IsDummy) continue;

            // Resolve the PPtr<Mesh> (handles same-file and external-file references).
            AssetExternal ext;
            try { ext = am.GetExtAsset(scene, meshPtr, onlyGetInfo: false, AssetReadFlags.None); }
            catch { continue; }
            if (ext.baseField is null) continue;

            try { if (TryAddMesh(ext.baseField, sink)) count++; }
            catch { /* a single unreadable mesh must not abort the whole scene */ }
        }
        return count;
    }

    /// <summary>
    /// Reads a <c>Mesh</c> base field into triangles. Unity stores vertices in the interleaved
    /// <c>m_VertexData</c> stream (position is channel 0, the leading XYZ of each vertex); index data lives in
    /// <c>m_IndexBuffer</c> with a 16- or 32-bit format selector. Returns false if the mesh has no readable
    /// geometry (e.g. an empty submesh, or vertices stored in an external <c>.resS</c> we don't load).
    /// </summary>
    public static bool TryAddMesh(AssetTypeValueField mesh, MeshTriangleSet sink)
    {
        var verts = ReadVertices(mesh);
        if (verts.Count == 0) return false;

        var indices = ReadIndices(mesh);
        if (indices.Count == 0) return false;

        int before = sink.TriangleCount;
        sink.AddIndexedMesh(verts, indices);
        return sink.TriangleCount > before;
    }

    private static List<float> ReadVertices(AssetTypeValueField mesh)
    {
        var result = new List<float>();

        // Interleaved m_VertexData: position is channel 0 (3 floats at the start of each vertex stride). We
        // read the vertex count and the inline data blob, derive the stride, and pull the leading XYZ.
        var vertexData = SafeChild(mesh, "m_VertexData");
        if (vertexData is not null)
        {
            // If vertices live in an external .resS (m_StreamData.size > 0), we don't load it — skip; the
            // navmesh source is the proper extractor and colliders are a best-effort fallback.
            var streamData = SafeChild(mesh, "m_StreamData");
            long streamSize = streamData is null ? 0 : SafeUInt(streamData, "size");

            int vertexCount = (int)SafeUInt(vertexData, "m_VertexCount");
            byte[] data = ReadBytes(SafeChild(vertexData, "m_DataSize"));
            var channels = SafeArray(SafeChild(vertexData, "m_Channels"));
            int stride = DerivePositionStride(channels);

            if (streamSize == 0 && vertexCount > 0 && stride >= 12 && data.Length >= (long)vertexCount * stride)
            {
                for (int v = 0; v < vertexCount; v++)
                {
                    int o = v * stride;
                    result.Add(BitConverter.ToSingle(data, o));
                    result.Add(BitConverter.ToSingle(data, o + 4));
                    result.Add(BitConverter.ToSingle(data, o + 8));
                }
                return result;
            }
        }

        // Legacy/uncompressed fallback: m_Vertices float array of [x,y,z, x,y,z, ...] (rare in 2020.3).
        var legacy = SafeArray(SafeChild(mesh, "m_Vertices"));
        if (legacy is not null)
            foreach (var f in legacy) result.Add(f.AsFloat);
        return result;
    }

    // The position channel (index 0) packs 3 floats at the front of each vertex; the stride is the sum of all
    // channel sizes for the same stream. We compute it from dimension * format-size across channels.
    private static int DerivePositionStride(AssetTypeValueField? channels)
    {
        if (channels is null) return 12; // no channel table: assume a tight position-only stride.
        int stride = 0;
        foreach (var ch in channels)
        {
            int dimension = SafeInt(ch, "dimension") & 0x0F; // low nibble = component count in 2020.3
            int format = SafeByte(ch, "format");
            stride += dimension * FormatSize(format);
        }
        return stride > 0 ? stride : 12;
    }

    private static int FormatSize(int format) => format switch
    {
        0 => 4, // Float32
        1 => 2, // Float16
        2 => 1, // UNorm8
        3 => 1, // SNorm8
        4 => 2, // UNorm16
        5 => 2, // SNorm16
        6 => 1, // UInt8
        7 => 1, // SInt8
        8 => 2, // UInt16
        9 => 2, // SInt16
        10 => 4, // UInt32
        11 => 4, // SInt32
        _ => 4
    };

    private static List<int> ReadIndices(AssetTypeValueField mesh)
    {
        var result = new List<int>();
        byte[] indexBuffer = ReadBytes(SafeChild(mesh, "m_IndexBuffer"));
        if (indexBuffer.Length == 0) return result;

        // m_IndexFormat: 0 = UInt16, 1 = UInt32 (2017+). Default to 16-bit when absent.
        var fmt = SafeChild(mesh, "m_IndexFormat");
        int indexFormat = fmt is null ? 0 : fmt.AsInt;
        if (indexFormat == 1)
            for (int i = 0; i + 3 < indexBuffer.Length; i += 4)
                result.Add(BitConverter.ToInt32(indexBuffer, i));
        else
            for (int i = 0; i + 1 < indexBuffer.Length; i += 2)
                result.Add(BitConverter.ToUInt16(indexBuffer, i));

        // We assume triangle-list topology (the accumulator groups successive indices into triangles, which
        // is correct for triangle lists — the only topology MeshColliders use).
        return result;
    }

    // --- dummy-safe field helpers ---------------------------------------------------------------------

    /// <summary>Returns the named child, or null if the parent is null/dummy or the child is missing/dummy.</summary>
    private static AssetTypeValueField? SafeChild(AssetTypeValueField? parent, string name)
    {
        if (parent is null || parent.IsDummy) return null;
        var child = parent[name];
        return child.IsDummy ? null : child;
    }

    /// <summary>Returns the <c>Array</c> child of a vector field, or null if absent/dummy.</summary>
    private static AssetTypeValueField? SafeArray(AssetTypeValueField? vectorField) =>
        SafeChild(vectorField, "Array");

    private static int SafeInt(AssetTypeValueField parent, string name)
    {
        var c = SafeChild(parent, name);
        return c is null ? 0 : c.AsInt;
    }

    private static byte SafeByte(AssetTypeValueField parent, string name)
    {
        var c = SafeChild(parent, name);
        return c is null ? (byte)0 : c.AsByte;
    }

    private static long SafeUInt(AssetTypeValueField parent, string name)
    {
        var c = SafeChild(parent, name);
        return c is null ? 0 : c.AsLong;
    }

    /// <summary>
    /// Reads a byte payload from either a <c>TypelessData</c> field (direct bytes) or a <c>vector&lt;UInt8&gt;</c>
    /// (an <c>Array</c> of byte elements). Returns empty for a null/dummy field.
    /// </summary>
    private static byte[] ReadBytes(AssetTypeValueField? field)
    {
        if (field is null || field.IsDummy) return Array.Empty<byte>();

        // TypelessData / byte-typed fields expose the bytes directly.
        try
        {
            var direct = field.AsByteArray;
            if (direct is { Length: > 0 }) return direct;
        }
        catch { /* not a direct byte field; try an Array-of-bytes vector */ }

        var arr = SafeArray(field);
        if (arr is null) return Array.Empty<byte>();
        try
        {
            var direct = arr.AsByteArray;
            if (direct is { Length: > 0 }) return direct;
        }
        catch { /* fall through to element-wise */ }

        var list = new List<byte>(arr.Children.Count);
        foreach (var e in arr) list.Add(e.AsByte);
        return list.ToArray();
    }
}
