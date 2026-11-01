using System.Buffers.Binary;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    internal IReadOnlyList<KeyValuePair<ulong, string>> PreparedProxyMaterialSignatures()
        => _linkedProxyStates.Values.SelectMany(state => state.MaterialTextures).ToArray();

    private void CaptureProxyMaterialSignatures(ProxyRuntimeState state)
    {
        foreach (ulong hash in ProxyMaterialHashes(state.Pairs.Values, lowest: false))
            state.MaterialTextures.Add(hash, CompiledProxyMaterialGuard.TextureSignature(hash)
                ?? throw new InvalidDataException($"Native material {hash:X16} is missing from the prepared proxy."));
        if (state.MaterialTextures.Count == 0) throw new InvalidDataException("A prepared proxy has no material dependencies.");
    }
    private static string? ValidateProxyMaterialSignatures(ProxyRuntimeState state)
    {
        foreach (var material in state.MaterialTextures)
            if (CompiledProxyMaterialGuard.TextureSignature(material.Key) != material.Value)
                return $"Material {material.Key:X16} changed texture slots or identity after proxy preparation. Undo that material change before saving or transforming the proxy.";
        return null;
    }
    private HashSet<ulong> ProxyMaterialHashes(IEnumerable<CompiledFrameNode> frames, bool lowest)
    {
        var hashes = new HashSet<ulong>();
        foreach (var frame in frames)
            foreach (var visual in ProxyVisuals(frame))
            {
                var set = Graph.Resolve(Graph.Field(visual, "m_MeshSet"));
                int level = lowest ? Graph.Items(Graph.Field(set, "m_LODs")).Count - 1 : 0;
                var face = Graph.Resolve(Graph.Field(visual, "m_FaceGroupSet"));
                var tables = Graph.Items(Graph.Field(Graph.Field(face, "m_Data"), "m_FaceGroups"));
                if (level < 0 || tables.Count == 0) throw new InvalidDataException("Missing native proxy material table.");
                var bytes = Graph.Data(tables[Math.Min(level, tables.Count - 1)]);
                if (bytes.Length < 4) throw new InvalidDataException("Invalid native proxy material table.");
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                if (bytes.Length != 4L + count * 40L) throw new InvalidDataException("Unknown native proxy material table layout.");
                for (int i = 0; i < count; i++)
                {
                    ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(12 + i * 40)..]);
                    if (hash != 0) hashes.Add(hash);
                }
            }
        return hashes;
    }
}
