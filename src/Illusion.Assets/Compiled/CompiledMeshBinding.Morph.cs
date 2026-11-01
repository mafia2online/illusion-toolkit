using System.Buffers.Binary;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledMeshBinding
{
    internal bool OwnedBy(CompiledGraph graph) => ReferenceEquals(_graph, graph);
    internal ICompiledMeshSource? MeshSource => _frame as ICompiledMeshSource;
    public sealed record MorphChannels(string? TextureName, ulong[] TargetIds, CompiledMorphAtlas.Influence[][] Vertices);

    /// <summary>Reads native expression channels without requiring unique geometry or mutating scene resources.</summary>
    public MorphChannels ReadMorphChannels(int lod, ReadOnlySpan<byte> atlasDds) => ReadMorphChannels(_graph, _visual, lod, atlasDds);

    internal static MorphChannels ReadMorphChannels(CompiledGraph graph, CompiledElement visual, int lod, ReadOnlySpan<byte> atlasDds)
    {
        var set = graph.Resolve(graph.Field(visual, "m_MeshSet"));
        var levels = graph.Items(graph.Field(set, "m_LODs"));
        if (lod < 0 || lod >= levels.Count) throw new ArgumentOutOfRangeException(nameof(lod));
        var mesh = graph.Resolve(graph.Field(levels[lod], "m_Mesh"));
        var records = graph.Data(graph.Field(set, "m_CorrectionMorphDataList")!);
        if (records.Length < 4) throw new InvalidDataException("Missing native correction morph table.");
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(records));
        if (records.Length != checked(4 + count * 48)) throw new NotSupportedException("Unknown native correction morph record layout.");
        var targets = new ulong[count];
        for (int i = 0; i < count; i++) targets[i] = BinaryPrimitives.ReadUInt64LittleEndian(records[(4 + i * 48 + 32)..]);
        var precisionData = graph.Data(graph.Field(set, "m_MorphPrecisionStepMultiplier")!);
        if (precisionData.Length != 4) throw new InvalidDataException("Invalid native morph precision.");
        float precision = BinaryPrimitives.ReadSingleLittleEndian(precisionData);
        int alignment = checked((int)graph.Integer(graph.Field(set, "m_MorphPixelAlignment")!));
        var atlas = new CompiledMorphAtlas(atlasDds);
        var declaration = graph.Data(graph.Field(mesh, "m_VFFlags")!);
        var streams = graph.Items(graph.Field(mesh, "m_VertexWrapper"));
        if (declaration.Length != 12 || streams.Count != 3) throw new InvalidDataException("Invalid morph vertex declaration.");
        CompiledMorphAtlas.Influence[][]? vertices = null;
        for (int i = 0; i < 3; i++)
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(declaration[(i * 4)..]);
            const uint supported = 1 | 2 | 4 | 0x10 | 0x40 | 0x80 | 0x1f00 | 0x20000 | 0x40000
                | 0x80000 | 0x100000 | 0x200000 | 0x2000000 | 0x4000000 | 0x8000000 | 0x10000000 | 0x40000000;
            if ((flags & ~supported) != 0 || (flags & 3) == 3)
                throw new NotSupportedException("The morph vertex declaration contains unsupported channels.");
            var layout = CompiledMeshDecoder.Layout(flags);
            if (layout.Morph < 0) continue;
            if (vertices is not null) throw new NotSupportedException("Multiple native morph vertex streams are unsupported.");
            var data = graph.Data(graph.Field(graph.Resolve(graph.Field(streams[i], "m_Buffer")), "m_Data")!);
            int offset = checked((int)graph.Integer(graph.Field(streams[i], "m_Offset")!));
            int vertexCount = checked((int)graph.Integer(graph.Field(streams[i], "m_VertexCount")!));
            if (offset < 0 || vertexCount < 0 || (long)vertexCount * layout.Stride > data.Length - offset)
                throw new InvalidDataException("Native morph stream exceeds its buffer.");
            vertices = new CompiledMorphAtlas.Influence[vertexCount][];
            for (int vertex = 0; vertex < vertexCount; vertex++)
                vertices[vertex] = atlas.ReadVertex(data.Slice(offset + vertex * layout.Stride + layout.Morph, 4), alignment, precision, count);
        }
        if (vertices is null) throw new NotSupportedException("The mesh has no sparse morph declaration.");
        var token = graph.Field(mesh, "m_ExternalMorphMapTexture");
        return new(token is null ? null : CompiledDependencyToken.ResolveName(graph, token), targets, vertices);
    }
}
