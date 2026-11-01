using System.Buffers.Binary;
using System.Numerics;
using Illusion.Assets.Bridge;
using Illusion.Bridge.Payload;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledMeshBinding
{
    internal BridgeMeshApplier.ApplyResult ProxyStaticGeometry(MeshObjectPayload payload, int lod,
        Vector3? forcedOrigin = null, float forcedScale = 0)
    {
        var context = Read(lod);
        if (context.Streams.Any(stream => stream.Layout.Skin >= 0 || stream.Layout.Morph >= 0)
            || payload.BoneIndices.Length != 0 || payload.BoneWeights.Length != 0 || payload.SkeletonId is not null)
            throw new InvalidDataException("Prepared replacement supports verified static geometry only; skin and morph resources require their own regeneration.");
        ValidateAttributes(payload, context);
        if (context.Bind is { } bind)
        {
            var exchange = new ExchangeContainer();
            MeshPayloadCodec.Add(exchange, payload); payload = MeshPayloadCodec.Read(exchange, exchange.Objects.Single());
            if (!Matrix4x4.Invert(bind, out var inverse)) throw new InvalidDataException("The immutable rigid binding is singular.");
            TransformPayload(payload, inverse);
        }
        payload = RestoreNativeOnlyFaces(payload, context);
        if (forcedOrigin is { } origin)
        {
            if (!Finite(origin) || !float.IsFinite(forcedScale) || forcedScale <= 0)
                throw new InvalidDataException("Proxy regeneration needs finite compression.");
            var coefficients = _graph.Field(_graph.Resolve(_graph.Field(_visual, "m_MeshSet")), "m_PosCompression")!;
            if (coefficients.DataSize != 80) throw new InvalidDataException("Unknown far position-compression layout.");
            var before = _graph.Data(coefficients).ToArray(); var after = before.ToArray();
            WriteVector(after, 0, origin); BinaryPrimitives.WriteSingleLittleEndian(after.AsSpan(12), forcedScale);
            BinaryPrimitives.WriteSingleLittleEndian(after.AsSpan(16), 1 / forcedScale);
            context = context with { Origin = origin, Scale = forcedScale, Requantized = !before.AsSpan().SequenceEqual(after),
                Pending = [new CompiledSubdivisionRefit.Change(coefficients, before, after)] };
            // Rebuild the far buffers from the same native donors and authored corner channels, so
            // source compression, seam splitting, material ranges and spatial trees remain coherent.
            return Rebuild(payload, context, lod);
        }
        context = PrepareCompression(payload, context, lod);
        return Rebuild(payload, context, lod);
    }
}
