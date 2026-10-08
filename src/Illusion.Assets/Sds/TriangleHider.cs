using System.Numerics;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;

namespace Illusion.Assets.Sds;

/// <summary>
/// Takes triangles out of a mesh without rebuilding it: a triangle is "hidden" by pointing its three indices
/// at one vertex, which the rasterizer draws as nothing.
///
/// <para>
/// This is how an opening is cut into a STOCK mesh — the painted door of a building one wants to walk
/// through. A rebuild through Blender cannot be trusted with that: a stock facade carries channels Blender
/// never sees (the shadow/occlusion-map UVs behind <c>ShadowTexture</c>), and vertices re-created from a
/// Blender mesh get wrong values for them — the whole facade comes back with black patches. Here no vertex
/// is touched and none is added or removed: the vertex buffer, the material ranges and every index outside
/// the hidden triangles stay exactly as shipped, on every level of detail.
/// </para>
/// </summary>
public static class TriangleHider
{
    /// <summary>One triangle found: its level of detail, the material of its slot and its corners in world space.</summary>
    public sealed record Triangle(int Lod, string Material, Vector3 A, Vector3 B, Vector3 C);

    /// <summary>One index buffer as it is and as it would be with the triangles hidden. Nothing is applied.</summary>
    public sealed record Change(IndexBuffer Buffer, uint[] Before, uint[] After);

    /// <summary>What a box takes out of a mesh.</summary>
    public sealed record Plan(IReadOnlyList<Triangle> Triangles, IReadOnlyList<Change> Changes);

    /// <summary>
    /// The triangles of <paramref name="mesh"/> whose three corners all lie inside the world-space box, on every
    /// level of detail, and the index buffers with them hidden. A triangle already hidden is not found again.
    /// </summary>
    /// <param name="world">The matrix the mesh is drawn at.</param>
    /// <param name="material">Only triangles of slots whose material name contains this, or null for any.</param>
    public static Plan Find(FrameObjectSingleMesh mesh, Matrix4x4 world, Vector3 min, Vector3 max, string? material = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        const float Slack = 1e-3f;
        Vector3 lo = Vector3.Min(min, max) - new Vector3(Slack);
        Vector3 hi = Vector3.Max(min, max) + new Vector3(Slack);
        bool Inside(Vector3 p) => p.X >= lo.X && p.X <= hi.X && p.Y >= lo.Y && p.Y <= hi.Y && p.Z >= lo.Z && p.Z <= hi.Z;

        var triangles = new List<Triangle>();
        // By buffer, so two levels drawing from one buffer are edited as one.
        var working = new Dictionary<ulong, (IndexBuffer Buffer, uint[] Before, uint[] After)>();
        int levels = mesh.Geometry?.LOD?.Length ?? 0;
        List<MaterialStruct[]>? slotsByLod = mesh.Material?.Materials;
        for (int lod = 0; lod < levels; lod++)
        {
            DecodedMesh? decoded = SdsMeshLoader.DecodeLod(mesh, lod);
            IndexBuffer? buffer = mesh.GetIndexBuffer(lod);
            if (decoded == null || buffer == null || slotsByLod == null || lod >= slotsByLod.Count) continue;
            if (!working.TryGetValue(buffer.Hash, out (IndexBuffer Buffer, uint[] Before, uint[] After) entry))
            {
                uint[] before = buffer.GetData();
                working[buffer.Hash] = entry = (buffer, before, (uint[])before.Clone());
            }

            uint[] indices = entry.After;
            Vector3[] positions = decoded.Positions;
            foreach (MaterialStruct slot in slotsByLod[lod])
            {
                string name = slot.MaterialName ?? "";
                if (material != null && !name.Contains(material, StringComparison.OrdinalIgnoreCase)) continue;
                int end = Math.Min(slot.StartIndex + (slot.NumFaces * 3), indices.Length);
                for (int i = slot.StartIndex; i + 2 < end; i += 3)
                {
                    uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    if (a == b && b == c) continue;
                    if (a >= positions.Length || b >= positions.Length || c >= positions.Length) continue;
                    Vector3 pa = Vector3.Transform(positions[a], world);
                    Vector3 pb = Vector3.Transform(positions[b], world);
                    Vector3 pc = Vector3.Transform(positions[c], world);
                    if (!Inside(pa) || !Inside(pb) || !Inside(pc)) continue;
                    triangles.Add(new Triangle(lod, name, pa, pb, pc));
                    indices[i + 1] = a;
                    indices[i + 2] = a;
                }
            }
        }

        List<Change> changes = [.. working.Values
            .Where(w => !w.Before.AsSpan().SequenceEqual(w.After))
            .Select(w => new Change(w.Buffer, w.Before, w.After))];
        return new Plan(triangles, changes);
    }

