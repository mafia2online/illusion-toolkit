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
}
