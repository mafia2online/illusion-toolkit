using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Navigation;

public sealed record Mafia3NavigationExtrusionCandidate(int Edge, Vector3 Position);

public sealed partial class Mafia3NavMeshFile
{
    private sealed record ExtrusionLayout(Mafia3NavigationCookPlan Plan, TranslationLayout Layout, Vector3[] Vertices, int[][] Faces, int[] Owners);
    private ExtrusionLayout PrepareExtrusionLayout()
    {
        var plan = PrepareInteriorDeformation(new Dictionary<int, Vector3>()); var layout = new TranslationLayout(_bytes);
        var owners = Enumerable.Repeat(-1, plan.EdgeCount).ToArray(); var faces = new int[plan.FaceCount][];
        for (int f = 0; f < faces.Length; f++)
        {
            int at = plan.Faces + f * 16, first = layout.Int(at), count = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(at + 8));
            faces[f] = new int[count];
            for (int i = 0; i < count; i++)
            {
                if (owners[first + i] != -1) throw new InvalidDataException("Navigation face edge ranges overlap.");
                owners[first + i] = f; faces[f][i] = layout.Int(plan.Edges + (first + i) * 20);
            }
        }
        return new(plan, layout, _meshes.Single().Vertices.ToArray(), faces, owners);
    }
    /// <summary>Read-only, bounded open-boundary extrusion candidates. Bounds may grow only for
    /// entirely isolated sections; streamed sections retain their AABB.
    /// Suggested positions extend a quarter-unit or one quarter of the edge length, whichever is less.</summary>
    public IReadOnlyList<Mafia3NavigationExtrusionCandidate> BoundaryExtrusionCandidates(int limit = 32)
    {
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        var context = PrepareExtrusionLayout(); var result = new List<Mafia3NavigationExtrusionCandidate>();
        for (int edge = 0; edge < context.Plan.EdgeCount && result.Count < limit; edge++)
        {
            if (context.Layout.Int(context.Plan.Edges + edge * 20 + 8) != -1) continue;
            try { _ = PrepareBoundaryExtrusion(context, edge, null, false, out var position); result.Add(new(edge, position)); }
            catch (NotSupportedException) { }
        }
        return result;
    }
    public Mafia3NavigationCookPlan PrepareBoundaryExtrusion(int edgeIndex, Vector3? position = null) =>
        PrepareBoundaryExtrusion(PrepareExtrusionLayout(), edgeIndex, position, true, out _);
    public Mafia3NavigationReplacement ApplyBoundaryExtrusionCook(int edgeIndex, ReadOnlySpan<byte> cooked, Vector3? position = null) =>
        ApplyNavigationGrowth(PrepareBoundaryExtrusion(edgeIndex, position), cooked);

    private Mafia3NavigationCookPlan PrepareBoundaryExtrusion(ExtrusionLayout context, int edgeIndex, Vector3? position, bool write, out Vector3 point)
    {
        var (plan, layout, vertices, faces, owners) = context; point = default;
        if ((uint)edgeIndex >= plan.EdgeCount) throw new ArgumentOutOfRangeException(nameof(edgeIndex));
        if (plan.FaceCount >= 100000 || plan.VertexCount >= 200000 || plan.EdgeCount > 600000 - 3)
            throw new NotSupportedException("Extrusion exceeds bounded navigation cook limits.");
        int faceIndex = owners[edgeIndex], edge = plan.Edges + edgeIndex * 20;
        if (faceIndex < 0 || layout.Int(edge + 8) != -1 || layout.Int(edge + 12) != -1 || _bytes[edge + 16] is not (0 or 4))
            throw new NotSupportedException("Extrusion requires an ordinary open boundary edge.");
        var mesh = layout.Variants[0]; var graph = layout.Variants[1]; int face = plan.Faces + faceIndex * 16;
        int cluster = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(face + 12));
        if (BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(face + 10)) != 0) throw new NotSupportedException("Extrusion cannot change a face with user edges.");
        foreach (var sets in new[] { layout.Array(mesh, 64, 56), layout.Array(graph, 104, 56) })
            for (int s = 0; s < sets.Count; s++)
            {
                int set = sets.Address + s * 56; var connections = layout.ArrayAt(set + 8, 16);
                for (int i = 0; i < connections.Count; i++) if (layout.Int(connections.Address + i * 16) == faceIndex || layout.Int(connections.Address + i * 16 + 4) == edgeIndex)
                    throw new NotSupportedException("Extrusion touches an actual streamed face or edge.");
                var graphConnections = layout.ArrayAt(set + 24, 16);
                for (int i = 0; i < graphConnections.Count; i++) if (layout.Int(graphConnections.Address + i * 16) == cluster)
                    throw new NotSupportedException("Extrusion cluster has an external graph connection.");
                if (layout.ArrayAt(set + 40, 8).Count != 0) throw new NotSupportedException("Volume streaming extrusion is unsupported.");
            }
        int a = layout.Int(edge), b = layout.Int(edge + 4); var pa = vertices[a]; var pb = vertices[b]; var normal = Vector3.Zero;
        int[] polygon = faces[faceIndex]; var origin = vertices[polygon[0]];
        int firstEdge = layout.Int(face);
        for (int i = 0; i < polygon.Length; i++)
            if (layout.Int(plan.Edges + (firstEdge + i) * 20 + 4) != polygon[(i + 1) % polygon.Length])
                throw new InvalidDataException("Extrusion source face edges do not form a loop.");
        for (int i = 1; i < polygon.Length - 1; i++) normal += Vector3.Cross(vertices[polygon[i]] - origin, vertices[polygon[i + 1]] - origin);
        if (normal.LengthSquared() < 1e-12f || Vector3.DistanceSquared(pa, pb) < 1e-10f) throw new NotSupportedException("Extrusion source is degenerate.");
        normal = Vector3.Normalize(normal);
        if (MathF.Abs(normal.Z) < .70710677f) throw new NotSupportedException("Extrusion requires a source face within 45 degrees of horizontal.");
        point = position ?? pa + (pb - pa) * .5f + Vector3.Normalize(Vector3.Cross(pb - pa, normal)) * MathF.Min(.25f, Vector3.Distance(pa, pb) * .25f);
        if (!Mafia3AiWorldFile.Finite(point)) throw new ArgumentException("Extrusion position must be finite.", nameof(position));
        if (MathF.Abs(Vector3.Dot(point - origin, normal)) > .0001f || Vector3.Dot(Vector3.Cross(pa - pb, point - pb), normal) <= 1e-8f)
            throw new NotSupportedException("Extrusion point must be coplanar and strictly outside the selected boundary edge.");
        Vector3 ReadVector(int at) => new(BitConverter.ToSingle(_bytes, at), BitConverter.ToSingle(_bytes, at + 4), BitConverter.ToSingle(_bytes, at + 8));
        var min = ReadVector(mesh.Object + 128); var max = ReadVector(mesh.Object + 144);
        bool expandsBounds = point.X < min.X || point.Y < min.Y || point.Z < min.Z || point.X > max.X || point.Y > max.Y || point.Z > max.Z;
        if (expandsBounds && (layout.Int(mesh.Object + 72) != 0 || layout.Int(graph.Object + 112) != 0))
            throw new NotSupportedException("Extrusion outside section bounds requires a fully isolated section without streaming sets.");
        // Conservative XY convex SAT: overpasses also refuse rather than guess vertical clearance.
        Vector3[] triangle = [pb, pa, point];
        bool Overlaps(int[] existing)
        {
            bool Separated(Vector3 p, Vector3 q)
            {
                double x = (double)q.Y - p.Y, y = (double)p.X - q.X;
                if (x == 0 && y == 0) return false;
                double Project(Vector3 v) => x * ((double)v.X - pa.X) + y * ((double)v.Y - pa.Y);
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity, otherLo = double.PositiveInfinity, otherHi = double.NegativeInfinity;
                foreach (var v in triangle) { double value = Project(v); lo = Math.Min(lo, value); hi = Math.Max(hi, value); }
                foreach (int id in existing) { double value = Project(vertices[id]); otherLo = Math.Min(otherLo, value); otherHi = Math.Max(otherHi, value); }
                return Math.Min(hi, otherHi) <= Math.Max(lo, otherLo);
            }
            for (int i = 0; i < 3; i++) if (Separated(triangle[i], triangle[(i + 1) % 3])) return false;
            for (int i = 0; i < existing.Length; i++) if (Separated(vertices[existing[i]], vertices[existing[(i + 1) % existing.Length]])) return false;
            return true;
        }
        bool SameXY(Vector3 x, Vector3 y) => x.X == y.X && x.Y == y.Y;
        bool OnSegment(Vector3 p, Vector3 x, Vector3 y) =>
            ((double)y.X - x.X) * ((double)p.Y - x.Y) - ((double)y.Y - x.Y) * ((double)p.X - x.X) == 0 &&
            p.X >= Math.Min(x.X, y.X) && p.X <= Math.Max(x.X, y.X) && p.Y >= Math.Min(x.Y, y.Y) && p.Y <= Math.Max(x.Y, y.Y);
        double Orientation(Vector3 x, Vector3 y, Vector3 p) => ((double)y.X - x.X) * ((double)p.Y - x.Y) - ((double)y.Y - x.Y) * ((double)p.X - x.X);
        bool Crosses(Vector3 x, Vector3 y, Vector3 u, Vector3 v) =>
            Orientation(x, y, u) * Orientation(x, y, v) < 0 && Orientation(u, v, x) * Orientation(u, v, y) < 0;
        for (int f = 0; f < faces.Length; f++)
        {
            int[] existing = faces[f];
            if (Overlaps(existing)) throw new NotSupportedException("Extrusion overlaps an existing navigation face in XY.");
            for (int i = 0; i < existing.Length; i++)
            {
                var x = vertices[existing[i]]; var y = vertices[existing[(i + 1) % existing.Length]];
                if (Enumerable.Range(0, 3).Any(t => Crosses(x, y, triangle[t], triangle[(t + 1) % 3])))
                    throw new NotSupportedException("Extrusion crosses existing boundary geometry.");
                if (OnSegment(point, x, y)) throw new NotSupportedException("Extrusion would touch another boundary and needs explicit reconnection.");
                if (!SameXY(x, pa) && !SameXY(x, pb) && Enumerable.Range(0, 3).Any(t => OnSegment(x, triangle[t], triangle[(t + 1) % 3])))
                    throw new NotSupportedException("Extrusion would create an unconnected boundary junction.");
                if (f != faceIndex && !SameXY(x, y) && OnSegment(x, pa, pb) && OnSegment(y, pa, pb))
                    throw new NotSupportedException("Extrusion source edge is shared geometrically without native connectivity.");
            }
        }
        if (!write) return plan;
        byte[] vertexData = new byte[(plan.VertexCount + 1) * 16]; _bytes.AsSpan(plan.Vertices, plan.VertexCount * 16).CopyTo(vertexData);
        BinaryPrimitives.WriteSingleLittleEndian(vertexData.AsSpan(plan.VertexCount * 16), point.X);
        BinaryPrimitives.WriteSingleLittleEndian(vertexData.AsSpan(plan.VertexCount * 16 + 4), point.Y);
        BinaryPrimitives.WriteSingleLittleEndian(vertexData.AsSpan(plan.VertexCount * 16 + 8), point.Z);
        byte[] faceData = new byte[(plan.FaceCount + 1) * 16]; _bytes.AsSpan(plan.Faces, plan.FaceCount * 16).CopyTo(faceData);
        _bytes.AsSpan(face, 16).CopyTo(faceData.AsSpan(plan.FaceCount * 16));
        BinaryPrimitives.WriteInt32LittleEndian(faceData.AsSpan(plan.FaceCount * 16), plan.EdgeCount);
        BinaryPrimitives.WriteUInt16LittleEndian(faceData.AsSpan(plan.FaceCount * 16 + 8), 3);
        byte[] edgeBytes = new byte[(plan.EdgeCount + 3) * 20]; _bytes.AsSpan(plan.Edges, plan.EdgeCount * 20).CopyTo(edgeBytes);
        void Int(int id, int off, int value) => BinaryPrimitives.WriteInt32LittleEndian(edgeBytes.AsSpan(id * 20 + off), value);
        for (int i = 0; i < 3; i++) _bytes.AsSpan(edge + 16, 4).CopyTo(edgeBytes.AsSpan((plan.EdgeCount + i) * 20 + 16));
        Int(edgeIndex, 8, plan.EdgeCount); Int(edgeIndex, 12, plan.FaceCount);
        int[] corners = [b, a, plan.VertexCount];
        for (int i = 0; i < 3; i++)
        {
            Int(plan.EdgeCount + i, 0, corners[i]); Int(plan.EdgeCount + i, 4, corners[(i + 1) % 3]);
            Int(plan.EdgeCount + i, 8, i == 0 ? edgeIndex : -1); Int(plan.EdgeCount + i, 12, i == 0 ? faceIndex : -1);
        }
        var append = new NavigationPackAppend(_bytes); append.Array(mesh.Object + 16, faceData, plan.FaceCount + 1);
        append.Array(mesh.Object + 32, edgeBytes, plan.EdgeCount + 3); append.Array(mesh.Object + 48, vertexData, plan.VertexCount + 1);
        void Grow(int header, int oldCount, int count, int template)
        {
            int stride = layout.Int(mesh.Object + (header == 80 ? 112 : 116)); if (stride == 0) return;
            var array = layout.Array(mesh, header, 4); byte[] data = new byte[(oldCount + count) * stride * 4]; _bytes.AsSpan(array.Address, oldCount * stride * 4).CopyTo(data);
            for (int i = 0; i < count; i++) _bytes.AsSpan(array.Address + template * stride * 4, stride * 4).CopyTo(data.AsSpan((oldCount + i) * stride * 4));
            append.Array(mesh.Object + header, data, (oldCount + count) * stride);
        }
        Grow(80, plan.FaceCount, 1, faceIndex); Grow(96, plan.EdgeCount, 3, edgeIndex);
        if (expandsBounds)
        {
            var minimum = Vector3.Min(min, point); var maximum = Vector3.Max(max, point);
            for (int axis = 0; axis < 3; axis++)
            {
                append.Int(mesh.Object + 128 + axis * 4, BitConverter.SingleToInt32Bits(minimum[axis]));
                append.Int(mesh.Object + 144 + axis * 4, BitConverter.SingleToInt32Bits(maximum[axis]));
            }
        }
        append.Int(plan.RootCount, 2); byte[] modified = append.Finish(); var modifiedPlan = Read(modified).PrepareInteriorDeformation(new Dictionary<int, Vector3>());
        return plan with { Modified = modified, ModifiedLayout = modifiedPlan with { RequiredFaceConnections = [faceIndex, plan.FaceCount] } };
    }
}
