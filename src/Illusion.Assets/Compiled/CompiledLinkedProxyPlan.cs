using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>A read-only cost preview. Creating this object does not prepare or unlock native objects.</summary>
public sealed class CompiledLinkedProxyPlan
{
    public ulong ParentGroupId { get; }
    public string DestinationArchive { get; }
    public int FineFrames { get; }
    public int BeforeVisuals { get; }
    public int AfterVisuals { get; }
    public int BeforeTriangles { get; }
    public int AfterTriangles { get; }
    public int BeforeDraws { get; }
    public int AfterDraws { get; }
    public int RequiredTextures { get; }
    public long SelectedNativeBufferBytes { get; }
    /// <summary>Current whole donor GENR payload sizes; a conservative reservation, not measured live RAM.</summary>
    public long ConservativeSourceGenericBytes { get; }
    public string CostNotice => "Preparation replaces an atlas proxy with separate native lowest-LOD visuals. "
        + "Triangle and draw costs can increase substantially. Byte and RAM projections are conservative, not measured runtime allocations.";
    internal string[] TextureNames { get; }
    internal CompiledSceneDocument Document { get; }
    internal CompiledElement ParentGroup { get; }
    internal CompiledElement ChildGroup { get; }
    internal CompiledSceneDocument.CompiledFrameNode Coarse { get; }
    internal CompiledSceneDocument.CompiledFrameNode[] Fine { get; }
    internal CompiledResource Destination { get; }
    internal IReadOnlyDictionary<CompiledResource, byte[]> ResourceHashes { get; }
    internal IReadOnlyDictionary<CompiledResource, long> ResourceRevisions { get; }
    internal IReadOnlyDictionary<string, byte[]> FileHashes { get; }

    internal CompiledLinkedProxyPlan(CompiledSceneDocument document, CompiledElement parent, CompiledElement child,
        CompiledSceneDocument.CompiledFrameNode coarse, CompiledSceneDocument.CompiledFrameNode[] fine,
        CompiledResource destination, string archive, int beforeVisuals, int afterVisuals, int beforeTriangles,
        int afterTriangles, int beforeDraws, int afterDraws, string[] textures, long buffers, long genericBytes,
        IReadOnlyDictionary<CompiledResource, byte[]> resourceHashes, IReadOnlyDictionary<string, byte[]> fileHashes)
    {
        Document = document; ParentGroup = parent; ChildGroup = child; Coarse = coarse; Fine = fine;
        Destination = destination; DestinationArchive = archive; ParentGroupId = parent.ObjectId; FineFrames = fine.Length;
        BeforeVisuals = beforeVisuals; AfterVisuals = afterVisuals; BeforeTriangles = beforeTriangles; AfterTriangles = afterTriangles;
        BeforeDraws = beforeDraws; AfterDraws = afterDraws; RequiredTextures = textures.Length; TextureNames = textures; SelectedNativeBufferBytes = buffers;
        ConservativeSourceGenericBytes = genericBytes; ResourceHashes = resourceHashes; FileHashes = fileHashes;
        ResourceRevisions = resourceHashes.Keys.ToDictionary(r => r, r => r.Revision);
    }
}
