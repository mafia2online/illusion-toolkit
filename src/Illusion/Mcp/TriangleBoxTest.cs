using System.Numerics;

namespace Illusion.Mcp;

/// <summary>
/// Whether a triangle touches an axis-aligned box — the separating-axis test (Akenine-Möller). A mesh's
/// bounding box cannot answer "is there geometry in this volume": a building's box swallows every room
/// inside it and a terrain tile's box swallows the street. Its triangles can.
/// </summary>
internal static class TriangleBoxTest
{
    public static bool Overlaps(Vector3 a, Vector3 b, Vector3 c, Vector3 boxMin, Vector3 boxMax)
    {
        Vector3 center = (boxMin + boxMax) * 0.5f;
        Vector3 half = (boxMax - boxMin) * 0.5f;
        Vector3 v0 = a - center, v1 = b - center, v2 = c - center;

        // The box's own three axes: the triangle's extent against the box's.
        if (MathF.Min(v0.X, MathF.Min(v1.X, v2.X)) > half.X || MathF.Max(v0.X, MathF.Max(v1.X, v2.X)) < -half.X) return false;
        if (MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y)) > half.Y || MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y)) < -half.Y) return false;
        if (MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z)) > half.Z || MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z)) < -half.Z) return false;

        // The triangle's plane.
        Vector3 e0 = v1 - v0, e1 = v2 - v1, e2 = v0 - v2;
        Vector3 normal = Vector3.Cross(e0, e1);
        if (MathF.Abs(Vector3.Dot(normal, v0)) > Reach(normal, half)) return false;

        // The nine cross products of a box axis with a triangle edge.
        ReadOnlySpan<Vector3> edges = [e0, e1, e2];
        ReadOnlySpan<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        foreach (Vector3 edge in edges)
        {
            foreach (Vector3 boxAxis in axes)
            {
                Vector3 axis = Vector3.Cross(boxAxis, edge);
                float p0 = Vector3.Dot(axis, v0), p1 = Vector3.Dot(axis, v1), p2 = Vector3.Dot(axis, v2);
                float reach = Reach(axis, half);
                if (MathF.Min(p0, MathF.Min(p1, p2)) > reach || MathF.Max(p0, MathF.Max(p1, p2)) < -reach) return false;
            }
        }
        return true;
    }

    // How far the box reaches along an axis, from its centre.
    private static float Reach(Vector3 axis, Vector3 half) =>
        half.X * MathF.Abs(axis.X) + half.Y * MathF.Abs(axis.Y) + half.Z * MathF.Abs(axis.Z);
}
