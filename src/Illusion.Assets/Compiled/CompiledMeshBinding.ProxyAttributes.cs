using Illusion.Assets.Bridge;
using Illusion.Bridge.Payload;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledMeshBinding
{
    internal IReadOnlySet<CompiledElement> ProxyLodStorage(int lod) => Read(lod).Streams.Select(s => s.Data).ToHashSet();

    internal BridgeMeshApplier.ApplyResult RestrictedProxyAttributes(MeshObjectPayload payload, int lod)
    {
        var context = Read(lod);
        var expected = Payload(context, payload.Id);
        if (context.Bind is { } bind) TransformPayload(expected, bind);
        if (context.Streams.Any(s => s.Layout.Skin >= 0 || s.Layout.Morph >= 0)
            || !payload.Positions.SequenceEqual(expected.Positions)
            || !payload.LoopVertexIndices.SequenceEqual(expected.LoopVertexIndices)
            || !payload.LoopOrigIndex.SequenceEqual(expected.LoopOrigIndex)
            || !payload.FaceMaterials.SequenceEqual(expected.FaceMaterials)
            || payload.BoneIndices.Length != 0 || payload.BoneWeights.Length != 0 || payload.SkeletonId is not null)
            throw new InvalidDataException("Prepared proxy geometry permits only static count-preserving UV, normal and color edits; positions and topology must remain exact.");
        var result = TryApplyCore(payload, lod, out var reason) ?? throw new InvalidDataException(reason);
        if (result.Requantized || result.CompiledTopologyRebuilt || result.NewMesh is not { } mesh
            || mesh.Positions.Length != context.Mesh.Positions.Length || !mesh.Indices.SequenceEqual(context.Mesh.Indices)
            || result.CompiledWriteFields.Any(f => !ReferenceEquals(f, context.NativeMesh) && !context.Streams.Any(s => ReferenceEquals(s.Data, f))))
            throw new InvalidDataException("Prepared proxy attributes cannot rebuild topology, bounds or compression metadata.");
        // Validate every actual changed native bit, including bytes outside this visual's vertex slice.
        // The packed POSITION z high bit stores tangent handedness; its other fifteen bits are immutable.
        var masks = context.Streams.Select(s => s.Data).Distinct().ToDictionary(f => f, f => new byte[_graph.Data(f).Length]);
        foreach (var stream in context.Streams)
        {
            var layout = stream.Layout; var mask = masks[stream.Data];
            for (int vertex = 0; vertex < stream.Count; vertex++)
            {
                int start = stream.Offset + vertex * layout.Stride;
                void Allow(int offset, int length) { if (offset >= 0) Array.Fill(mask, (byte)255, start + offset, length); }
                Allow(layout.Normal, 3);
                foreach (var slot in AttributeSlots(context).Where(s => ReferenceEquals(s.Stream, stream))) Allow(slot.Offset, 4);
                if ((layout.Flags & 0x14) == 0x14)
                {
                    if (layout.Normal < 4) throw new InvalidDataException("Unknown tangent frame packing.");
                    Allow(layout.Normal - 2, 2); Allow(layout.Normal + 3, 1);
                    mask[start + layout.Normal - 3] |= 0x80;
                }
            }
        }
        int cursor = 0;
        foreach (var stream in context.Streams)
        {
            var mask = masks[stream.Data];
            for (int index = 0; index < stream.Original.Length; index++)
                if (((result.OldVertexData[cursor + index] ^ result.NewVertexData[cursor + index]) & ~mask[index]) != 0)
                    throw new InvalidDataException("Prepared proxy edit changes a protected native vertex channel.");
            cursor += stream.Original.Length;
        }
        if (cursor != result.NewVertexData.Length) throw new InvalidDataException("Unknown compiled attribute write layout.");
        return result;
    }
}
