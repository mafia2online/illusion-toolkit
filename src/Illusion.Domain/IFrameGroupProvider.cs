namespace Illusion.Domain;

/// <summary>Creates a persistent identity group with the same undo contract as object duplication.</summary>
public interface IFrameGroupProvider
{
    IFrameDuplication? CreateGroup(string name, IFrameNode? parent, out string? refusal);
}
