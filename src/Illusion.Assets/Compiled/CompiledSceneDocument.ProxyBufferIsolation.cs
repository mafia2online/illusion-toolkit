using System.Buffers.Binary;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    // Only called on newly cloned mesh definitions. Native wrappers address byte ranges inside
    // pooled buffers; changing their topology requires independent allocations, not resizing pools.
    private void IsolateCopiedVertexAllocations(CompiledElement mesh, List<GeometryCopy> copies,
        HashSet<CompiledElement> detached)
    {
        var declaration = Graph.Field(mesh, "m_VFFlags");
        var wrappers = Graph.Items(Graph.Field(mesh, "m_VertexWrapper"));
        if (declaration?.DataSize != 12 || wrappers.Count != 3)
            throw new InvalidDataException("Unknown copied native vertex declaration.");
        var plans = new List<(CompiledElement Reference, CompiledElement Offset, CompiledElement Buffer, byte[] Bytes)>();
        var ranges = new List<(CompiledElement Buffer, int Offset, int Length)>();
        for (int stream = 0; stream < wrappers.Count; stream++)
        {
            var layout = CompiledMeshDecoder.Layout(BinaryPrimitives.ReadUInt32LittleEndian(Graph.Data(declaration)[(stream * 4)..]));
            if (layout.Stride == 0) continue;
            var reference = Graph.Field(wrappers[stream], "m_Buffer") ?? throw new InvalidDataException("Missing copied vertex buffer reference.");
            var buffer = Graph.Resolve(reference) ?? throw new InvalidDataException("Unresolved copied vertex buffer.");
            var data = Graph.Field(buffer, "m_Data") ?? throw new InvalidDataException("Missing copied vertex allocation.");
            var offsetField = Graph.Field(wrappers[stream], "m_Offset");
            var countField = Graph.Field(wrappers[stream], "m_VertexCount");
            if (offsetField?.DataSize != 4 || countField?.DataSize != 4)
                throw new InvalidDataException("Unknown copied vertex allocation range.");
            int offset = checked((int)Graph.Integer(offsetField)), count = checked((int)Graph.Integer(countField));
            int length = checked(count * layout.Stride);
            if (offset < 0 || length > data.DataSize - offset)
                throw new InvalidDataException("Copied vertex range exceeds its native pool.");
            if (ranges.Any(range => ReferenceEquals(range.Buffer, buffer) && offset < (long)range.Offset + range.Length
                && range.Offset < (long)offset + length))
                throw new InvalidDataException("Overlapping compiled vertex stream ranges cannot be isolated safely.");
            ranges.Add((buffer, offset, length));
            bool shared = wrappers.Count(wrapper => ReferenceEquals(Graph.Resolve(Graph.Field(wrapper, "m_Buffer")), buffer)) > 1;
            if (offset == 0 && length == data.DataSize && !shared) continue;
            plans.Add((reference, offsetField, buffer, Graph.Data(data).Slice(offset, length).ToArray()));
        }
        foreach (var plan in plans)
        {
            var copy = new GeometryCopy(this); copies.Add(copy);
            var buffer = copy.CloneDefinition(plan.Buffer, followDependencies: false); copy.Finish();
            var data = Graph.Field(buffer, "m_Data")!;
            Graph.Owner(data).ReplaceVariable(data, plan.Bytes);
            ReplaceReference(plan.Reference, buffer.ObjectId, detached);
            Graph.Owner(plan.Offset).Replace(plan.Offset, new byte[4]);
        }
    }
}
