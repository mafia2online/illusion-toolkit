namespace Illusion.Mcp;

/// <summary>Read-only native linked-proxy preparation costs.</summary>
public sealed record LinkedProxyInfo(string ParentGroup, string DestinationArchive,
    int FineFrames, int BeforeVisuals, int AfterVisuals, int BeforeTriangles, int AfterTriangles,
    int BeforeDraws, int AfterDraws, int RequiredTextures, long SelectedNativeBufferBytes,
    long ConservativeSourceGenericBytes, string CostNotice);
