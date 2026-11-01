namespace Illusion.Mcp;

public sealed record NativePlacementInfo(string Archive, IReadOnlyList<string> Resources, int Placements,
    int RenderedPlacements, IReadOnlyDictionary<string, int> Refusals)
{
    public int RestPosePlacements { get; init; }
    public int LightOverridePlacements { get; init; }
    public int UnsupportedLightChanges { get; init; }
}
