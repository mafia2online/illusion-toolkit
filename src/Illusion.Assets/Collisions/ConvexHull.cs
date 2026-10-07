using System.Numerics;

namespace Illusion.Assets.Collisions;

/// <summary>
/// The convex hull of a point cloud, as triangles wound outward — what a simplified collision hull is cooked
/// from.
/// <para>
/// Incremental: a starting tetrahedron, then every point outside the current hull replaces the faces it can
/// see with a fan to their horizon. The points are first snapped to a grid a fortieth of the object's size,
/// which is what keeps the result to dozens of triangles rather than hundreds: a hull is a stand-in for the
/// object, and detail finer than that is detail a player's capsule never feels. Arithmetic in doubles, with a
/// tolerance scaled to the object, so coplanar faces do not tear the hull.
/// </para>
/// </summary>
public static class ConvexHull
{
    // How finely the points are snapped, as a fraction of the object's largest extent.
    private const double GridFraction = 1.0 / 40.0;

    /// <summary>
    /// The hull's vertices and its triangles (three indices each, wound so the normal points out). Null when
    /// the points do not span a volume — a flat or a thin object has no convex hull, only a box.
    /// </summary>
    public static (Vector3[] Vertices, int[] Triangles)? Build(IReadOnlyList<Vector3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 4) return null;

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector3 p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        double extent = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z));
        if (extent <= 1e-6) return null;
        double cell = extent * GridFraction;
        double eps = extent * 1e-6;

        // Snapped and unique.
        var unique = new Dictionary<(long, long, long), V>();
        foreach (Vector3 p in points)
        {
            var key = ((long)Math.Round(p.X / cell), (long)Math.Round(p.Y / cell), (long)Math.Round(p.Z / cell));
            unique.TryAdd(key, new V(key.Item1 * cell, key.Item2 * cell, key.Item3 * cell));
        }
        List<V> pts = [.. unique.Values];
        if (pts.Count < 4) return null;

        if (!Tetrahedron(pts, eps, out int i0, out int i1, out int i2, out int i3)) return null;
        V centre = (pts[i0] + pts[i1] + pts[i2] + pts[i3]) * 0.25;
        var faces = new List<Face>();
        void AddFace(int a, int b, int c)
        {
            var f = new Face(a, b, c, pts);
            if (f.Distance(centre) > 0) f = new Face(a, c, b, pts); // the inside must be behind every face
            faces.Add(f);
        }
        AddFace(i0, i1, i2);
        AddFace(i0, i1, i3);
        AddFace(i0, i2, i3);
        AddFace(i1, i2, i3);

        for (int p = 0; p < pts.Count; p++)
        {
            if (p == i0 || p == i1 || p == i2 || p == i3) continue;
            var visible = faces.Where(f => f.Distance(pts[p]) > eps).ToList();
            if (visible.Count == 0) continue; // inside

            // The horizon: edges of the visible faces whose other side is not visible.
            var edges = new HashSet<(int, int)>();
            foreach (Face f in visible)
            {
                foreach ((int u, int v) in f.Edges) edges.Add((u, v));
            }
            var horizon = edges.Where(e => !edges.Contains((e.Item2, e.Item1))).ToList();
            foreach (Face f in visible) faces.Remove(f);
            foreach ((int u, int v) in horizon)
            {
                // Same winding as the face it replaces, so the new face faces out as well.
                faces.Add(new Face(u, v, p, pts));
            }
        }

        // Only the points the hull uses, renumbered.
        var remap = new Dictionary<int, int>();
        var vertices = new List<Vector3>();
        var triangles = new List<int>(faces.Count * 3);
        foreach (Face f in faces)
        {
            foreach (int i in new[] { f.A, f.B, f.C })
            {
                if (!remap.TryGetValue(i, out int j))
                {
                    remap[i] = j = vertices.Count;
                    vertices.Add(new Vector3((float)pts[i].X, (float)pts[i].Y, (float)pts[i].Z));
                }
                triangles.Add(j);
            }
        }
        return ([.. vertices], [.. triangles]);
    }

    /// <summary>The box the points fit in, axis-aligned in their own space: 8 corners, 12 outward triangles.</summary>
    public static (Vector3[] Vertices, int[] Triangles)? Box(IReadOnlyList<Vector3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) return null;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector3 p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        // A flat object (a rug, a sign) still gets something a capsule can stand on or bump into.
        Vector3 thin = Vector3.Max(max - min, new Vector3(0.02f)) - (max - min);
        min -= thin / 2;
        max += thin / 2;
        Vector3[] v =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];
        int[] t =
        [
            0, 2, 1, 0, 3, 2, // bottom (−Z)
            4, 5, 6, 4, 6, 7, // top (+Z)
            0, 1, 5, 0, 5, 4, // −Y
            2, 3, 7, 2, 7, 6, // +Y
            1, 2, 6, 1, 6, 5, // +X
            3, 0, 4, 3, 4, 7, // −X
        ];
        return (v, t);
    }

    // Four points that span a volume: the two farthest apart along an axis, the one farthest from their line,
    // the one farthest from the plane of the three.
    private static bool Tetrahedron(List<V> pts, double eps, out int a, out int b, out int c, out int d)
    {
        a = b = c = d = -1;
        double best = -1;
        for (int axis = 0; axis < 3; axis++)
        {
            int lo = 0, hi = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                if (pts[i][axis] < pts[lo][axis]) lo = i;
                if (pts[i][axis] > pts[hi][axis]) hi = i;
            }
            double span = (pts[hi] - pts[lo]).Length;
            if (span > best) { best = span; a = lo; b = hi; }
        }
        if (best <= eps) return false;

        V ab = pts[b] - pts[a];
        best = -1;
        for (int i = 0; i < pts.Count; i++)
        {
            double dist = V.Cross(ab, pts[i] - pts[a]).Length;
            if (dist > best) { best = dist; c = i; }
        }
        if (best <= eps) return false;

        V n = V.Cross(ab, pts[c] - pts[a]).Normalized;
        best = -1;
        for (int i = 0; i < pts.Count; i++)
        {
            double dist = Math.Abs(V.Dot(n, pts[i] - pts[a]));
            if (dist > best) { best = dist; d = i; }
        }
        return best > eps;
    }

    private sealed class Face
    {
        public readonly int A, B, C;
        private readonly V _normal;
        private readonly double _offset;

        public Face(int a, int b, int c, List<V> pts)
        {
            A = a;
            B = b;
            C = c;
            _normal = V.Cross(pts[b] - pts[a], pts[c] - pts[a]).Normalized;
            _offset = V.Dot(_normal, pts[a]);
        }

        /// <summary>Signed distance of a point in front of the face (positive outside).</summary>
        public double Distance(V p) => V.Dot(_normal, p) - _offset;

        public IEnumerable<(int, int)> Edges
        {
            get
            {
                yield return (A, B);
                yield return (B, C);
                yield return (C, A);
            }
        }
    }

    private readonly record struct V(double X, double Y, double Z)
    {
        public double this[int axis] => axis == 0 ? X : axis == 1 ? Y : Z;
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public V Normalized => Length > 0 ? this * (1 / Length) : this;
        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V operator *(V a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static V Cross(V a, V b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
}
