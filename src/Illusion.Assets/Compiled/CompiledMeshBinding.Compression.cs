using System.Buffers.Binary;
using System.Numerics;
using Illusion.Bridge.Payload;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledMeshBinding
{
    private Context PrepareCompression(MeshObjectPayload payload, Context context, int lod)
    {
        var pushed = payload.LoopVertexIndices.Select(index => index < payload.Positions.Length
            ? payload.Positions[index] : throw new InvalidDataException("Invalid pushed vertex index.")).ToArray();
        if (pushed.Length == 0 || pushed.Any(p => !Finite(p))) throw new InvalidDataException("Invalid pushed positions.");
        bool Fits(Vector3 p)
        {
            var q = (p - context.Origin) / context.Scale;
            return q.X >= -0.01f && q.Y >= -0.01f && q.Z >= -0.01f && q.X <= 65535.01f && q.Y <= 65535.01f && q.Z <= 32767.01f;
        }
        if (pushed.All(Fits)) return context;
        var set = _graph.Resolve(_graph.Field(_visual, "m_MeshSet"))!;
        if (_graph.Field(set, "m_MorphMapHash") is { } morph && _graph.Integer(morph) != 0
            || _graph.Resolve(_graph.Field(set, "m_MorphTextureData")) is not null
            || _graph.Field(set, "m_CorrectionMorphDataHash") is { } correction && _graph.Integer(correction) != 0
            || _graph.Field(set, "m_CorrectionMorphDataList") is { } list
                && (_graph.Data(list).Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(_graph.Data(list)) != 0))
            throw new InvalidDataException("Morphing meshes require a matching morph compression rebuild.");
        var levels = Enumerable.Range(0, LodCount).Select(Read).DistinctBy(c => c.NativeMesh).ToArray();
        if (levels.Any(c => _graph.Resolve(_graph.Field(c.NativeMesh, "m_InternalMorphMapTextureData")) is not null))
            throw new InvalidDataException("Morphing meshes require a matching morph compression rebuild.");
        var all = levels.SelectMany(c => c.Mesh.Positions).Concat(pushed).ToArray();
        Vector3 min = all.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min);
        Vector3 max = all.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max);
        var extent = max - min;
        float scale = Math.Max(context.Scale, Math.Max(Math.Max(extent.X / 65532f, extent.Y / 65532f), extent.Z / 32764f));
        // Reserve one quantization unit on either side; round upward to avoid float overflow at the upper endpoint.
        scale = MathF.BitIncrement(scale); var origin = min - new Vector3(scale);
        if (!Finite(origin) || !float.IsFinite(scale) || scale <= 0 || !float.IsFinite(1 / scale))
            throw new InvalidDataException("Position range cannot be represented by native finite compression coefficients.");
        var changes = new Dictionary<CompiledElement, CompiledSubdivisionRefit.Change>();
        void Change(CompiledElement field, byte[] after) => changes[field] = new(field, _graph.Data(field).ToArray(), after);
        var coefficients = _graph.Field(set, "m_PosCompression")!;
        if (coefficients.DataSize != 80) throw new InvalidDataException("Unsupported native position-compression layout.");
        var bytes = _graph.Data(coefficients).ToArray(); WriteVector(bytes, 0, origin);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), scale); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), 1 / scale);
        Change(coefficients, bytes);
        foreach (var level in levels.Where(c => !ReferenceEquals(c.NativeMesh, context.NativeMesh)))
        {
            var lods = _graph.Items(_graph.Field(set, "m_LODs"));
            var positions = (Vector3[])level.Mesh.Positions.Clone();
            foreach (var stream in level.Streams.Where(s => s.Layout.Position >= 0))
            {
                var data = (byte[])stream.Original.Clone(); int p = stream.Layout.Position;
                for (int vertex = 0; vertex < positions.Length; vertex++)
                {
                    var value = (positions[vertex] - origin) / scale;
                    int x = Quantize(value.X, 65535), y = Quantize(value.Y, 65535), z = Quantize(value.Z, 32767);
                    var target = data.AsSpan(stream.Offset + vertex * stream.Layout.Stride);
                    ushort sign = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(target[(p + 4)..]) & 0x8000);
                    BinaryPrimitives.WriteUInt16LittleEndian(target[p..], (ushort)x); BinaryPrimitives.WriteUInt16LittleEndian(target[(p + 2)..], (ushort)y);
                    BinaryPrimitives.WriteUInt16LittleEndian(target[(p + 4)..], (ushort)(z | sign));
                    positions[vertex] = origin + new Vector3(x, y, z) * scale;
                }
                Change(stream.Data, data);
            }
            foreach (var change in CompiledSubdivisionRefit.Compute(_graph, level.NativeMesh, positions)) Change(change.Field, change.After);
            foreach (int index in Enumerable.Range(0, lods.Count).Where(i => ReferenceEquals(
                _graph.Resolve(_graph.Field(lods[i], "m_Mesh")), level.NativeMesh)))
                foreach (var change in VisualCollisionChanges(index, level.NativeMesh, positions)) Change(change.Field, change.After);
            var boundsContext = context with { Pending = changes.Values.ToArray() };
            foreach (var change in ExpandBounds(boundsContext, positions)) Change(change.Field, change.After);
        }
        return context with { Origin = origin, Scale = scale, Pending = changes.Values.ToArray(), Requantized = true };
    }

    private List<CompiledSubdivisionRefit.Change> ExpandBounds(Context context, Vector3[] positions)
    {
        var changes = new List<CompiledSubdivisionRefit.Change>();
        void Box(CompiledElement? field, int offset, Vector3[] points)
        {
            if (field is null) return;
            if (field.DataSize < offset + 24) throw new InvalidDataException("Truncated compiled culling bounds.");
            var before = _graph.Data(field).ToArray();
            var bytes = context.Pending?.LastOrDefault(c => ReferenceEquals(c.Field, field))?.After.ToArray() ?? (byte[])before.Clone();
            var min = CompiledMeshDecoder.Vector(bytes.AsSpan(offset)); var max = CompiledMeshDecoder.Vector(bytes.AsSpan(offset + 12));
            if (!Finite(min) || !Finite(max) || Vector3.Min(min, max) != min) throw new InvalidDataException("Invalid compiled culling bounds.");
            foreach (var point in points) { min = Vector3.Min(min, point); max = Vector3.Max(max, point); }
            WriteVector(bytes, offset, min); WriteVector(bytes, offset + 12, max);
            if (!before.AsSpan().SequenceEqual(bytes)) changes.Add(new(field, before, bytes));
        }
        var data = _graph.Field(_visual, "m_Data");
        if (data?.DataSize != 88) throw new InvalidDataException("Unsupported compiled visual bounds layout.");
        Box(data, 16, positions);
        Box(_graph.Field(_graph.Resolve(_graph.Field(_visual, "m_FaceGroupSet")), "m_BBox"), 0, positions);
        var local = context.Bind is { } bind ? positions.Select(p => Vector3.Transform(p, bind)).ToArray() : positions;
        var canonical = (_frame as ICompiledMeshSource)?.CanonicalFrame ?? _frame;
        for (var frame = canonical as CompiledSceneDocument.CompiledFrameNode; frame is not null; frame = frame.ParentFrame)
        {
            foreach (var component in _graph.Items(_graph.Field(frame.Element, "m_Components")).Select(_graph.Resolve))
                Box(_graph.Field(component, "m_LocalAABB"), 0, local);
            local = local.Select(p => Vector3.Transform(p, frame.LocalTransform)).ToArray();
        }
        return changes;
    }

    private static void WriteVector(byte[] bytes, int offset, Vector3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 4), value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 8), value.Z);
    }
}
