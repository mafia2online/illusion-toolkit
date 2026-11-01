using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Navigation;

public sealed partial class Mafia3NavMeshFile
{
    /// <summary>Appends one interior vertex and divides a triangle into three faces in its existing
    /// cluster. Actual streamed face/edge/cluster identities must remain outside the patch.</summary>
    public Mafia3NavigationCookPlan PrepareFaceSubdivision(int faceIndex, Vector3? position = null)
    {
        var plan = PrepareInteriorDeformation(new Dictionary<int, Vector3>()); var layout = new TranslationLayout(_bytes);
        return PrepareFaceSubdivision(plan, layout, faceIndex, position, true);
    }

    /// <summary>Lists bounded read-only centroid subdivision candidates; native baseline validation
    /// remains mandatory when cooking the requested edit.</summary>
    public IReadOnlyList<int> FaceSubdivisionCandidates(int limit = 32)
    {
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        var plan = PrepareInteriorDeformation(new Dictionary<int, Vector3>()); var layout = new TranslationLayout(_bytes); var result = new List<int>();
        for (int face = 0; face < plan.FaceCount && result.Count < limit; face++)
            try { _ = PrepareFaceSubdivision(plan, layout, face, null, false); result.Add(face); }
            catch (NotSupportedException) { }
        return result;
    }

