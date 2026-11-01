using System.Buffers.Binary;
using System.Numerics;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Native UV paint mapping's explicit mesh/level/channel, without face-group or scene-name inference.</summary>
internal sealed record CompiledPaintMapping(Vector3[] Positions, Vector3[] ImagePositions, uint[] Indices)
{
    // C_VertexTranslator zeroes exponent-zero half values and expands exponent 31 as a finite float.
    internal static float NativeHalf(ushort value) => (value & 0x7c00) == 0 ? 0 :
        BitConverter.UInt32BitsToSingle((((uint)value >> 10 & 31) << 23) + 0x38000000u
            | ((uint)value << 16 & 0x80000000u) | ((uint)value << 13 & 0x7fe000u));

    internal static CompiledPaintMapping Read(CompiledGraph graph, CompiledElement mapping, Matrix4x4 world)
    {
        int I(CompiledElement node, string name) => checked((int)graph.Integer(graph.Field(node, name)
            ?? throw new InvalidDataException($"Paint mapping lacks {name}.")));
        var set = graph.Resolve(graph.Field(mapping, "m_MeshSet")) ?? throw new InvalidDataException("Paint mapping mesh set is unresolved.");
        var levels = graph.Items(graph.Field(set, "m_LODs")); int level = I(mapping, "m_LOD"), channel = I(mapping, "m_UVChannel");
        if ((uint)level >= levels.Count || channel is < 0 or > 4) throw new InvalidDataException("Invalid paint mapping level/channel.");
        var mesh = graph.Resolve(graph.Field(levels[level], "m_Mesh")) ?? throw new InvalidDataException("Paint mapping mesh is unresolved.");
        var vf = graph.Data(graph.Field(mesh, "m_VFFlags") ?? throw new InvalidDataException("Paint mesh has no vertex declaration."));
        var wrappers = graph.Items(graph.Field(mesh, "m_VertexWrapper"));
        if (vf.Length != 12 || wrappers.Count != 3) throw new InvalidDataException("Unknown paint mesh vertex declaration.");
        var compression = graph.Data(graph.Field(set, "m_PosCompression") ?? throw new InvalidDataException("Paint mapping has no position compression."));
        if (compression.Length < 16) throw new InvalidDataException("Invalid paint position compression.");
        var origin = CompiledMeshDecoder.Vector(compression); float scale = CompiledMeshDecoder.Float(compression, 12);
        Vector3[]? positions = null, image = null;
        for (int stream = 0; stream < wrappers.Count; stream++)
        {
            var layout = CompiledMeshDecoder.Layout(BinaryPrimitives.ReadUInt32LittleEndian(vf[(stream * 4)..]));
            int uv = CompiledMeshDecoder.UvOffset(layout, channel);
            if (layout.Position < 0 && uv < 0) continue;
            int count = I(wrappers[stream], "m_VertexCount"), offset = I(wrappers[stream], "m_Offset");
            var buffer = graph.Resolve(graph.Field(wrappers[stream], "m_Buffer")) ?? throw new InvalidDataException("Paint vertex buffer is unresolved.");
            var bytes = graph.Data(graph.Field(buffer, "m_Data") ?? throw new InvalidDataException("Paint vertex data is absent."));
            if (count < 0 || offset < 0 || layout.Stride * (long)count > bytes.Length - offset) throw new InvalidDataException("Paint vertex stream is truncated.");
            if (layout.Position >= 0) positions = new Vector3[count];
            if (uv >= 0) image = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                var row = bytes.Slice(offset + i * layout.Stride, layout.Stride);
                if (layout.Position >= 0)
                {
                    var raw = new Vector3(BinaryPrimitives.ReadUInt16LittleEndian(row[layout.Position..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(row[(layout.Position + 2)..]),
                        (layout.Flags & 1) != 0 ? BinaryPrimitives.ReadUInt16LittleEndian(row[(layout.Position + 4)..]) & 0x7fff : 0);
                    var local = origin + raw * scale;
                    // Native AddVertex uses three multiply/add terms then translation, without UV inversion.
                    positions![i] = new(((local.X * world.M11 + local.Y * world.M21) + local.Z * world.M31) + world.M41,
                        ((local.X * world.M12 + local.Y * world.M22) + local.Z * world.M32) + world.M42,
                        ((local.X * world.M13 + local.Y * world.M23) + local.Z * world.M33) + world.M43);
                }
                if (uv >= 0) image![i] = new(NativeHalf(BinaryPrimitives.ReadUInt16LittleEndian(row[uv..])),
                    NativeHalf(BinaryPrimitives.ReadUInt16LittleEndian(row[(uv + 2)..])), 0);
            }
        }
        if (positions is null || image is null || positions.Length != image.Length) throw new NotSupportedException("Paint mapping lacks matching native positions and selected UVs.");
        var index = graph.Resolve(graph.Field(graph.Field(mesh, "m_IndexWrapper"), "m_Buffer")) ?? throw new InvalidDataException("Paint index buffer is unresolved.");
        int size = I(index, "m_Format") == 2 ? 4 : 2;
        var rawIndices = graph.Data(graph.Field(index, "m_Data") ?? throw new InvalidDataException("Paint indices are absent."));
        if (rawIndices.Length % (3 * size) != 0) throw new InvalidDataException("Paint index stream has incomplete triangles.");
        var indices = new uint[rawIndices.Length / size];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(rawIndices[(i * 2)..]) : BinaryPrimitives.ReadUInt32LittleEndian(rawIndices[(i * 4)..]);
            if (indices[i] >= positions.Length) throw new InvalidDataException("Paint triangle index is out of range.");
        }
        return new(positions, image, indices);
    }
}
