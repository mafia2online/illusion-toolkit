using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledMeshBinding
{
    public sealed record DiagnosticChannels(IReadOnlyList<Vector2[]> UvSets, Vector4[]? Colors, byte[]? PackedWeights);

    /// <summary>Read native channels without requiring independent geometry ownership or changing any buffers.</summary>
    public DiagnosticChannels ReadDiagnosticChannels(int lod)
    {
        var set = _graph.Resolve(_graph.Field(_visual, "m_MeshSet"));
        var lods = _graph.Items(_graph.Field(set, "m_LODs"));
        if (lod < 0 || lods.Count == 0) throw new InvalidDataException("Missing native mesh level.");
        var mesh = _graph.Resolve(_graph.Field(lods[Math.Min(lod, lods.Count - 1)], "m_Mesh"));
        var declaration = _graph.Data(_graph.Field(mesh, "m_VFFlags")!);
        var streams = _graph.Items(_graph.Field(mesh, "m_VertexWrapper"));
        if (declaration.Length != 12 || streams.Count != 3) throw new InvalidDataException("Invalid native vertex streams.");
        List<Vector2[]> uvs = [];
        Vector4[]? colors = null; byte[]? weights = null;
        for (int stream = 0; stream < 3; stream++)
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(declaration[(stream * 4)..]);
            const uint supported = 1 | 2 | 4 | 0x10 | 0x40 | 0x80 | 0x1f00 | 0x20000 | 0x40000
                | 0x80000 | 0x100000 | 0x200000 | 0x2000000 | 0x4000000 | 0x8000000 | 0x10000000 | 0x40000000;
            if ((flags & ~supported) != 0 || (flags & 3) == 3)
                throw new NotSupportedException("The native diagnostic vertex declaration is not decoded safely.");
            var layout = CompiledMeshDecoder.Layout(flags);
            if (layout.Stride == 0) continue;
            var buffer = _graph.Resolve(_graph.Field(streams[stream], "m_Buffer"));
            var data = _graph.Data(_graph.Field(buffer, "m_Data")!);
            int offset = checked((int)_graph.Integer(_graph.Field(streams[stream], "m_Offset")!));
            int count = checked((int)_graph.Integer(_graph.Field(streams[stream], "m_VertexCount")!));
            if (offset < 0 || (long)count * layout.Stride > data.Length - offset) throw new InvalidDataException("Native vertex stream exceeds its buffer.");
            if (layout.Color >= 0) colors = new Vector4[count];
            if (layout.Skin >= 0) weights = new byte[checked(count * 4)];
            List<(int Offset, Vector2[] Values)> sets = [];
            int uvOffset = layout.Uv;
            for (int channel = 0; channel < 5; channel++)
            {
                bool alias = channel == 3 && (flags & 0x1800) == 0x1000;
                if ((flags & (256u << channel)) == 0 && !alias) continue;
                if (uvOffset < 0 || uvOffset + 4 > layout.Stride) throw new InvalidDataException("Native UV channel exceeds its vertex.");
                var values = new Vector2[count]; sets.Add((uvOffset, values)); uvs.Add(values);
                if (!alias) uvOffset += 4;
            }
            for (int vertex = 0; vertex < count; vertex++)
            {
                var source = data.Slice(offset + vertex * layout.Stride, layout.Stride);
                if (layout.Color >= 0) colors![vertex] = new Vector4(source[layout.Color + 2], source[layout.Color + 1], source[layout.Color], source[layout.Color + 3]) / 255f;
                if (layout.Skin >= 0) source.Slice(layout.Skin, 4).CopyTo(weights!.AsSpan(vertex * 4));
                foreach (var channel in sets)
                    channel.Values[vertex] = new((float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(source[channel.Offset..])),
                        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(source[(channel.Offset + 2)..])));
            }
        }
        return new(uvs, colors, weights);
    }
}
