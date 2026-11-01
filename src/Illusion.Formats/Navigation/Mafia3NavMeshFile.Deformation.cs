using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Navigation;

public sealed partial class Mafia3NavMeshFile
{
    /// <summary>Validates planar-face, fixed-topology interior edits before an isolated native cook.
    /// Boundary, streaming, cluster membership and all connectivity records remain unchanged.</summary>
    public Mafia3NavigationCookPlan PrepareInteriorDeformation(IReadOnlyDictionary<int, Vector3> edits)
        => PrepareDeformation(edits, null);

    private Mafia3NavigationCookPlan PrepareDeformation(IReadOnlyDictionary<int, Vector3> edits, IReadOnlySet<int>? pairedBoundary)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var layout = new TranslationLayout(_bytes);
        if (layout.Variants.Count < 2 || layout.Variants[0].Type != "hkaiNavMesh" ||
            layout.Variants[1].Type != "hkaiDirectedGraphExplicitCost" || layout.Variants.Skip(2).Any(v =>
                v.Type is not ("hkcdStaticAabbTree" or "hkaiNavMeshClearanceCacheSeeder")))
            throw new NotSupportedException("Native deformation requires one mesh/graph followed by known optional accelerators.");
        var mesh = layout.Variants[0]; var graph = layout.Variants[1];
        var vertices = layout.Array(mesh, 48, 16); var faces = layout.Array(mesh, 16, 16);
        var edges = layout.Array(mesh, 32, 20); var points = layout.Array(graph, 16, 16);
        var nodes = layout.Array(graph, 32, 8); var graphEdges = layout.Array(graph, 48, 8);
        if (faces.Count is < 1 or > 100000 || vertices.Count is < 3 or > 200000 ||
            nodes.Count != points.Count || nodes.Count is < 1 or > 4096)
            throw new NotSupportedException("Navigation exceeds the bounded native cook limits.");
        Vector3 Read(int address) => new(BitConverter.ToSingle(_bytes, address), BitConverter.ToSingle(_bytes, address + 4), BitConverter.ToSingle(_bytes, address + 8));
        var original = Enumerable.Range(0, vertices.Count).Select(i => Read(vertices.Address + i * 16)).ToArray();
        var changed = original.ToArray();
        foreach (var (id, value) in edits)
        {
            if ((uint)id >= vertices.Count || !Mafia3AiWorldFile.Finite(value)) throw new ArgumentException("Invalid navigation vertex edit.", nameof(edits));
            changed[id] = value;
        }
        var boundary = new HashSet<int>();
        for (int i = 0; i < edges.Count; i++)
        {
            int edge = edges.Address + i * 20, a = layout.Int(edge), b = layout.Int(edge + 4);
            if ((uint)a >= vertices.Count || (uint)b >= vertices.Count) throw new InvalidDataException("Invalid navigation edge vertices.");
            uint opposite = unchecked((uint)layout.Int(edge + 8)), face = unchecked((uint)layout.Int(edge + 12));
            if (opposite == uint.MaxValue || face == uint.MaxValue || opposite >= edges.Count || face >= faces.Count || (_bytes[edge + 16] & 0x60) != 0)
            { boundary.Add(a); boundary.Add(b); }
        }
        foreach (int id in boundary)
            if (changed[id] != original[id] && pairedBoundary?.Contains(id) != true) throw new NotSupportedException($"Vertex {id} touches a boundary, blocked edge or streaming connection.");
        Vector3 minimum = Read(mesh.Object + 128), maximum = Read(mesh.Object + 144);
        foreach (var value in changed)
            if (value.X < minimum.X || value.Y < minimum.Y || value.Z < minimum.Z || value.X > maximum.X || value.Y > maximum.Y || value.Z > maximum.Z)
                throw new NotSupportedException("Interior edits must remain inside the existing section bounds.");
        for (int f = 0; f < faces.Count; f++)
        {
            int face = faces.Address + f * 16, first = layout.Int(face);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(face + 8));
            if (count < 3 || first < 0 || first > edges.Count - count || BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(face + 12)) >= nodes.Count)
                throw new InvalidDataException("Invalid navigation face or cluster.");
            int[] ids = Enumerable.Range(first, count).Select(i => layout.Int(edges.Address + i * 20)).ToArray();
            if (ids.All(i => changed[i] == original[i])) continue;
            var origin = original[ids[0]]; var normal = Vector3.Zero;
            for (int i = 1; i < count - 1; i++) normal += Vector3.Cross(original[ids[i]] - origin, original[ids[i + 1]] - origin);
            if (!Mafia3AiWorldFile.Finite(normal) || normal.LengthSquared() < 1e-12f) throw new InvalidDataException("Degenerate original navigation face.");
            normal = Vector3.Normalize(normal);
            var changedOrigin = changed[ids[0]]; var changedNormal = Vector3.Zero;
            for (int i = 1; i < count - 1; i++) changedNormal += Vector3.Cross(changed[ids[i]] - changedOrigin, changed[ids[i + 1]] - changedOrigin);
            if (!Mafia3AiWorldFile.Finite(changedNormal) || changedNormal.LengthSquared() < 1e-12f)
                throw new NotSupportedException("Deformation would collapse a navigation face.");
            changedNormal = Vector3.Normalize(changedNormal);
            // Mafia III's navigation is Z-up. Preserve orientation and never introduce a
            // steeper-than-45-degree surface (or worsen an already steeper authored face).
            if (Vector3.Dot(normal, changedNormal) <= 0 || MathF.Abs(changedNormal.Z) + 1e-6f < MathF.Min(MathF.Abs(normal.Z), .70710677f))
                throw new NotSupportedException("Deformation would reverse or excessively steepen a navigation face.");
            for (int i = 0; i < count; i++)
            {
                Vector3 a = changed[ids[i]], b = changed[ids[(i + 1) % count]], c = changed[ids[(i + 2) % count]];
                if (MathF.Abs(Vector3.Dot(a - changedOrigin, changedNormal)) > .001f)
                    throw new NotSupportedException("Deformation would make a navigation polygon non-planar.");
                if (Vector3.Dot(Vector3.Cross(b - a, c - b), changedNormal) <= 1e-8f)
                    throw new NotSupportedException("Deformation would collapse, reverse or make a navigation face concave.");
            }
        }
        for (int i = 0; i < graphEdges.Count; i++)
            if ((uint)layout.Int(graphEdges.Address + i * 8 + 4) >= nodes.Count)
                throw new NotSupportedException("Native deformation cannot cook external graph edge targets.");
        byte[] modified = (byte[])_bytes.Clone();
        for (int i = 0; i < changed.Length; i++)
        {
            int address = vertices.Address + i * 16;
            BinaryPrimitives.WriteSingleLittleEndian(modified.AsSpan(address), changed[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(modified.AsSpan(address + 4), changed[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(modified.AsSpan(address + 8), changed[i].Z);
        }
        return new(_bytes.ToArray(), modified, mesh.Object, graph.Object, vertices.Address, vertices.Count,
            faces.Address, faces.Count, edges.Address, edges.Count, points.Address, nodes.Address, nodes.Count,
            graphEdges.Address, graphEdges.Count, layout.RootCount, boundary.Order().ToArray());
    }

    /// <summary>Applies a fully validated helper result to the existing canonical mesh adapters.</summary>
    public Mafia3NavigationTranslation ApplyInteriorCook(ReadOnlySpan<byte> cooked)
    {
        if (cooked.Length != _bytes.Length) throw new InvalidDataException("Native cook changed resource size.");
        _ = Read(cooked);
        var initial = PrepareInteriorDeformation(new Dictionary<int, Vector3>());
        var edits = new Dictionary<int, Vector3>();
        for (int v = 0; v < initial.VertexCount; v++)
        {
            int address = initial.Vertices + v * 16;
            if (_bytes.AsSpan(address, 12).SequenceEqual(cooked.Slice(address, 12))) continue;
            edits.Add(v, new(BinaryPrimitives.ReadSingleLittleEndian(cooked[address..]),
                BinaryPrimitives.ReadSingleLittleEndian(cooked[(address + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(cooked[(address + 8)..])));
        }
        var validated = PrepareInteriorDeformation(edits);
        foreach (var link in ReadStreaming().Graph)
            if (!_bytes.AsSpan(initial.GraphPositions + link.Node * 16, 12).SequenceEqual(cooked.Slice(initial.GraphPositions + link.Node * 16, 12)))
                throw new NotSupportedException("Moving a streamed cluster representative requires joint peer cost recooking.");
        var allowed = new bool[_bytes.Length];
        void Allow(int address, int size) => Array.Fill(allowed, true, address, size);
        for (int v = 0; v < initial.VertexCount; v++) Allow(initial.Vertices + v * 16, 12);
        for (int c = 0; c < initial.ClusterCount; c++) Allow(initial.GraphPositions + c * 16, 12);
        for (int e = 0; e < initial.GraphEdgeCount; e++) Allow(initial.GraphEdges + e * 8, 2);
        Allow(initial.RootCount, 4);
        for (int address = 0; address < allowed.Length; address++)
            if (!allowed[address] && cooked[address] != validated.Baseline[address])
                throw new InvalidDataException("Native cook changed connectivity, streaming, user data or unsupported metadata.");
        if (edits.Count != 0 && BinaryPrimitives.ReadInt32LittleEndian(cooked[initial.RootCount..]) != 2)
            throw new InvalidDataException("Native cook retained stale geometric accelerators.");
        var patches = new List<NavigationBytePatch>();
        int i = 0;
        while (i < _bytes.Length)
        {
            if (_bytes[i] == cooked[i]) { i++; continue; }
            int start = i++;
            while (i < _bytes.Length && _bytes[i] != cooked[i]) i++;
            patches.Add(new(start, _bytes.AsSpan(start, i - start).ToArray(), cooked[start..i].ToArray()));
        }
        var result = new Mafia3NavigationTranslation(this, patches); result.Redo(); return result;
    }
}

public sealed record Mafia3NavigationCookPlan(byte[] Baseline, byte[] Modified, int MeshObject, int GraphObject,
    int Vertices, int VertexCount, int Faces, int FaceCount, int Edges, int EdgeCount,
    int GraphPositions, int GraphNodes, int ClusterCount, int GraphEdges, int GraphEdgeCount,
    int RootCount, int[] BoundaryVertices)
{
    public Mafia3NavigationCookPlan? ModifiedLayout { get; init; }
    public int[]? RequiredFaceConnections { get; init; }
}
