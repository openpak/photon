using System.Buffers.Binary;

namespace BlackIce.Server.Core.Navigation;

/// <summary>
/// A walkable-surface model learned at runtime from observed positions, for worlds we have no static navmesh
/// for (Black Ice assembles its levels procedurally, so the baked <see cref="NavMesh"/>es don't match the live
/// play space). Space is quantized into fixed-size cubic cells; a cell is "walkable" once a provably-grounded
/// position has been observed in it.
///
/// <para><b>Ground truth is the real player.</b> Only a real player's avatar is physics-simulated by its
/// client, so its position proves that spot is walkable. Server-puppeted bots have no collision — their
/// positions must NOT be recorded (they would mark mid-air/inside-wall cells as walkable). The recorder feeds
/// this from player positions only.</para>
///
/// <para>Cells are deduped (a <see cref="System.Collections.Generic.HashSet{T}"/> of integer grid coords), so
/// re-walking the same area is idempotent and the map converges on the level's reachable footprint. Persisted
/// as a compact "BWLK" file so coverage accumulates across sessions.</para>
/// </summary>
public sealed class WalkableMap
{
    private static readonly byte[] Magic = "BWLK"u8.ToArray();
    private const int Version = 1;

    private readonly float _cell;
    private readonly HashSet<(int x, int y, int z)> _cells = new();

    /// <param name="cellSize">Edge length of a quantization cell, in world units. Smaller = finer map but more
    /// cells; ~3u (roughly a player's footprint) is a good default.</param>
    public WalkableMap(float cellSize = 3f) => _cell = cellSize > 0.01f ? cellSize : 3f;

    public float CellSize => _cell;
    public int Count => _cells.Count;

    private (int, int, int) Key(float x, float y, float z) =>
        ((int)MathF.Round(x / _cell), (int)MathF.Round(y / _cell), (int)MathF.Round(z / _cell));

    /// <summary>Marks the cell containing (x,y,z) walkable. Returns true if this revealed a NEW cell (useful
    /// for "frontier" logging — how fast the map is still growing).</summary>
    public bool Record(float x, float y, float z) => _cells.Add(Key(x, y, z));

    /// <summary>True if the cell containing (x,y,z) has been observed walkable.</summary>
    public bool Contains(float x, float y, float z) => _cells.Contains(Key(x, y, z));

    /// <summary>Cell-center positions of every walkable cell (for export / nearest-walkable queries).</summary>
    public IEnumerable<(float x, float y, float z)> Points()
    {
        foreach (var (cx, cy, cz) in _cells)
            yield return (cx * _cell, cy * _cell, cz * _cell);
    }

    /// <summary>Axis-aligned bounds over all walkable cell centers, or all-zero when empty.</summary>
    public (float minX, float minY, float minZ, float maxX, float maxY, float maxZ) Bounds()
    {
        if (_cells.Count == 0) return (0, 0, 0, 0, 0, 0);
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var (x, y, z) in Points())
        {
            if (x < minX) minX = x; if (y < minY) minY = y; if (z < minZ) minZ = z;
            if (x > maxX) maxX = x; if (y > maxY) maxY = y; if (z > maxZ) maxZ = z;
        }
        return (minX, minY, minZ, maxX, maxY, maxZ);
    }

    /// <summary>Writes the map as a compact BWLK blob: magic, version, cell size, count, then int32 x,y,z per cell.</summary>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[4];
        stream.Write(Magic);
        WriteI(stream, buf, Version);
        BinaryPrimitives.WriteSingleLittleEndian(buf, _cell); stream.Write(buf);
        WriteI(stream, buf, _cells.Count);
        foreach (var (x, y, z) in _cells)
        {
            WriteI(stream, buf, x); WriteI(stream, buf, y); WriteI(stream, buf, z);
        }
    }

    /// <summary>Reads a BWLK blob written by <see cref="Save"/>. Throws on a bad magic/version.</summary>
    public static WalkableMap Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> hdr = stackalloc byte[4];
        ReadExact(stream, hdr);
        if (!hdr.SequenceEqual(Magic)) throw new InvalidDataException("not a BWLK walkable map (bad magic)");
        int version = ReadI(stream); if (version != Version) throw new InvalidDataException($"unsupported BWLK version {version}");
        Span<byte> f = stackalloc byte[4]; ReadExact(stream, f);
        float cell = BinaryPrimitives.ReadSingleLittleEndian(f);
        int count = ReadI(stream);
        if (count < 0 || count > 50_000_000) throw new InvalidDataException("implausible BWLK cell count");
        var map = new WalkableMap(cell);
        for (int i = 0; i < count; i++)
        {
            int x = ReadI(stream), y = ReadI(stream), z = ReadI(stream);
            map._cells.Add((x, y, z));
        }
        return map;
    }

    private static void WriteI(Stream s, Span<byte> buf, int v) { BinaryPrimitives.WriteInt32LittleEndian(buf, v); s.Write(buf); }
    private static int ReadI(Stream s) { Span<byte> b = stackalloc byte[4]; ReadExact(s, b); return BinaryPrimitives.ReadInt32LittleEndian(b); }
    private static void ReadExact(Stream s, Span<byte> buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf[read..]);
            if (n <= 0) throw new EndOfStreamException("truncated BWLK file");
            read += n;
        }
    }
}
