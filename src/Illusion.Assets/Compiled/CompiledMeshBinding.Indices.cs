using System.Buffers.Binary;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledMeshBinding
{
    private List<CompiledSubdivisionRefit.Change> VisualCollisionChanges(int lod, CompiledElement mesh, System.Numerics.Vector3[] positions)
    {
        var overrides = _graph.Items(_graph.Field(_visual, "m_CDSubdiv"));
        if (overrides.Count == 0) return [];
        var cd = _graph.Resolve(overrides[Math.Min(lod, overrides.Count - 1)]);
        if (cd is null || ReferenceEquals(cd, _graph.Resolve(_graph.Field(mesh, "m_MeshCDSubdiv")))) return [];
        if (cd.TypeId == 0x17bf41a8)
        {
            // C_SingleMeshCollisionSubdiv stores format/counts and joint/triangle ranges only.
            // Its optional box hierarchy belongs to the separate visual definition. With no
            // definition, native ProcessTris traverses these groups directly; fixed topology
            // needs no spatial refit and must retain every palette/range byte.
            var definition = _graph.Field(_visual, "m_Def");
            if (_graph.Resolve(definition) is not null || definition?.Values.FirstOrDefault() is { DataSize: >= 8 } reference && _graph.Integer(reference) != 0)
                throw new InvalidDataException("The visual has a precomputed collision definition; geometry changes require its box hierarchy rebuild.");
            var descriptor = _graph.Field(cd, "m_Data"); var groups = _graph.Field(cd, "m_Groups");
            if (descriptor?.DataSize != 16 || groups is null) throw new InvalidDataException("Unsupported single-mesh collision descriptor.");
            var data = _graph.Data(descriptor); var ranges = _graph.Data(groups);
            var indices = FullIndices(mesh, positions.Length);
            if (ranges.Length < 4 || ranges.Length != 4L + BinaryPrimitives.ReadUInt32LittleEndian(ranges) * 16L
                || BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != positions.Length)
                throw new InvalidDataException("Single-mesh collision counts do not match native geometry.");
            for (int offset = 4; offset < ranges.Length; offset += 16)
                if (BinaryPrimitives.ReadUInt32LittleEndian(ranges[offset..]) > 256
                    || (ulong)BinaryPrimitives.ReadUInt32LittleEndian(ranges[(offset + 4)..]) + 3UL * BinaryPrimitives.ReadUInt32LittleEndian(ranges[(offset + 8)..]) > (ulong)indices.Length
                    || BinaryPrimitives.ReadUInt32LittleEndian(ranges[(offset + 12)..]) > 4)
                    throw new InvalidDataException("Invalid single-mesh collision joint/triangle ranges.");
            return [];
        }
        var opcode = _graph.Field(cd, "m_OpcodeData");
        if (cd.TypeId != 0x4dc47de8 || opcode?.TypeId != 0x3a9e63f8)
            throw new InvalidDataException("Unsupported independent visual collision subdivision.");
        var min = positions.Aggregate(new System.Numerics.Vector3(float.PositiveInfinity), System.Numerics.Vector3.Min);
        var max = positions.Aggregate(new System.Numerics.Vector3(float.NegativeInfinity), System.Numerics.Vector3.Max);
        byte[] before = _graph.Data(opcode).ToArray();
        return [new(opcode, before, CompiledSubdivisionRefit.RefitOpcode(before, min, max))];
    }

    private uint[] FullIndices(CompiledElement mesh, int vertexCount)
    {
        var buffer = _graph.Resolve(_graph.Field(_graph.Field(mesh, "m_IndexWrapper"), "m_Buffer"));
        var field = _graph.Field(buffer, "m_Data") ?? throw new InvalidDataException("Unresolved native index buffer.");
        var format = _graph.Field(buffer, "m_Format") ?? throw new InvalidDataException("Missing native index format.");
        ulong value = _graph.Integer(format);
        if (value is not (1 or 2)) throw new InvalidDataException("Unsupported native index format.");
        int stride = value == 1 ? 2 : 4; var data = _graph.Data(field);
        if (data.Length % (3 * stride) != 0) throw new InvalidDataException("Native indices do not form complete triangles.");
        var indices = new uint[data.Length / stride];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = stride == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(data[(i * stride)..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[(i * stride)..]);
            if (indices[i] >= vertexCount) throw new InvalidDataException("Native index exceeds the vertex allocation.");
        }
        return indices;
    }
}
