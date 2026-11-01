using System.Numerics;
using Illusion.Rendering.Gpu;

namespace Illusion.Rendering.Scene;

/// <summary>
/// Viewport ray-picking: builds a world-space ray from a screen pixel and finds the nearest mesh under it.
/// Broad phase = ray vs world AABB (with pruning); narrow phase = Möller–Trumbore over the mesh triangles
/// (transformed to world by the mesh's current world matrix). Pure math — headless-testable, no D3D.
/// </summary>
public static class Picking
{
    /// <summary>
    /// Screen pixel → world ray. Uses the row-vector convention throughout (clip = worldRow · viewProj),
    /// so unprojection is clipRow · inverse(viewProj). D3D NDC z ∈ [0,1]: z=0 is the near plane, z=1 the far.
    /// <para>
    /// The ray starts on the NEAR PLANE, not at the camera. Under a perspective projection the two are the same
    /// line — every ray converges on the eye, so starting half a metre along it changes nothing but where t is
    /// measured from. Under a parallel one they are not: there the direction is the same for every pixel and it
    /// is the ORIGIN that differs, so a ray anchored at the camera would make the whole screen pick along one
    /// line. <paramref name="cameraPos"/> is only the answer for a projection that cannot be inverted at all.
    /// </para>
    /// </summary>
    public static (Vector3 Origin, Vector3 Dir) BuildRay(
        Matrix4x4 viewProj, Vector3 cameraPos, double screenX, double screenY, double width, double height)
    {
        float nx = (float)(2.0 * screenX / width - 1.0);
        float ny = (float)(1.0 - 2.0 * screenY / height);

        if (!Matrix4x4.Invert(viewProj, out Matrix4x4 inv))
        {
            return (cameraPos, Vector3.UnitX);
        }

        Vector3 near = Unproject(new Vector4(nx, ny, 0f, 1f), inv);
        Vector3 far = Unproject(new Vector4(nx, ny, 1f, 1f), inv);
        Vector3 dir = far - near;
        float len = dir.Length();
        dir = len > 1e-8f ? dir / len : Vector3.UnitX;
        return (near, dir);
    }

    private static Vector3 Unproject(Vector4 clip, Matrix4x4 invViewProj)
    {
        Vector4 w = Vector4.Transform(clip, invViewProj); // row-vector: clip · inv
        return Math.Abs(w.W) > 1e-8f ? new Vector3(w.X, w.Y, w.Z) / w.W : new Vector3(w.X, w.Y, w.Z);
    }

    /// <summary>Ray vs axis-aligned box (slab method). <paramref name="tEnter"/> is clamped to 0 if the origin is inside.</summary>
    public static bool IntersectAabb(Vector3 origin, Vector3 dir, Vector3 min, Vector3 max, out float tEnter)
    {
        float t0 = float.NegativeInfinity, t1 = float.PositiveInfinity;
        for (int a = 0; a < 3; a++)
        {
            float o = Component(origin, a), d = Component(dir, a);
            float lo = Component(min, a), hi = Component(max, a);
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < lo || o > hi) { tEnter = 0f; return false; } // parallel and outside the slab
            }
            else
            {
                float inv = 1f / d;
                float ta = (lo - o) * inv;
                float tb = (hi - o) * inv;
                if (ta > tb) (ta, tb) = (tb, ta);
                if (ta > t0) t0 = ta;
                if (tb < t1) t1 = tb;
                if (t0 > t1) { tEnter = 0f; return false; }
            }
        }
        if (t1 < 0f) { tEnter = 0f; return false; } // box entirely behind the origin
        tEnter = MathF.Max(t0, 0f);
        return true;
    }

    /// <summary>Möller–Trumbore ray/triangle test (two-sided). <paramref name="t"/> is the ray distance to the hit.</summary>
    public static bool IntersectTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        Vector3 e1 = b - a;
        Vector3 e2 = c - a;
        Vector3 p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-8f) return false; // ray parallel to the triangle
        float invDet = 1f / det;
        Vector3 tv = o - a;
        float u = Vector3.Dot(tv, p) * invDet;
        if (u < 0f || u > 1f) return false;
        Vector3 q = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(d, q) * invDet;
        if (v < 0f || u + v > 1f) return false;
        float tt = Vector3.Dot(e2, q) * invDet;
        if (tt <= 1e-5f) return false; // behind the origin
        t = tt;
        return true;
    }

    /// <summary>
    /// Nearest pickable mesh hit by the ray, or null. Broad phase sorts candidates by AABB entry distance and
    /// prunes any whose box starts beyond the best confirmed triangle hit — so dense scenes stay responsive.
    /// </summary>
    public static GpuMesh? Pick(IReadOnlyList<GpuMesh> meshes, Vector3 origin, Vector3 dir, out float bestT)
    {
        bestT = float.PositiveInfinity;
        GpuMesh? best = null;

        // Broad phase: gather visible, pickable meshes whose world AABB the ray hits.
        var candidates = new List<(GpuMesh Mesh, float TEnter)>();
        foreach (GpuMesh m in meshes)
        {
            if (!m.Visible || m.Instanced || m.PickPositions == null || m.PickIndices == null) continue;
            m.RefreshPose();
            if (IntersectAabb(origin, dir, m.BoundsMin, m.BoundsMax, out float tEnter))
            {
                candidates.Add((m, tEnter));
            }
        }
        candidates.Sort(static (x, y) => x.TEnter.CompareTo(y.TEnter));

        // Narrow phase: exact triangles, nearest-first with pruning.
        foreach ((GpuMesh mesh, float tEnter) in candidates)
        {
            if (tEnter > bestT) break; // sorted — nothing further can beat the current hit
            Vector3[] pos = mesh.PickPositions!;
            uint[] idx = mesh.PickIndices!;
            Matrix4x4 world = mesh.World;
            for (int i = 0; i + 2 < idx.Length; i += 3)
            {
                Vector3 a = Vector3.Transform(pos[idx[i]], world);
                Vector3 b = Vector3.Transform(pos[idx[i + 1]], world);
                Vector3 c = Vector3.Transform(pos[idx[i + 2]], world);
                if (IntersectTriangle(origin, dir, a, b, c, out float t) && t < bestT)
                {
                    bestT = t;
                    best = mesh;
                }
            }
        }
        return best;
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}
