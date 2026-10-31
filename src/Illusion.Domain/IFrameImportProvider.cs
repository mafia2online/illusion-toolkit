namespace Illusion.Domain;

/// <summary>Import of a stock compiled prototype into an existing scene hierarchy.</summary>
public interface IFrameImportProvider
{
    IFrameDuplication? ImportFrame(string sourceResourcePath, ulong sourceObjectId, IFrameNode parent, out string? refusal);
}
