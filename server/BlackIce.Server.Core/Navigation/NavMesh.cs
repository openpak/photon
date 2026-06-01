namespace BlackIce.Server.Core.Navigation;

/// <summary>
/// A walkable-surface navigation mesh: a set of triangles (over a shared vertex list) with per-edge
/// adjacency, plus the queries playerbots need to move on the real map instead of guessing coordinates.
///
/// <para>This is the server's <b>own</b> representation — deliberately game-agnostic (just floats and
/// indices). It is produced offline by <c>tools/MapExtractor</c> from a game scene's baked NavMeshData and
/// loaded at runtime via <see cref="NavMeshFile"/>. The extracted artifact is game-derived and never
/// committed (clean-room); this type and its format are original code and fully testable with synthetic
/// meshes. See docs/superpowers/specs/2026-06-01-map-navmesh-design.md.</para>
///
/// <para>Coordinates follow the game's left-handed Y-up convention: XZ is the ground plane, Y is height.
/// Pathing reasons in XZ; <see cref="NearestPoint"/> returns the mesh Y so bots sit on the surface.</para>
/// </summary>
public sealed class NavMesh
{
    /// <summary>Flat vertex array: vertex i is (V[i*3], V[i*3+1], V[i*3+2]) = (x, y, z).</summary>
    private readonly float[] _verts;
    /// <summary>Flat triangle array: triangle t uses vertices T[t*3], T[t*3+1], T[t*3+2].</summary>
    private readonly int[] _tris;
    /// <summary>Per-triangle neighbor array: N[t*3 + e] is the triangle across edge e of t, or -1 if none.</summary>
    private readonly int[] _neighbors;

    public int VertexCount => _verts.Length / 3;
    public int TriangleCount => _tris.Length / 3;

    /// <param name="verts">Flat XYZ vertices (length multiple of 3).</param>
    /// <param name="tris">Flat triangle vertex indices (length multiple of 3).</param>
    /// <param name="neighbors">Optional flat per-edge neighbor triangle indices (-1 = boundary). If null,
    /// adjacency is computed from shared edges. Length, if given, must equal <paramref name="tris"/>.</param>
    public NavMesh(float[] verts, int[] tris, int[]? neighbors = null)
    {
        ArgumentNullException.ThrowIfNull(verts);
        ArgumentNullException.ThrowIfNull(tris);
        if (verts.Length % 3 != 0) throw new ArgumentException("verts length must be a multiple of 3", nameof(verts));
        if (tris.Length % 3 != 0) throw new ArgumentException("tris length must be a multiple of 3", nameof(tris));
        if (neighbors is not null && neighbors.Length != tris.Length)
            throw new ArgumentException("neighbors length must equal tris length", nameof(neighbors));
        _verts = verts;
        _tris = tris;
        _neighbors = neighbors ?? BuildAdjacency(tris);
    }

    /// <summary>The three corner positions of triangle <paramref name="t"/>.</summary>
    public ((float x, float y, float z) a, (float x, float y, float z) b, (float x, float y, float z) c) Triangle(int t)
    {
        int i0 = _tris[t * 3], i1 = _tris[t * 3 + 1], i2 = _tris[t * 3 + 2];
        return (Vert(i0), Vert(i1), Vert(i2));
    }

    /// <summary>The triangle across edge <paramref name="edge"/> (0..2) of triangle <paramref name="t"/>, or -1.</summary>
    public int Neighbor(int t, int edge) => _neighbors[t * 3 + edge];

    /// <summary>The (x,y,z) of vertex <paramref name="i"/>. For serialization (<see cref="NavMeshFile"/>).</summary>
    public (float x, float y, float z) VertexAt(int i) => Vert(i);

    /// <summary>The three vertex indices of triangle <paramref name="t"/>. For serialization.</summary>
    public (int i0, int i1, int i2) TriangleIndices(int t) => (_tris[t * 3], _tris[t * 3 + 1], _tris[t * 3 + 2]);

    /// <summary>The XZ centroid of triangle <paramref name="t"/> (used as the A* node position).</summary>
    public (float x, float z) Centroid(int t)
    {
        var (a, b, c) = Triangle(t);
        return ((a.x + b.x + c.x) / 3f, (a.z + b.z + c.z) / 3f);
    }

