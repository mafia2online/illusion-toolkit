using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Navigation;

public sealed partial class Mafia3NavMeshFile
{
    /// <summary>Flips one internal diagonal between compatible, coplanar triangles in one cluster.
    /// All counts and allocations remain fixed; the native graph must subsequently be recooked.</summary>
    public Mafia3NavigationCookPlan PrepareEdgeFlip(int edgeIndex)
    {
        var plan = PrepareInteriorDeformation(new Dictionary<int, Vector3>());
        return PrepareEdgeFlip(plan, new TranslationLayout(_bytes), edgeIndex, null, true);
    }

    /// <summary>Lists bounded, format-compatible diagonal candidates without cooking or changing bytes.
    /// Native baseline reproduction is still checked when the edit is requested.</summary>
    public IReadOnlyList<int> EdgeFlipCandidates(int limit = 32)
    {
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        var plan = PrepareInteriorDeformation(new Dictionary<int, Vector3>()); var layout = new TranslationLayout(_bytes);
        var owners = Enumerable.Repeat(-1, plan.EdgeCount).ToArray(); var candidates = new List<int>();
        for (int f = 0; f < plan.FaceCount; f++)
        {
            int face = plan.Faces + f * 16, first = layout.Int(face), count = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(face + 8));
            for (int e = first; e < first + count; e++)
            {
                if (owners[e] != -1) throw new InvalidDataException("Navigation face edge ranges overlap.");
                owners[e] = f;
            }
        }
        for (int e = 0; e < plan.EdgeCount && candidates.Count < limit; e++)
        {
            int opposite = layout.Int(plan.Edges + e * 20 + 8);
            if (opposite <= e || opposite >= plan.EdgeCount || owners[e] < 0 || owners[opposite] < 0) continue;
            try { _ = PrepareEdgeFlip(plan, layout, e, owners, false); candidates.Add(e); }
            catch (NotSupportedException) { }
        }
        return candidates;
    }

    private Mafia3NavigationCookPlan PrepareEdgeFlip(Mafia3NavigationCookPlan plan, TranslationLayout layout,
        int edgeIndex, int[]? owners, bool write)
    {
        var mesh = layout.Variants[0]; var graph = layout.Variants[1];
        if ((uint)edgeIndex >= plan.EdgeCount) throw new ArgumentOutOfRangeException(nameof(edgeIndex));
        int At(int edge) => plan.Edges + edge * 20;
        int opposite = layout.Int(At(edgeIndex) + 8);
        if ((uint)opposite >= plan.EdgeCount || layout.Int(At(opposite) + 8) != edgeIndex)
            throw new NotSupportedException("Select a reciprocal internal navigation edge.");
        int Face(int edge)
        {
            if (owners is not null) return owners[edge] >= 0 ? owners[edge] : throw new InvalidDataException("Navigation edge has no owning face.");
            for (int f = 0; f < plan.FaceCount; f++)
            {
                int at = plan.Faces + f * 16, first = layout.Int(at), count = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(at + 8));
                if (edge >= first && edge < first + count) return f;
            }
            throw new InvalidDataException("Navigation edge has no owning face.");
        }
        int faceA = Face(edgeIndex), faceB = Face(opposite);
        if (faceA == faceB || layout.Int(At(edgeIndex) + 12) != faceB || layout.Int(At(opposite) + 12) != faceA)
            throw new InvalidDataException("Navigation diagonal face links disagree.");
        int fa = plan.Faces + faceA * 16, fb = plan.Faces + faceB * 16;
        ushort U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(p));
        if (U16(fa + 8) != 3 || U16(fb + 8) != 3 || U16(fa + 10) != 0 || U16(fb + 10) != 0 ||
            U16(fa + 12) != U16(fb + 12) || U16(fa + 14) != U16(fb + 14))
            throw new NotSupportedException("Flip requires two triangles in the same cluster without user edges and with matching flags.");
        var faceData = layout.Array(mesh, 80, 4); int faceStride = layout.Int(mesh.Object + 112);
        if (!_bytes.AsSpan(faceData.Address + faceA * faceStride * 4, faceStride * 4).SequenceEqual(
            _bytes.AsSpan(faceData.Address + faceB * faceStride * 4, faceStride * 4)))
            throw new NotSupportedException("Diagonal faces have different navigation user data.");
        int[] Slots(int first, int selected) => [selected, first + (selected - first + 1) % 3, first + (selected - first + 2) % 3];
        int[] aa = Slots(layout.Int(fa), edgeIndex), bb = Slots(layout.Int(fb), opposite);
        // hkaiStreamingSet: two u32 UIDs, then three 16-byte arrays. Mesh connections
        // contain local face/edge and opposite face/edge; graph connections begin with
        // local/opposite cluster IDs. A boundary face/cluster must remain untouched.
        foreach (var sets in new[] { layout.Array(mesh, 64, 56), layout.Array(graph, 104, 56) })
            for (int s = 0; s < sets.Count; s++)
            {
                int set = sets.Address + s * 56;
                var connections = layout.ArrayAt(set + 8, 16);
                for (int i = 0; i < connections.Count; i++)
                {
                    int connection = connections.Address + i * 16, localFace = layout.Int(connection), localEdge = layout.Int(connection + 4);
                    if (localFace == faceA || localFace == faceB || aa.Contains(localEdge) || bb.Contains(localEdge))
                        throw new NotSupportedException("Diagonal patch touches a streaming face or edge.");
                }
                var graphConnections = layout.ArrayAt(set + 24, 16);
                for (int i = 0; i < graphConnections.Count; i++)
                    if (layout.Int(graphConnections.Address + i * 16) == U16(fa + 12))
                        throw new NotSupportedException("Diagonal cluster has an external streaming graph connection.");
                if (layout.ArrayAt(set + 40, 8).Count != 0)
                    throw new NotSupportedException("Navigation volume streaming connections require a separate topology cook.");
            }
        int a = layout.Int(At(aa[0])), b = layout.Int(At(aa[0]) + 4), c = layout.Int(At(aa[1]) + 4), d = layout.Int(At(bb[1]) + 4);
        if (new[] { a, b, c, d }.Distinct().Count() != 4 || layout.Int(At(bb[0])) != b || layout.Int(At(bb[0]) + 4) != a)
            throw new NotSupportedException("Diagonal does not separate four distinct vertices.");
        foreach (var slots in new[] { aa, bb }) for (int i = 0; i < 3; i++)
            if (layout.Int(At(slots[i]) + 4) != layout.Int(At(slots[(i + 1) % 3]))) throw new InvalidDataException("Triangle edges do not form a loop.");
        var edgeData = layout.Array(mesh, 96, 4); int edgeStride = layout.Int(mesh.Object + 116);
        foreach (int e in aa.Concat(bb))
        {
            // EDGE_ORIGINAL=4 denotes base-mesh edges (as opposed to runtime cutter edges).
            // A newly authored base diagonal retains that policy; user/blocked/external and
            // runtime silhouette/retriangulation bits need their own metadata regeneration.
            if (_bytes[At(e) + 16] is not (0 or 4) || !_bytes.AsSpan(At(e) + 16, 4).SequenceEqual(_bytes.AsSpan(At(edgeIndex) + 16, 4)))
                throw new NotSupportedException("Diagonal patch contains unsupported navigation edge flags.");
            if (!_bytes.AsSpan(edgeData.Address + e * edgeStride * 4, edgeStride * 4).SequenceEqual(
                _bytes.AsSpan(edgeData.Address + edgeIndex * edgeStride * 4, edgeStride * 4)))
                throw new NotSupportedException("Diagonal patch has different edge user data.");
        }
        Vector3 Position(int v) => new(BitConverter.ToSingle(_bytes, plan.Vertices + v * 16),
            BitConverter.ToSingle(_bytes, plan.Vertices + v * 16 + 4), BitConverter.ToSingle(_bytes, plan.Vertices + v * 16 + 8));
        Vector3 pa = Position(a), pb = Position(b), pc = Position(c), pd = Position(d);
        var normal = Vector3.Cross(pb - pa, pc - pa);
        if (normal.LengthSquared() < 1e-12f) throw new InvalidDataException("Degenerate source triangle.");
        normal = Vector3.Normalize(normal);
        if (MathF.Abs(Vector3.Dot(pd - pa, normal)) > .0001f ||
            Vector3.Dot(Vector3.Cross(pd - pc, pb - pc), normal) <= 1e-8f ||
            Vector3.Dot(Vector3.Cross(pc - pd, pa - pd), normal) <= 1e-8f)
            throw new NotSupportedException("Diagonal flip requires a coplanar strictly convex quadrilateral.");
        foreach (int e in aa.Concat(bb))
        {
            int p = At(e), other = layout.Int(p + 8);
            if (other == -1) continue;
            if ((uint)other >= plan.EdgeCount || layout.Int(At(other) + 8) != e || layout.Int(p + 12) != Face(other) ||
                layout.Int(At(other) + 12) != Face(e) || layout.Int(p) != layout.Int(At(other) + 4) || layout.Int(p + 4) != layout.Int(At(other)))
                throw new NotSupportedException("Diagonal patch needs unsupported welded or nonreciprocal edge reconstruction.");
        }
        if (!write) return plan;
        var remap = new Dictionary<int, int> { [aa[0]] = aa[0], [bb[0]] = bb[0],
            [aa[1]] = aa[2], [aa[2]] = bb[1], [bb[1]] = bb[2], [bb[2]] = aa[1] };
        byte[] modified = _bytes.ToArray();
        foreach (var (source, target) in remap) _bytes.AsSpan(At(source), 20).CopyTo(modified.AsSpan(At(target)));
        void Write(int address, int value) => BinaryPrimitives.WriteInt32LittleEndian(modified.AsSpan(address), value);
        int Owner(int target) => aa.Contains(target) ? faceA : faceB;
        for (int e = 0; e < plan.EdgeCount; e++)
        {
            int p = At(e), oldOpposite = BinaryPrimitives.ReadInt32LittleEndian(modified.AsSpan(p + 8));
            if (remap.TryGetValue(oldOpposite, out int updated)) { Write(p + 8, updated); Write(p + 12, Owner(updated)); }
        }
        Write(At(aa[0]), c); Write(At(aa[0]) + 4, d); Write(At(aa[0]) + 8, bb[0]); Write(At(aa[0]) + 12, faceB);
        Write(At(bb[0]), d); Write(At(bb[0]) + 4, c); Write(At(bb[0]) + 8, aa[0]); Write(At(bb[0]) + 12, faceA);
        int Updated(int address) => BinaryPrimitives.ReadInt32LittleEndian(modified.AsSpan(address));
        foreach (var slots in new[] { aa, bb }) for (int i = 0; i < 3; i++)
        {
            int e = slots[i], p = At(e), other = Updated(p + 8);
            if (Updated(p + 4) != Updated(At(slots[(i + 1) % 3]))) throw new InvalidDataException("Flipped triangle does not form a loop.");
            if (other == -1) continue;
            if ((uint)other >= plan.EdgeCount || Updated(At(other) + 8) != e || Updated(p + 12) != Face(other) ||
                Updated(At(other) + 12) != Owner(e) || Updated(p) != Updated(At(other) + 4) || Updated(p + 4) != Updated(At(other)))
                throw new NotSupportedException("Diagonal patch needs unsupported welded or nonreciprocal edge reconstruction.");
        }
        _ = Read(modified);
        return plan with { Modified = modified, ModifiedLayout = plan with { RequiredFaceConnections = [faceA, faceB] } };
    }

    public Mafia3NavigationTranslation ApplyEdgeFlipCook(int edgeIndex, ReadOnlySpan<byte> cooked)
    {
        var plan = PrepareEdgeFlip(edgeIndex);
        if (cooked.Length != _bytes.Length) throw new InvalidDataException("Topology cook changed storage extents.");
        _ = Read(cooked);
        var allowed = new bool[_bytes.Length];
        for (int c = 0; c < plan.ClusterCount; c++) Array.Fill(allowed, true, plan.GraphPositions + c * 16, 12);
        for (int e = 0; e < plan.GraphEdgeCount; e++) Array.Fill(allowed, true, plan.GraphEdges + e * 8, 2);
        Array.Fill(allowed, true, plan.RootCount, 4);
        for (int i = 0; i < cooked.Length; i++) if (!allowed[i] && cooked[i] != plan.Modified[i])
            throw new InvalidDataException("Topology cook changed data outside the validated diagonal and native graph.");
        if (BinaryPrimitives.ReadInt32LittleEndian(cooked[plan.RootCount..]) != 2) throw new InvalidDataException("Topology cook retained stale accelerators.");
        var patches = new List<NavigationBytePatch>();
        for (int i = 0; i < cooked.Length;)
        {
            if (cooked[i] == _bytes[i]) { i++; continue; }
            int first = i++; while (i < cooked.Length && cooked[i] != _bytes[i]) i++;
            patches.Add(new(first, _bytes.AsSpan(first, i - first).ToArray(), cooked[first..i].ToArray()));
        }
        var action = new Mafia3NavigationTranslation(this, patches, true); action.Redo(); return action;
    }
}
