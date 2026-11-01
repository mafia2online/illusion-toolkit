using Illusion.Bridge.Payload;
using Illusion.Domain;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private static MeshObjectPayload RebaseProxyGeometryDonors(MeshObjectPayload payload, MeshData from, MeshData to, bool positionsMayRequantize = false)
    {
        if (from.Indices.Length != to.Indices.Length || from.Parts.Length != to.Parts.Length
            || from.Parts.Where((part, index) => part.MaterialHash != to.Parts[index].MaterialHash || part.IndexCount != to.Parts[index].IndexCount).Any())
            throw new InvalidDataException("Native donor topology no longer corresponds across proxy ownership.");
        var mapping = new Dictionary<int, int>();
        for (int corner = 0; corner < from.Indices.Length; corner++)
        {
            int oldIndex = checked((int)from.Indices[corner]), newIndex = checked((int)to.Indices[corner]);
            if (!positionsMayRequantize && from.Positions[oldIndex] != to.Positions[newIndex])
                throw new InvalidDataException("Native donor positions changed during ownership isolation.");
            if (mapping.TryGetValue(oldIndex, out int previous) && previous != newIndex)
                throw new InvalidDataException("Native donor seam splitting is ambiguous across proxy ownership.");
            mapping[oldIndex] = newIndex;
        }
        var exchange = new ExchangeContainer(); MeshPayloadCodec.Add(exchange, payload);
        var copy = MeshPayloadCodec.Read(exchange, exchange.Objects.Single());
        copy.LoopOrigIndex = payload.LoopOrigIndex.Select(index => index < 0 ? index : mapping.TryGetValue(index, out int value) ? value
            : throw new InvalidDataException("An authored donor points outside the retained native visual partition.")).ToArray();
        return copy;
    }
}
