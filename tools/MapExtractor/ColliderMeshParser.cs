using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace BlackIce.Tools.MapExtractor;

/// <summary>
/// Fallback geometry source: extracts triangles from the scene's <c>MeshCollider</c> / <c>MeshFilter</c>
/// shared meshes rather than the baked NavMeshData. These are ordinary serialized <c>Mesh</c> objects whose
/// vertex/index layout is well understood, so this path is the reliable ground truth while the Detour tile
/// decode in <see cref="NavMeshDataParser"/> awaits live-asset verification. Note: collider geometry is the
/// raw world surface (walls, ceilings) — not a walkable filter — so it is a coarser navmesh than the baked
/// data; the spec documents NavMeshData as preferred and colliders as the fallback.
/// </summary>
public static class ColliderMeshParser
{
    /// <summary>
    /// Walks MeshCollider (then MeshFilter) components, follows each <c>m_Mesh</c>/<c>m_Sharedmesh</c> PPtr to
    /// its <c>Mesh</c>, and appends that mesh's triangles to <paramref name="sink"/>. Returns the mesh count.
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
            var comp = am.GetBaseField(scene, info);
            if (comp is null) continue;
            var meshPtr = comp[meshPtrField];
            if (meshPtr.IsDummy) continue;

            // Resolve the PPtr<Mesh> (handles same-file and external-file references).
            var ext = am.GetExtAsset(scene, meshPtr, onlyGetInfo: false, AssetReadFlags.None);
            if (ext.baseField is null) continue;
            if (TryAddMesh(ext.baseField, sink)) count++;
        }
        return count;
    }

    /// <summary>
    /// Reads a <c>Mesh</c> base field into triangles. Unity stores vertices either in the per-vertex
    /// <c>m_VertexData</c> stream or (older/uncompressed) in legacy float arrays; index data lives in
    /// <c>m_IndexBuffer</c> with a 16- or 32-bit format selector. We handle the common uncompressed cases and
    /// note where a packed/compressed mesh would need extra decoding.
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

        // Preferred: interleaved m_VertexData (position is the first channel, 3 floats, at the start of each
        // vertex stride). We read stride and vertex count and pull the leading XYZ of every vertex.
        var vertexData = mesh["m_VertexData"];
        if (!vertexData.IsDummy)
        {
            int vertexCount = vertexData["m_VertexCount"].AsInt;
            byte[] data = ReadBytes(vertexData["m_DataSize"]);
            // Channel 0 (position) stride: derive from the channels table; fall back to 0 if unavailable.
            var channels = vertexData["m_Channels"]["Array"];
            int stride = DerivePositionStride(channels, vertexData);
            if (vertexCount > 0 && stride >= 12 && data.Length >= (long)vertexCount * stride)
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

        // Legacy/uncompressed fallback: m_Vertices float array of [x,y,z, x,y,z, ...].
        var legacy = mesh["m_Vertices"]["Array"];
        if (!legacy.IsDummy)
            foreach (var f in legacy) result.Add(f.AsFloat);
        return result;
    }

    // The position channel (index 0) packs 3 floats at the front of each vertex; the stride is the sum of all
    // channel sizes for the same stream. We compute it from dimension * format-size across channels.
    private static int DerivePositionStride(AssetTypeValueField channels, AssetTypeValueField vertexData)
    {
        int stride = 0;
        foreach (var ch in channels)
        {
            int dimension = ch["dimension"].AsInt & 0x0F; // low nibble = component count in 2020.3
            int format = ch["format"].AsByte;
            stride += dimension * FormatSize(format);
        }
        // If channel parsing yields nothing useful, infer a tight position-only stride.
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
        byte[] indexBuffer = ReadBytes(mesh["m_IndexBuffer"]);
        if (indexBuffer.Length == 0) return result;

        // m_IndexFormat: 0 = UInt16, 1 = UInt32 (2017+). Default to 16-bit when absent.
        int indexFormat = mesh["m_IndexFormat"].IsDummy ? 0 : mesh["m_IndexFormat"].AsInt;
        if (indexFormat == 1)
            for (int i = 0; i + 3 < indexBuffer.Length; i += 4)
                result.Add(BitConverter.ToInt32(indexBuffer, i));
        else
            for (int i = 0; i + 1 < indexBuffer.Length; i += 2)
                result.Add(BitConverter.ToUInt16(indexBuffer, i));

        // m_SubMeshes describe (firstByte, indexCount, topology). We assume triangle lists (topology 0); the
        // accumulator simply groups successive indices into triangles, which is correct for triangle lists.
        return result;
    }

    private static byte[] ReadBytes(AssetTypeValueField field)
    {
        if (field.IsDummy) return Array.Empty<byte>();
        try
        {
            var direct = field.AsByteArray;
            if (direct is { Length: > 0 }) return direct;
        }
        catch { /* not a byte array field; try array of bytes */ }

        var arr = field["Array"];
        if (arr.IsDummy) return Array.Empty<byte>();
        var list = new List<byte>(arr.Children.Count);
        foreach (var e in arr) list.Add(e.AsByte);
        return list.ToArray();
    }
}