    /// <summary>
    /// The point on the mesh nearest to (<paramref name="x"/>,<paramref name="z"/>) in XZ, with the mesh's
    /// interpolated Y — i.e. "snap this position onto the walkable surface." Returns false only for an empty
    /// mesh. If the point is inside a triangle, that triangle's plane gives Y; otherwise the nearest
    /// triangle's clamped point is used.
    /// </summary>
    public bool NearestPoint(float x, float z, out (float x, float y, float z) point, out int triangle)
    {
        point = default; triangle = -1;
        if (TriangleCount == 0) return false;

        double bestDist = double.MaxValue;
        for (int t = 0; t < TriangleCount; t++)
        {
            var (a, b, c) = Triangle(t);
            if (PointInTriangleXZ(x, z, a, b, c))
            {
                point = (x, InterpolateY(x, z, a, b, c), z);
                triangle = t;
                return true;   // inside a triangle is the best possible match
            }
            // Track the nearest centroid as a fallback for points off the mesh.
            double dx = x - (a.x + b.x + c.x) / 3f, dz = z - (a.z + b.z + c.z) / 3f;
            double d = dx * dx + dz * dz;
            if (d < bestDist) { bestDist = d; triangle = t; point = ((a.x + b.x + c.x) / 3f, (a.y + b.y + c.y) / 3f, (a.z + b.z + c.z) / 3f); }
        }
        return true;
    }

    /// <summary>The walkable height at (x,z) if it lies on the mesh, else null (off-mesh).</summary>
    public float? SampleHeight(float x, float z)
    {
        for (int t = 0; t < TriangleCount; t++)
        {
            var (a, b, c) = Triangle(t);
            if (PointInTriangleXZ(x, z, a, b, c)) return InterpolateY(x, z, a, b, c);
        }
        return null;
    }

    private (float x, float y, float z) Vert(int i) => (_verts[i * 3], _verts[i * 3 + 1], _verts[i * 3 + 2]);

    // --- geometry helpers (XZ plane) -------------------------------------------------------------

    internal static bool PointInTriangleXZ(float px, float pz,
        (float x, float y, float z) a, (float x, float y, float z) b, (float x, float y, float z) c)
    {
        // Barycentric sign test in XZ. Tolerant of either winding.
        float d1 = Sign(px, pz, a.x, a.z, b.x, b.z);
        float d2 = Sign(px, pz, b.x, b.z, c.x, c.z);
        float d3 = Sign(px, pz, c.x, c.z, a.x, a.z);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0;
        bool pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
        static float Sign(float px, float pz, float ax, float az, float bx, float bz)
            => (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
    }

    internal static float InterpolateY(float px, float pz,
        (float x, float y, float z) a, (float x, float y, float z) b, (float x, float y, float z) c)
    {
        // Barycentric interpolation of Y over the triangle's XZ projection.
        float det = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
        if (MathF.Abs(det) < 1e-9f) return (a.y + b.y + c.y) / 3f;   // degenerate in XZ
        float l1 = ((b.z - c.z) * (px - c.x) + (c.x - b.x) * (pz - c.z)) / det;
        float l2 = ((c.z - a.z) * (px - c.x) + (a.x - c.x) * (pz - c.z)) / det;
        float l3 = 1f - l1 - l2;
        return l1 * a.y + l2 * b.y + l3 * c.y;
    }

    /// <summary>Computes per-edge triangle adjacency by matching shared undirected vertex-index edges.</summary>
    private static int[] BuildAdjacency(int[] tris)
    {
        int triCount = tris.Length / 3;
        var neighbors = new int[tris.Length];
        Array.Fill(neighbors, -1);
        // Map an undirected edge (min,max vertex index) → (triangle, edgeSlot) of the first owner seen.
        var edgeOwner = new Dictionary<(int, int), (int tri, int edge)>(tris.Length);
        for (int t = 0; t < triCount; t++)
            for (int e = 0; e < 3; e++)
            {
                int v0 = tris[t * 3 + e], v1 = tris[t * 3 + (e + 1) % 3];
                var key = v0 < v1 ? (v0, v1) : (v1, v0);
                if (edgeOwner.TryGetValue(key, out var owner))
                {
                    neighbors[t * 3 + e] = owner.tri;
                    neighbors[owner.tri * 3 + owner.edge] = t;
                }
                else edgeOwner[key] = (t, e);
            }
        return neighbors;
    }
}
