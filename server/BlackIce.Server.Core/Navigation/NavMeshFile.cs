using System.Buffers.Binary;

namespace BlackIce.Server.Core.Navigation;

/// <summary>
/// Reads and writes the server's own compact <c>.navmesh</c> binary format. This format is original code —
/// it describes geometry but contains no Unity/game types — so the <em>format</em> is ours; the extracted
/// <em>data</em> is still game-derived and lives only in gitignored <c>maps/</c> artifacts (clean-room).
///
/// <para>The offline <c>tools/MapExtractor</c> writes this from a game scene's baked NavMeshData; the server
/// reads it at startup. Keeping read/write here (not in the extractor) means the server depends only on our
/// format and can be unit-tested with a synthetic mesh — no game asset required.</para>
///
/// <para>Layout (little-endian):
/// <code>
///   magic   : 4 bytes  "BNAV"
///   version : int32    (currently 1)
///   vCount  : int32    vertex count
///   tCount  : int32    triangle count
///   verts   : float32 × vCount×3   (x,y,z)
///   tris    : int32   × tCount×3   (vertex indices)
///   links   : int32   × tCount×3   (per-edge neighbor triangle, -1 = boundary)
/// </code>
/// Adjacency is stored so load is O(n) and need not recompute; a writer may pass -1s and let
/// <see cref="NavMesh"/> rebuild it.</para>
/// </summary>
public static class NavMeshFile
{
    private static readonly byte[] Magic = "BNAV"u8.ToArray();
    public const int Version = 1;

    public static void Write(Stream stream, NavMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(mesh);

        int vCount = mesh.VertexCount, tCount = mesh.TriangleCount;
        Span<byte> hdr = stackalloc byte[16];
        Magic.CopyTo(hdr);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[8..], vCount);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[12..], tCount);
        stream.Write(hdr);

        Span<byte> f = stackalloc byte[4];
        for (int i = 0; i < vCount; i++)
        {
            var (x, y, z) = mesh.VertexAt(i);
            WriteF(stream, f, x); WriteF(stream, f, y); WriteF(stream, f, z);
        }
        for (int t = 0; t < tCount; t++)
        {
            var (i0, i1, i2) = mesh.TriangleIndices(t);
            WriteI(stream, f, i0); WriteI(stream, f, i1); WriteI(stream, f, i2);
        }
        for (int t = 0; t < tCount; t++)
            for (int e = 0; e < 3; e++) WriteI(stream, f, mesh.Neighbor(t, e));
    }

    public static NavMesh Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> hdr = stackalloc byte[16];
        ReadExact(stream, hdr);
        if (!hdr[..4].SequenceEqual(Magic)) throw new InvalidDataException("not a BNAV navmesh (bad magic)");
        int version = BinaryPrimitives.ReadInt32LittleEndian(hdr[4..]);
        if (version != Version) throw new InvalidDataException($"unsupported navmesh version {version} (expected {Version})");
        int vCount = BinaryPrimitives.ReadInt32LittleEndian(hdr[8..]);
        int tCount = BinaryPrimitives.ReadInt32LittleEndian(hdr[12..]);
        if (vCount < 0 || tCount < 0) throw new InvalidDataException("negative count in navmesh header");

        var verts = new float[vCount * 3];
        var tris = new int[tCount * 3];
        var links = new int[tCount * 3];
        Span<byte> f = stackalloc byte[4];
        for (int i = 0; i < verts.Length; i++) { ReadExact(stream, f); verts[i] = BinaryPrimitives.ReadSingleLittleEndian(f); }
        for (int i = 0; i < tris.Length; i++) { ReadExact(stream, f); tris[i] = BinaryPrimitives.ReadInt32LittleEndian(f); }
        for (int i = 0; i < links.Length; i++) { ReadExact(stream, f); links[i] = BinaryPrimitives.ReadInt32LittleEndian(f); }
        return new NavMesh(verts, tris, links);
    }

    /// <summary>Loads a navmesh from a file path, or null if the file does not exist (the server's graceful
    /// "no extracted map → fall back to player-anchor behavior" path). Throws on a present-but-corrupt file.</summary>
    public static NavMesh? LoadOrNull(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    private static void WriteF(Stream s, Span<byte> buf, float v) { BinaryPrimitives.WriteSingleLittleEndian(buf, v); s.Write(buf); }
    private static void WriteI(Stream s, Span<byte> buf, int v) { BinaryPrimitives.WriteInt32LittleEndian(buf, v); s.Write(buf); }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf[read..]);
            if (n == 0) throw new EndOfStreamException("navmesh truncated");
            read += n;
        }
    }
}