    /// <summary>A triangle of the mesh's FIRST level of detail - the one drawn up close, and so the one a click
    /// lands on - named by where its three indices stand in that level's index buffer.</summary>
    public sealed record Picked(int Index, float Distance, Triangle Triangle);

    /// <summary>
    /// The triangle of the first level of detail a world-space ray meets first, from either side, or null when
    /// it meets none. A triangle already hidden is not met.
    /// </summary>
    public static Picked? Pick(FrameObjectSingleMesh mesh, Matrix4x4 world, Vector3 origin, Vector3 direction)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        DecodedMesh? decoded = (mesh.Geometry?.LOD?.Length ?? 0) > 0 ? SdsMeshLoader.DecodeLod(mesh, 0) : null;
        IndexBuffer? buffer = decoded == null ? null : mesh.GetIndexBuffer(0);
        if (decoded == null || buffer == null || mesh.Material?.Materials is not { Count: > 0 } slotsByLod) return null;

        uint[] indices = buffer.GetData();
        Vector3[] positions = decoded.Positions;
        Picked? best = null;
        foreach (MaterialStruct slot in slotsByLod[0])
        {
            int end = Math.Min(slot.StartIndex + (slot.NumFaces * 3), indices.Length);
            for (int i = slot.StartIndex; i + 2 < end; i += 3)
            {
                uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
                if ((a == b && b == c) || a >= positions.Length || b >= positions.Length || c >= positions.Length) continue;
                Vector3 pa = Vector3.Transform(positions[a], world);
                Vector3 pb = Vector3.Transform(positions[b], world);
                Vector3 pc = Vector3.Transform(positions[c], world);
                if (RayMeets(origin, direction, pa, pb, pc, out float t) && (best == null || t < best.Distance))
                {
                    best = new Picked(i, t, new Triangle(0, slot.MaterialName ?? "", pa, pb, pc));
                }
            }
        }
        return best;
    }

    /// <summary>
    /// What hiding triangles picked one by one takes out of a mesh: those triangles of the first level of detail
    /// (<see cref="Picked.Index"/>), and on every other level the triangles that LIE ON them - their corners, the
    /// middles of their edges and their own middle within <paramref name="near"/> metres of a picked triangle. A level cut coarser than the first has
    /// triangles that reach past what was picked; those stay, and the opening closes at the distance that level
    /// is drawn from - the count per level says so before anything is hidden.
    /// </summary>
    public static Plan FindPicked(FrameObjectSingleMesh mesh, Matrix4x4 world, IReadOnlyCollection<int> picked, float near = 0.05f)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(picked);
        var triangles = new List<Triangle>();
        var working = new Dictionary<ulong, (IndexBuffer Buffer, uint[] Before, uint[] After)>();
        int levels = mesh.Geometry?.LOD?.Length ?? 0;
        List<MaterialStruct[]>? slotsByLod = mesh.Material?.Materials;
        var chosen = new List<Triangle>();
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        for (int lod = 0; lod < levels && picked.Count > 0; lod++)
        {
            DecodedMesh? decoded = SdsMeshLoader.DecodeLod(mesh, lod);
            IndexBuffer? buffer = mesh.GetIndexBuffer(lod);
            if (decoded == null || buffer == null || slotsByLod == null || lod >= slotsByLod.Count) continue;
            if (!working.TryGetValue(buffer.Hash, out (IndexBuffer Buffer, uint[] Before, uint[] After) entry))
            {
                uint[] before = buffer.GetData();
                working[buffer.Hash] = entry = (buffer, before, (uint[])before.Clone());
            }

            uint[] indices = entry.After;
            Vector3[] positions = decoded.Positions;
            foreach (MaterialStruct slot in slotsByLod[lod])
            {
                int end = Math.Min(slot.StartIndex + (slot.NumFaces * 3), indices.Length);
                for (int i = slot.StartIndex; i + 2 < end; i += 3)
                {
                    if (lod == 0 && !picked.Contains(i)) continue;
                    uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    if ((a == b && b == c) || a >= positions.Length || b >= positions.Length || c >= positions.Length) continue;
                    Vector3 pa = Vector3.Transform(positions[a], world);
                    Vector3 pb = Vector3.Transform(positions[b], world);
                    Vector3 pc = Vector3.Transform(positions[c], world);
                    var triangle = new Triangle(lod, slot.MaterialName ?? "", pa, pb, pc);
                    if (lod == 0)
                    {
                        chosen.Add(triangle);
                        lo = Vector3.Min(lo, Vector3.Min(pa, Vector3.Min(pb, pc)));
                        hi = Vector3.Max(hi, Vector3.Max(pa, Vector3.Max(pb, pc)));
                    }
                    else if (!LiesOn(pa) || !LiesOn(pb) || !LiesOn(pc)
                        // corners on the picks do not make the triangle lie on them: one spanning a door from
                        // frame to frame has all three on the frame. Its middle and its edges are asked too.
                        || !LiesOn((pa + pb + pc) / 3f) || !LiesOn((pa + pb) * 0.5f) || !LiesOn((pb + pc) * 0.5f) || !LiesOn((pc + pa) * 0.5f))
                    {
                        continue;
                    }
                    triangles.Add(triangle);
                    indices[i + 1] = a;
                    indices[i + 2] = a;
                }
            }
        }

        bool LiesOn(Vector3 p)
        {
            if (p.X < lo.X - near || p.Y < lo.Y - near || p.Z < lo.Z - near || p.X > hi.X + near || p.Y > hi.Y + near || p.Z > hi.Z + near) return false;
            foreach (Triangle t in chosen)
            {
                if (Vector3.DistanceSquared(p, Closest(p, t.A, t.B, t.C)) <= near * near) return true;
            }
            return false;
        }

        List<Change> changes = [.. working.Values
            .Where(w => !w.Before.AsSpan().SequenceEqual(w.After))
            .Select(w => new Change(w.Buffer, w.Before, w.After))];
        return new Plan(triangles, changes);
    }

    // Moller-Trumbore, from either side: the editor draws a mesh without culling, so what is seen is what is met.
    private static bool RayMeets(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        Vector3 ab = b - a, ac = c - a;
        Vector3 p = Vector3.Cross(direction, ac);
        float det = Vector3.Dot(ab, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        Vector3 s = origin - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        Vector3 q = Vector3.Cross(s, ab);
        float v = Vector3.Dot(direction, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(ac, q) * inv;
        return t > 1e-4f;
    }

    // The point of triangle abc nearest to p (Ericson, Real-Time Collision Detection 5.1.5).
    private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;
        float vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + (ab * (d1 / (d1 - d3)));
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;
        float vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + (ac * (d2 / (d2 - d6)));
        float va = (d3 * d6) - (d5 * d4);
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))));
        float denom = 1f / (va + vb + vc);
        return a + (ab * (vb * denom)) + (ac * (vc * denom));
    }
}
