using Illusion.Assets.Textures;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private sealed record MorphCacheEntry(string Path, long Length, DateTime Modified,
        (CompiledResource Resource, long Revision)[] Resources, CompiledMeshBinding.MorphChannels Channels);
    private readonly Dictionary<(CompiledMeshBinding Binding, int Lod), MorphCacheEntry> _morphChannelsCache = [];
    private readonly record struct InlineMorphSource(CompiledElement Texture, CompiledElement Pixels, int Width, int Height);

    private InlineMorphSource? ReadInlineMorphSource(CompiledElement? set, CompiledElement? mesh)
    {
        if (Graph.Resolve(Graph.Field(set, "m_MorphTextureData")) is not { } inline) return null;
        if (inline.TypeId != 0x3adc2df6 || Graph.Field(inline, "m_Format") is not { DataSize: 4 } format || Graph.Integer(format) != 0
            || Graph.Field(inline, "m_Data") is not { } pixels
            || Graph.Field(inline, "m_Width") is not { DataSize: 4 } width || Graph.Field(inline, "m_Height") is not { DataSize: 4 } height
            || Graph.Field(inline, "m_HashName") is not { DataSize: 8 } textureHash || Graph.Field(set, "m_MorphMapHash") is not { DataSize: 8 } setHash
            || Graph.Integer(setHash) == 0 || Graph.Integer(textureHash) != Graph.Integer(setHash) || Graph.Fields(inline).Count != 5)
            throw new NotSupportedException("Unsupported inline native morph texture schema or cache identity.");
        if (Graph.Field(mesh, "m_ExternalMorphMapTexture") is { } external && CompiledDependencyToken.ResolveName(Graph, external) is not null)
            throw new NotSupportedException("Simultaneous inline and external morph atlases require a verified native precedence contract.");
        int w = checked((int)Graph.Integer(width)), h = checked((int)Graph.Integer(height));
        if (w != 512 || h <= 0 || h > ushort.MaxValue || Graph.Data(pixels).Length != checked(w * h * 8))
            throw new NotSupportedException("Unsupported inline native morph atlas dimensions or payload length.");
        return new(inline, pixels, w, h);
    }

    // Contract inspection creates temporary bindings: do not retain them in the live preview cache.
    private CompiledMeshBinding.MorphChannels ReadInlineMorphChannels(CompiledElement visual, int lod)
    {
        var set = Graph.Resolve(Graph.Field(visual, "m_MeshSet")); var levels = Graph.Items(Graph.Field(set, "m_LODs"));
        if ((uint)lod >= levels.Count) throw new ArgumentOutOfRangeException(nameof(lod));
        var mesh = Graph.Resolve(Graph.Field(levels[lod], "m_Mesh"));
        var source = ReadInlineMorphSource(set, mesh) ?? throw new NotSupportedException("The visual has no inline native morph atlas.");
        return CompiledMeshBinding.ReadMorphChannels(Graph, visual, lod, new CompiledMorphAtlas(source.Width, source.Height, Graph.Data(source.Pixels)));
    }

    /// <summary>Reads expression target IDs and sparse data from the native external morph atlas.</summary>
    public CompiledMeshBinding.MorphChannels MorphChannels(CompiledMeshBinding binding, int lod)
    {
        if (!binding.OwnedBy(Graph)) throw new InvalidOperationException("Morph binding belongs to another document.");
        var set = Graph.Resolve(Graph.Field(binding.Visual, "m_MeshSet"));
        var levels = Graph.Items(Graph.Field(set, "m_LODs"));
        if (lod < 0 || lod >= levels.Count) throw new ArgumentOutOfRangeException(nameof(lod));
        var mesh = Graph.Resolve(Graph.Field(levels[lod], "m_Mesh"));
        if (Graph.Resolve(Graph.Field(mesh, "m_InternalMorphMapTextureData")) is not null)
            throw new NotSupportedException("Internal morph atlas preview is not decoded yet.");
        if (ReadInlineMorphSource(set, mesh) is { } inline)
        {
            var inlineKey = (binding, lod); string identity = Graph.Owner(inline.Texture).Path + "#" + inline.Texture.ObjectId.ToString("X16");
            if (_morphChannelsCache.TryGetValue(inlineKey, out var entry) && entry.Path == identity
                && entry.Resources.Length == Graph.Resources.Count && entry.Resources.Select((stamp, index) => ReferenceEquals(stamp.Resource, Graph.Resources[index])
                    && stamp.Revision == stamp.Resource.Revision).All(current => current)) return entry.Channels;
            var decoded = binding.ReadMorphChannels(lod, new CompiledMorphAtlas(inline.Width, inline.Height, Graph.Data(inline.Pixels)));
            _morphChannelsCache[inlineKey] = new(identity, inline.Pixels.DataSize, default,
                Graph.Resources.Select(resource => (resource, resource.Revision)).ToArray(), decoded);
            return decoded;
        }
        var token = Graph.Field(mesh, "m_ExternalMorphMapTexture")
            ?? throw new NotSupportedException("Mesh has no external morph atlas.");
        string name = CompiledDependencyToken.ResolveName(Graph, token)
            ?? throw new NotSupportedException("Mesh has no external morph atlas.");
        string path = TextureSearchIndex.FindPath(name)
            ?? throw new FileNotFoundException("Morph atlas dependency is not available in the copied resource mirror.", name);
        var file = new FileInfo(path);
        var key = (binding, lod);
        if (_morphChannelsCache.TryGetValue(key, out var cached) && cached.Path == path
            && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc
            && cached.Resources.Length == Graph.Resources.Count
            && cached.Resources.Select((stamp, index) => ReferenceEquals(stamp.Resource, Graph.Resources[index])
                && stamp.Revision == stamp.Resource.Revision).All(current => current))
            return cached.Channels;
        var channels = binding.ReadMorphChannels(lod, File.ReadAllBytes(path));
        _morphChannelsCache[key] = new(path, file.Length, file.LastWriteTimeUtc,
            Graph.Resources.Select(resource => (resource, resource.Revision)).ToArray(), channels);
        return channels;
    }

    /// <summary>Transient editor expression preview. Never changes native resources, weights, or history.</summary>
    public MeshData PreviewMorph(CompiledMeshBinding binding, int lod, IReadOnlyDictionary<int, float> weights)
        => PreviewMorphCore(binding, lod, weights, false);

    /// <summary>Native animation float weights may be signed or exceed one; no invented clamp is applied.</summary>
    public MeshData PreviewAnimatedMorph(CompiledMeshBinding binding, int lod, IReadOnlyDictionary<int, float> weights)
        => PreviewMorphCore(binding, lod, weights, true);

    private MeshData PreviewMorphCore(CompiledMeshBinding binding, int lod, IReadOnlyDictionary<int, float> weights, bool animated)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var channels = MorphChannels(binding, lod);
        var values = new float[channels.TargetIds.Length];
        foreach (var (index, weight) in weights)
        {
            if (index < 0 || index >= values.Length || !float.IsFinite(weight) || !animated && (weight < 0 || weight > 1))
                throw new ArgumentOutOfRangeException(nameof(weights), animated ? "Animation target indices must exist and weights must be finite." : "Preview target indices must exist and weights must be finite in [0,1].");
            values[index] = weight;
        }
        if (binding.MeshSource?.CanonicalFrame is not CompiledFrameNode frame)
            throw new InvalidOperationException("Morph binding has no canonical compiled frame.");
        if (animated && IsPreparedMorphFrame(frame) && weights.Values.Any(weight => weight is < 0 or > 1))
            throw new NotSupportedException("Prepared morph envelopes currently cover explicit target weights in [0,1]; signed or overshoot animation needs its own transient envelope contract.");
        var decoded = DecodeVisual(frame, binding.Visual, new CompiledMeshDecoder(Graph), lod,
            values.Any(value => value != 0) ? channels : null, values)
            ?? throw new InvalidDataException("Morph preview geometry could not be decoded.");
        if (decoded.Positions.Concat(decoded.Normals).Concat(decoded.Tangents ?? []).Concat(decoded.Binormals ?? [])
            .Any(vector => !float.IsFinite(vector.X) || !float.IsFinite(vector.Y) || !float.IsFinite(vector.Z)))
            throw new InvalidDataException("The sampled native morph weights produce non-finite geometry.");
        return decoded;
    }
}
