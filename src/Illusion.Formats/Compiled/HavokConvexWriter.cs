using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Illusion.Formats.Compiled;

/// <summary>Builds Mafia III PC Havok 2014.2 convex packfiles, including native support planes and face topology.</summary>
public static class HavokConvexWriter
{
    private sealed record Polygon(Vector4 Plane, byte[] Vertices);

    /// <summary>The template supplies the exact platform layout and Havok header predicates, never its objects.</summary>
    public static byte[] Write(ReadOnlySpan<byte> template, IReadOnlyList<HavokConvexHull> hulls)
    {
        _ = HavokConvexData.Read(template);
        if (hulls.Count is < 1 or > 10000) throw new ArgumentException("A convex container needs 1–10000 shapes.", nameof(hulls));
        using var classes = new MemoryStream();
        using var classWriter = new BinaryWriter(classes, Encoding.ASCII, leaveOpen: true);
        int Class(uint signature, string name)
        {
            classWriter.Write(signature); classWriter.Write((byte)9);
            int offset = checked((int)classes.Position);
            classWriter.Write(Encoding.ASCII.GetBytes(name)); classWriter.Write((byte)0);
            return offset;
        }
        int rootClass = Class(0x2772c11e, "hkRootLevelContainer");
        int shapeClass = Class(0x58beb7f4, "hknpConvexPolytopeShape");
        Pad(classes, 16, 0xff);
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, Encoding.ASCII, leaveOpen: true);
        var local = new List<(int Source, int Target)>();
        var virtuals = new List<(int Object, int Class)> { (0, rootClass) };
        writer.Write(0UL); writer.Write(hulls.Count); writer.Write(unchecked((uint)hulls.Count | 0x80000000u));
        int variants = checked((int)data.Position);
        writer.Write(new byte[checked(hulls.Count * 24)]);
        local.Add((0, variants));
        int Text(string value)
        {
            int offset = checked((int)data.Position);
            writer.Write(Encoding.ASCII.GetBytes(value)); writer.Write((byte)0);
            return offset;
        }
        int typeName = Text("hknpConvexPolytopeShape");
        for (int index = 0; index < hulls.Count; index++)
        {
            local.Add((variants + index * 24, Text("Shape_" + index)));
            local.Add((variants + index * 24 + 8, typeName));
            Pad(data, 16);
            int shape = checked((int)data.Position);
            local.Add((variants + index * 24 + 16, shape));
            virtuals.Add((shape, shapeClass));
            writer.Write(Shape(hulls[index]));
        }
        Pad(data, 16);
        int localStart = checked((int)data.Position);
        foreach (var fixup in local) { writer.Write(fixup.Source); writer.Write(fixup.Target); }
        Pad(data, 16, 0xff);
        int globalStart = checked((int)data.Position);
        // The entire container lives in one data section, so it needs no cross-section pointer fixups.
        int virtualStart = globalStart;
        foreach (var fixup in virtuals) { writer.Write(fixup.Object); writer.Write(0); writer.Write(fixup.Class); }
        Pad(data, 16, 0xff);
        int end = checked((int)data.Position);
        using var output = new MemoryStream();
        using var destination = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        byte[] header = template[..80].ToArray();
        void Header(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset), value);
        Header(20, 3); Header(24, 2); Header(28, 0); Header(32, 0); Header(36, rootClass);
        destination.Write(header);
        int classStart = 80 + 3 * 64;
        int dataStart = checked(classStart + (int)classes.Length);
        Section(destination, "__classnames__", classStart, checked((int)classes.Length), checked((int)classes.Length), checked((int)classes.Length), checked((int)classes.Length));
        Section(destination, "__types__", dataStart, 0, 0, 0, 0);
        Section(destination, "__data__", dataStart, localStart, globalStart, virtualStart, end);
        destination.Write(classes.ToArray()); destination.Write(data.ToArray());
        byte[] result = output.ToArray();
        var decoded = HavokConvexData.Read(result);
        if (decoded.Count != hulls.Count || decoded.Any(h => h is null)) throw new InvalidDataException("Cooked convex packfile failed its independent reader.");
        return result;
    }

    private static void Section(BinaryWriter writer, string name, int start, int local, int global, int virtuals, int end)
    {
        byte[] label = new byte[20]; Encoding.ASCII.GetBytes(name).CopyTo(label, 0);
        writer.Write(label); writer.Write(start); writer.Write(local); writer.Write(global); writer.Write(virtuals);
        writer.Write(end); writer.Write(end); writer.Write(end); writer.Write(new byte[16]);
    }

    private static byte[] Shape(HavokConvexHull hull)
    {
        var vertices = hull.Vertices;
        if (vertices.Length is < 4 or > 252 || hull.Indices.Length < 12 || hull.Indices.Length % 3 != 0
            || vertices.Any(v => !Finite(v)) || !float.IsFinite(hull.ConvexRadius) || hull.ConvexRadius < 0)
            throw new ArgumentException("A cooked convex needs 4–252 finite vertices, closed triangles and a nonnegative radius.", nameof(hull));
        var polygons = Polygons(vertices, hull.Indices);
        if (polygons.Count is < 4 or > 255) throw new ArgumentException("Havok convexes support 4–255 faces.", nameof(hull));
        int paddedVertices = (vertices.Length + 3) & ~3;
        const int vertexStart = 96;
        int planeStart = checked(vertexStart + paddedVertices * 16);
        int faceStart = checked(planeStart + polygons.Count * 16);
        int indexStart = (faceStart + polygons.Count * 4 + 15) & ~15;
        int indexCount = polygons.Sum(p => p.Vertices.Length);
        int size = (indexStart + indexCount + 15) & ~15;
        if (size > ushort.MaxValue) throw new ArgumentException("The cooked convex exceeds Havok's 16-bit relative layout.", nameof(hull));
        byte[] bytes = new byte[size];
        void U16(int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)value));
        void F(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
        // Refcount/vtable/runtime pointers remain zero in serialized objects and are fixed by Havok's loader.
        U16(16, polygons.All(p => p.Vertices.Length <= 16) ? 0x143 : 0x103);
        bytes[19] = 1; F(20, hull.ConvexRadius);
        U16(48, paddedVertices); U16(50, vertexStart - 48);
        U16(64, polygons.Count); U16(66, planeStart - 64);
        U16(68, polygons.Count); U16(70, faceStart - 68);
        U16(72, indexCount); U16(74, indexStart - 72);
        for (int index = 0; index < paddedVertices; index++)
        {
            int unique = Math.Min(index, vertices.Length - 1), offset = vertexStart + index * 16;
            var vertex = vertices[unique]; F(offset, vertex.X); F(offset + 4, vertex.Y); F(offset + 8, vertex.Z);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 12), 0x3f000000u | (uint)unique);
        }
        int first = 0;
        for (int index = 0; index < polygons.Count; index++)
        {
            var polygon = polygons[index]; int offset = planeStart + index * 16;
            F(offset, polygon.Plane.X); F(offset + 4, polygon.Plane.Y); F(offset + 8, polygon.Plane.Z); F(offset + 12, polygon.Plane.W);
            U16(faceStart + index * 4, first); bytes[faceStart + index * 4 + 2] = checked((byte)polygon.Vertices.Length);
            // Zero angular TIM tolerance conservatively invalidates cached face choices after rotation.
            bytes[faceStart + index * 4 + 3] = 0;
            polygon.Vertices.CopyTo(bytes, indexStart + first); first += polygon.Vertices.Length;
        }
        return bytes;
    }

    private static List<Polygon> Polygons(Vector3[] vertices, uint[] indices)
    {
        float extent = Vector3.Distance(vertices.Aggregate(Vector3.Min), vertices.Aggregate(Vector3.Max));
        if (!(extent > 1e-6f)) throw new ArgumentException("The convex has no volume.", nameof(vertices));
        float epsilon = extent * 1e-5f;
        var groups = new List<(Vector4 Plane, List<(int A, int B, int C)> Triangles)>();
        var edges = new Dictionary<(int A, int B), int>();
        double volume = 0;
        foreach (var triangle in indices.Chunk(3))
        {
            if (triangle.Any(index => index >= vertices.Length)) throw new ArgumentException("A convex triangle names a missing vertex.", nameof(indices));
            int a = (int)triangle[0], b = (int)triangle[1], c = (int)triangle[2];
            var cross = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            if (cross.LengthSquared() <= epsilon * epsilon * epsilon * epsilon) throw new ArgumentException("The convex contains a degenerate triangle.", nameof(indices));
            var normal = Vector3.Normalize(cross); float distance = -Vector3.Dot(normal, vertices[a]);
            if (vertices.Any(vertex => Vector3.Dot(normal, vertex) + distance > epsilon))
                throw new ArgumentException("Triangles must form an outward-wound convex surface.", nameof(indices));
            volume += Vector3.Dot(vertices[a], Vector3.Cross(vertices[b], vertices[c])) / 6d;
            foreach (var edge in new[] { (a, b), (b, c), (c, a) }) edges[edge] = edges.GetValueOrDefault(edge) + 1;
            int group = groups.FindIndex(g => Vector3.Dot(new Vector3(g.Plane.X, g.Plane.Y, g.Plane.Z), normal) > 0.99999f && Math.Abs(g.Plane.W - distance) <= epsilon);
            if (group < 0) { group = groups.Count; groups.Add((new Vector4(normal, distance), [])); }
            groups[group].Triangles.Add((a, b, c));
        }
        if (edges.Any(edge => edge.Value != 1 || edges.GetValueOrDefault((edge.Key.B, edge.Key.A)) != 1)
            || volume <= (double)epsilon * epsilon * epsilon)
            throw new ArgumentException("The convex must be a closed, consistently wound three-dimensional surface.", nameof(indices));
        var result = new List<Polygon>();
        foreach (var group in groups)
        {
            var boundary = new HashSet<(int A, int B)>();
            foreach (var (a, b, c) in group.Triangles)
                foreach (var edge in new[] { (a, b), (b, c), (c, a) })
                    if (!boundary.Remove((edge.Item2, edge.Item1))) boundary.Add(edge);
            if (boundary.Count < 3) throw new ArgumentException("Invalid convex face boundary.", nameof(indices));
            var first = boundary.First(); int current = first.A;
            var polygon = new List<byte>();
            while (true)
            {
                polygon.Add(checked((byte)current));
                var next = boundary.Where(edge => edge.A == current).ToArray();
                if (next.Length != 1) throw new ArgumentException("The convex face has a branched boundary.", nameof(indices));
                boundary.Remove(next[0]); current = next[0].B;
                if (current == first.A) break;
                if (polygon.Count > vertices.Length) throw new ArgumentException("Cyclic convex face boundary.", nameof(indices));
            }
            if (boundary.Count != 0 || polygon.Count > 255) throw new ArgumentException("The convex face contains disconnected loops.", nameof(indices));
            result.Add(new Polygon(group.Plane, polygon.ToArray()));
        }
        return result;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static void Pad(Stream stream, int alignment, byte value = 0)
    { while (stream.Position % alignment != 0) stream.WriteByte(value); }
}
