namespace Illusion.Mcp;

public sealed record NativePlacementInfo(string Archive, IReadOnlyList<string> Resources, int Placements,
    int RenderedPlacements, IReadOnlyDictionary<string, int> Refusals);