    private Mafia3NavigationCookPlan PrepareFaceSubdivision(Mafia3NavigationCookPlan plan, TranslationLayout layout,
        int faceIndex, Vector3? position, bool write)
    {
        if ((uint)faceIndex >= plan.FaceCount) throw new ArgumentOutOfRangeException(nameof(faceIndex));
        if (plan.FaceCount > 99998 || plan.VertexCount >= 200000 || plan.EdgeCount > 600000 - 6)
            throw new NotSupportedException("Subdivision exceeds the bounded navigation cook limits.");
        var mesh = layout.Variants[0]; var graph = layout.Variants[1]; int face = plan.Faces + faceIndex * 16, first = layout.Int(face);
        ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(at));
        if (U16(face + 8) != 3 || U16(face + 10) != 0) throw new NotSupportedException("Subdivision requires a triangle without user edges.");
        int cluster = U16(face + 12);
        foreach (var sets in new[] { layout.Array(mesh, 64, 56), layout.Array(graph, 104, 56) })
            for (int s = 0; s < sets.Count; s++)
            {
                int set = sets.Address + s * 56; var connections = layout.ArrayAt(set + 8, 16);
                for (int i = 0; i < connections.Count; i++)
                {
                    int at = connections.Address + i * 16, edge = layout.Int(at + 4);
                    if (layout.Int(at) == faceIndex || edge >= first && edge < first + 3)
                        throw new NotSupportedException("Subdivision touches a streamed face or edge.");
                }
                var graphConnections = layout.ArrayAt(set + 24, 16);
                for (int i = 0; i < graphConnections.Count; i++) if (layout.Int(graphConnections.Address + i * 16) == cluster)
                    throw new NotSupportedException("Subdivision cluster has an external streaming graph connection.");
                if (layout.ArrayAt(set + 40, 8).Count != 0) throw new NotSupportedException("Volume streaming topology is unsupported.");
            }
        var edgeData = layout.Array(mesh, 96, 4); int edgeStride = layout.Int(mesh.Object + 116);
        int[] ids = new int[3];
        for (int i = 0; i < 3; i++)
        {
            int at = plan.Edges + (first + i) * 20, next = plan.Edges + (first + (i + 1) % 3) * 20;
            ids[i] = layout.Int(at);
            if (layout.Int(at + 4) != layout.Int(next) || _bytes[at + 16] is not (0 or 4) ||
                !_bytes.AsSpan(at + 16, 4).SequenceEqual(_bytes.AsSpan(plan.Edges + first * 20 + 16, 4)))
                throw new NotSupportedException("Subdivision triangle has unsupported edge topology or flags.");
            if (!_bytes.AsSpan(edgeData.Address + (first + i) * edgeStride * 4, edgeStride * 4).SequenceEqual(
                _bytes.AsSpan(edgeData.Address + first * edgeStride * 4, edgeStride * 4)))
                throw new NotSupportedException("Subdivision requires equal triangle edge user data.");
            int opposite = layout.Int(at + 8);
            if (opposite == -1) continue;
            if ((uint)opposite >= plan.EdgeCount || opposite >= first && opposite < first + 3 ||
                layout.Int(plan.Edges + opposite * 20 + 8) != first + i || layout.Int(plan.Edges + opposite * 20 + 12) != faceIndex ||
                layout.Int(plan.Edges + opposite * 20) != layout.Int(at + 4) || layout.Int(plan.Edges + opposite * 20 + 4) != ids[i])
                throw new NotSupportedException("Subdivision needs unsupported welded or external neighbor reconstruction.");
            int neighborFace = layout.Int(at + 12);
            if ((uint)neighborFace >= plan.FaceCount) throw new InvalidDataException("Subdivision neighbor face index is invalid.");
            int neighbor = plan.Faces + neighborFace * 16, neighborStart = layout.Int(neighbor), neighborCount = U16(neighbor + 8);
            if (opposite < neighborStart || opposite >= neighborStart + neighborCount) throw new InvalidDataException("Subdivision neighbor edge has the wrong owning face.");
        }
        Vector3 ReadVertex(int v) => new(BitConverter.ToSingle(_bytes, plan.Vertices + v * 16),
            BitConverter.ToSingle(_bytes, plan.Vertices + v * 16 + 4), BitConverter.ToSingle(_bytes, plan.Vertices + v * 16 + 8));
        Vector3 a = ReadVertex(ids[0]), b = ReadVertex(ids[1]), c = ReadVertex(ids[2]), point = position ?? (a + b + c) / 3;
        var normal = Vector3.Cross(b - a, c - a);
        if (!Mafia3AiWorldFile.Finite(point) || normal.LengthSquared() < 1e-12f) throw new ArgumentException("Invalid subdivision position.", nameof(position));
        normal = Vector3.Normalize(normal);
        if (MathF.Abs(Vector3.Dot(point - a, normal)) > .0001f ||
            Enumerable.Range(0, 3).Any(i => Vector3.Dot(Vector3.Cross(ReadVertex(ids[(i + 1) % 3]) - ReadVertex(ids[i]), point - ReadVertex(ids[i])), normal) <= 1e-8f))
            throw new NotSupportedException("Subdivision vertex must lie strictly inside the original triangle plane.");
        if (!write) return plan;
        byte[] vertices = new byte[(plan.VertexCount + 1) * 16]; _bytes.AsSpan(plan.Vertices, plan.VertexCount * 16).CopyTo(vertices);
        void Float(byte[] data, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at), value);
        Float(vertices, plan.VertexCount * 16, point.X); Float(vertices, plan.VertexCount * 16 + 4, point.Y); Float(vertices, plan.VertexCount * 16 + 8, point.Z);
        byte[] faces = new byte[(plan.FaceCount + 2) * 16]; _bytes.AsSpan(plan.Faces, plan.FaceCount * 16).CopyTo(faces);
        for (int i = 0; i < 2; i++)
        {
            _bytes.AsSpan(face, 16).CopyTo(faces.AsSpan((plan.FaceCount + i) * 16));
            BinaryPrimitives.WriteInt32LittleEndian(faces.AsSpan((plan.FaceCount + i) * 16), plan.EdgeCount + i * 3);
        }
        byte[] edges = new byte[(plan.EdgeCount + 6) * 20]; _bytes.AsSpan(plan.Edges, plan.EdgeCount * 20).CopyTo(edges);
        int[] starts = [first, plan.EdgeCount, plan.EdgeCount + 3], owners = [faceIndex, plan.FaceCount, plan.FaceCount + 1];
        void Int(int edge, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(edges.AsSpan(edge * 20 + offset), value);
        for (int i = 0; i < 3; i++)
        {
            int old = first + i, target = starts[i]; _bytes.AsSpan(plan.Edges + old * 20, 20).CopyTo(edges.AsSpan(target * 20));
            int other = layout.Int(plan.Edges + old * 20 + 8);
            if (other != -1) { Int(other, 8, target); Int(other, 12, owners[i]); }
            for (int j = 1; j < 3; j++) _bytes.AsSpan(plan.Edges + first * 20 + 16, 4).CopyTo(edges.AsSpan((target + j) * 20 + 16));
            Int(target + 1, 0, ids[(i + 1) % 3]); Int(target + 1, 4, plan.VertexCount);
            Int(target + 1, 8, starts[(i + 1) % 3] + 2); Int(target + 1, 12, owners[(i + 1) % 3]);
            Int(target + 2, 0, plan.VertexCount); Int(target + 2, 4, ids[i]);
            Int(target + 2, 8, starts[(i + 2) % 3] + 1); Int(target + 2, 12, owners[(i + 2) % 3]);
        }
        var append = new NavigationPackAppend(_bytes); append.Array(mesh.Object + 16, faces, plan.FaceCount + 2);
        append.Array(mesh.Object + 32, edges, plan.EdgeCount + 6); append.Array(mesh.Object + 48, vertices, plan.VertexCount + 1);
        void GrowData(int header, int oldCount, int addedCount, int sourceIndex)
        {
            int stride = layout.Int(mesh.Object + (header == 80 ? 112 : 116)); if (stride == 0) return;
            var array = layout.Array(mesh, header, 4); byte[] data = new byte[(oldCount + addedCount) * stride * 4];
            _bytes.AsSpan(array.Address, oldCount * stride * 4).CopyTo(data);
            for (int i = 0; i < addedCount; i++) _bytes.AsSpan(array.Address + sourceIndex * stride * 4, stride * 4).CopyTo(data.AsSpan((oldCount + i) * stride * 4));
            append.Array(mesh.Object + header, data, (oldCount + addedCount) * stride);
        }
        GrowData(80, plan.FaceCount, 2, faceIndex); GrowData(96, plan.EdgeCount, 6, first);
        append.Int(plan.RootCount, 2); byte[] modified = append.Finish();
        var modifiedPlan = Read(modified).PrepareInteriorDeformation(new Dictionary<int, Vector3>());
        var edgeOwners = Enumerable.Repeat(-1, modifiedPlan.EdgeCount).ToArray();
        int Updated(int at) => BinaryPrimitives.ReadInt32LittleEndian(modified.AsSpan(at));
        for (int f = 0; f < modifiedPlan.FaceCount; f++)
        {
            int at = modifiedPlan.Faces + f * 16, start = Updated(at), count = BinaryPrimitives.ReadUInt16LittleEndian(modified.AsSpan(at + 8));
            for (int e = start; e < start + count; e++)
            {
                if (edgeOwners[e] != -1) throw new InvalidDataException("Subdivision face edge ranges overlap.");
                edgeOwners[e] = f;
            }
        }
        for (int f = 0; f < 3; f++) for (int i = 0; i < 3; i++)
        {
            int e = starts[f] + i, at = modifiedPlan.Edges + e * 20, opposite = Updated(at + 8);
            if (Updated(at + 4) != Updated(modifiedPlan.Edges + (starts[f] + (i + 1) % 3) * 20))
                throw new InvalidDataException("Subdivision face is not a closed loop.");
            if (opposite == -1) continue;
            int other = modifiedPlan.Edges + opposite * 20;
            if ((uint)opposite >= modifiedPlan.EdgeCount || Updated(other + 8) != e || Updated(other + 12) != owners[f] ||
                Updated(at + 12) != edgeOwners[opposite] || Updated(at) != Updated(other + 4) || Updated(at + 4) != Updated(other))
                throw new InvalidDataException("Subdivision neighbor links disagree.");
        }
        return plan with { Modified = modified, ModifiedLayout = modifiedPlan with { RequiredFaceConnections = [faceIndex, plan.FaceCount, faceIndex, plan.FaceCount + 1] } };
    }

    public Mafia3NavigationReplacement ApplyFaceSubdivisionCook(int faceIndex, ReadOnlySpan<byte> cooked, Vector3? position = null)
    {
        return ApplyNavigationGrowth(PrepareFaceSubdivision(faceIndex, position), cooked);
    }

    private Mafia3NavigationReplacement ApplyNavigationGrowth(Mafia3NavigationCookPlan plan, ReadOnlySpan<byte> cooked, IReadOnlySet<int>? streamingCosts = null, bool apply = true)
    {
        var modified = plan.ModifiedLayout!;
        if (cooked.Length != plan.Modified.Length) throw new InvalidDataException("Subdivision cook changed unplanned storage extents.");
        _ = Read(cooked); var allowed = new bool[cooked.Length];
        for (int c = 0; c < modified.ClusterCount; c++)
        {
            int at = modified.GraphPositions + c * 16;
            var point = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(cooked[at..]), BinaryPrimitives.ReadSingleLittleEndian(cooked[(at + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(cooked[(at + 8)..]));
            if (!Mafia3AiWorldFile.Finite(point)) throw new InvalidDataException("Non-finite cooked graph representative.");
            Array.Fill(allowed, true, at, 12);
        }
        for (int e = 0; e < modified.GraphEdgeCount; e++)
        {
            int at = modified.GraphEdges + e * 8; float cost = BitConverter.UInt32BitsToSingle((uint)BinaryPrimitives.ReadUInt16LittleEndian(cooked[at..]) << 16);
            if (!float.IsFinite(cost) || cost < 0) throw new InvalidDataException("Invalid cooked graph edge cost.");
            Array.Fill(allowed, true, at, 2);
        }
        if (streamingCosts is not null)
            foreach (int at in streamingCosts)
            {
                ushort cost = BinaryPrimitives.ReadUInt16LittleEndian(cooked[at..]);
                if ((cost & 0xFF80) >= 0x7F80) throw new InvalidDataException("Invalid cooked streaming cost.");
                Array.Fill(allowed, true, at, 2);
            }
        for (int i = 0; i < cooked.Length; i++) if (!allowed[i] && cooked[i] != plan.Modified[i])
            throw new InvalidDataException("Subdivision cook changed data outside the exact planned arrays and native graph.");
        var action = new Mafia3NavigationReplacement(this, _bytes.ToArray(), cooked.ToArray()); if (apply) action.Redo(); return action;
    }

    internal void RestoreReplacement(byte[] expected, byte[] replacement)
    {
        if (!_bytes.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("Navigation changed outside this edit history; refusing to overwrite another edit.");
        var parsed = Read(replacement);
        if (parsed.Meshes.Count != _meshes.Count || parsed.Meshes.Where((mesh, i) => mesh.FaceDataStride != _meshes[i].FaceDataStride || mesh.EdgeDataStride != _meshes[i].EdgeDataStride).Any())
            throw new InvalidDataException("Navigation replacement changes unsupported mesh or user data schema.");
        for (int i = 0; i < _meshes.Count; i++) _meshes[i].RefreshGeometry(parsed.Meshes[i], true, true);
        _bytes = replacement.ToArray();
    }
}

/// <summary>Whole-resource native navigation replacement with canonical mesh identity and exact undo.</summary>
public sealed class Mafia3NavigationReplacement
{
    private readonly Mafia3NavMeshFile _file;
    private readonly byte[] _before, _after;
    private bool _applied;
    internal Mafia3NavigationReplacement(Mafia3NavMeshFile file, byte[] before, byte[] after) => (_file, _before, _after) = (file, before, after);
    public void Undo() { if (!_applied) return; _file.RestoreReplacement(_after, _before); _applied = false; }
    public void Redo() { if (_applied) return; _file.RestoreReplacement(_before, _after); _applied = true; }
}
