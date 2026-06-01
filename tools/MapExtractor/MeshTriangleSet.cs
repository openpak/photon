namespace BlackIce.Tools.MapExtractor;

/// <summary>
/// Accumulates walkable triangles from one or more source meshes into the flat vertex/index arrays the
/// server's <c>NavMesh</c> consumes. Deduplicates coincident vertices so that triangles which share an edge
/// in world space also share vertex indices — that is what lets <c>NavMesh</c> rebuild edge adjacency for
/// A* pathing. This is pure data plumbing (no AssetsTools or game types), so it is unit-testable with
/// synthetic input.
/// </summary>
public sealed class MeshTriangleSet
{
    private readonly List<float> _verts = new();
    private readonly List<int> _tris = new();
    // Map a quantized (x,y,z) position to its vertex index, so shared corners collapse to one vertex.
    private readonly Dictionary<(int, int, int), int> _index = new();

    /// <summary>Quantization step (world units) for vertex welding. NavMesh corners are exact in the source
    /// data; a small grid tolerates float round-trip noise without merging genuinely distinct vertices.</summary>
    public float WeldEpsilon { get; init; } = 1e-4f;

    public int VertexCount => _verts.Count / 3;
    public int TriangleCount => _tris.Count / 3;

    /// <summary>Adds a triangle by its three world-space corners. Winding is preserved as given.</summary>
    public void AddTriangle(
        (float x, float y, float z) a,
        (float x, float y, float z) b,
        (float x, float y, float z) c)
    {
        int ia = Intern(a), ib = Intern(b), ic = Intern(c);
        // Drop fully degenerate triangles (two corners welded to the same vertex): they carry no surface.
        if (ia == ib || ib == ic || ia == ic) return;
        _tris.Add(ia); _tris.Add(ib); _tris.Add(ic);
    }

    /// <summary>Adds an indexed mesh: flat XYZ vertices and triangle index triples.</summary>
    public void AddIndexedMesh(IReadOnlyList<float> vertices, IReadOnlyList<int> indices)
    {
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            int v0 = indices[i] * 3, v1 = indices[i + 1] * 3, v2 = indices[i + 2] * 3;
            if (v0 + 2 >= vertices.Count || v1 + 2 >= vertices.Count || v2 + 2 >= vertices.Count) continue;
            AddTriangle(
                (vertices[v0], vertices[v0 + 1], vertices[v0 + 2]),
                (vertices[v1], vertices[v1 + 1], vertices[v1 + 2]),
                (vertices[v2], vertices[v2 + 1], vertices[v2 + 2]));
        }
    }

    public float[] Vertices => _verts.ToArray();
    public int[] Triangles => _tris.ToArray();

    private int Intern((float x, float y, float z) p)
    {
        var key = (Q(p.x), Q(p.y), Q(p.z));
        if (_index.TryGetValue(key, out int existing)) return existing;
        int idx = _verts.Count / 3;
        _verts.Add(p.x); _verts.Add(p.y); _verts.Add(p.z);
        _index[key] = idx;
        return idx;
    }

    private int Q(float v) => (int)MathF.Round(v / WeldEpsilon);
}
