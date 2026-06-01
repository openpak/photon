namespace BlackIce.Server.Core.Navigation;

/// <summary>
/// A* pathfinding over a <see cref="NavMesh"/>'s triangle adjacency graph, returning a list of XZ waypoints
/// from start to goal that stay on the walkable surface. This is what lets a bot route AROUND a wall
/// instead of clipping through it: the path only ever crosses shared triangle edges, so every segment lies
/// on the mesh.
///
/// <para>The search is over triangle centroids (each triangle is a node; edges connect to edge-neighbors),
/// then the corridor of triangles is reduced to waypoints. v1 keeps it simple — one waypoint per triangle
/// centroid in the corridor, plus the exact goal — which already produces walkable, non-clipping motion;
/// a full string-pulling funnel is a later refinement (noted in the spec).</para>
/// </summary>
public static class NavMeshPath
{
    /// <summary>
    /// Finds a walkable path from (<paramref name="fromX"/>,<paramref name="fromZ"/>) to
    /// (<paramref name="toX"/>,<paramref name="toZ"/>). Returns the ordered XZ waypoints (excluding the
    /// start, including the goal snapped to the mesh), or an empty list if either end is off the mesh or no
    /// corridor connects them. Y is taken from the mesh at each waypoint.
    /// </summary>
    public static IReadOnlyList<(float x, float y, float z)> Find(NavMesh mesh, float fromX, float fromZ, float toX, float toZ)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!mesh.NearestPoint(fromX, fromZ, out var startPt, out int startTri)) return Array.Empty<(float, float, float)>();
        if (!mesh.NearestPoint(toX, toZ, out var goalPt, out int goalTri)) return Array.Empty<(float, float, float)>();

        if (startTri == goalTri)
            return new[] { goalPt };   // same triangle — walk straight to the goal

        int[] cameFrom = AStar(mesh, startTri, goalTri);
        if (cameFrom[goalTri] == -2 && startTri != goalTri)
            return Array.Empty<(float, float, float)>();   // unreachable

        // Reconstruct the triangle corridor goal→start, then reverse to start→goal.
        var corridor = new List<int>();
        for (int t = goalTri; t != -1; t = cameFrom[t])
        {
            corridor.Add(t);
            if (t == startTri) break;
        }
        corridor.Reverse();

        // Waypoints: each corridor triangle's centroid (skipping the start triangle the bot is already in),
        // finishing at the exact goal point. Centroids sit inside the walkable surface, so segments between
        // consecutive centroids cross only shared edges → stay on the mesh.
        var waypoints = new List<(float x, float y, float z)>(corridor.Count);
        for (int i = 1; i < corridor.Count; i++)
        {
            int t = corridor[i];
            var (cx, cz) = mesh.Centroid(t);
            float y = mesh.SampleHeight(cx, cz) ?? goalPt.y;
            waypoints.Add((cx, y, cz));
        }
        // Replace the final centroid with the precise goal (same triangle), or append it.
        if (waypoints.Count > 0) waypoints[^1] = goalPt; else waypoints.Add(goalPt);
        return waypoints;
    }

    /// <summary>A* over triangle nodes; returns a came-from array (parent triangle, -1 at start, -2 unvisited).</summary>
    private static int[] AStar(NavMesh mesh, int start, int goal)
    {
        int n = mesh.TriangleCount;
        var cameFrom = new int[n];
        Array.Fill(cameFrom, -2);
        var g = new double[n];
        Array.Fill(g, double.MaxValue);

        var (gx, gz) = mesh.Centroid(goal);
        double H(int t) { var (x, z) = mesh.Centroid(t); double dx = x - gx, dz = z - gz; return Math.Sqrt(dx * dx + dz * dz); }

        // Simple binary-heap-free open set: a sorted-by-f scan. NavMeshes here are modest (hundreds–low
        // thousands of tris) and pathing runs at most a few times per maintenance tick, so an O(n) pop is
        // fine and keeps the code dependency-free. Swap in a heap if a profile ever flags it.
        var open = new PriorityQueue<int, double>();
        g[start] = 0; cameFrom[start] = -1;
        open.Enqueue(start, H(start));

        while (open.TryDequeue(out int cur, out _))
        {
            if (cur == goal) break;
            var (cx, cz) = mesh.Centroid(cur);
            for (int e = 0; e < 3; e++)
            {
                int nb = mesh.Neighbor(cur, e);
                if (nb < 0) continue;
                var (nx, nz) = mesh.Centroid(nb);
                double step = Math.Sqrt((nx - cx) * (nx - cx) + (nz - cz) * (nz - cz));
                double tentative = g[cur] + step;
                if (tentative < g[nb])
                {
                    g[nb] = tentative;
                    cameFrom[nb] = cur;
                    open.Enqueue(nb, tentative + H(nb));
                }
            }
        }
        return cameFrom;
    }
}
