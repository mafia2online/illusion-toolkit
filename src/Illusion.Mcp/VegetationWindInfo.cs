namespace Illusion.Mcp;

public sealed record VegetationWindInfo(bool Enabled, float Time, float[] GlobalWind,
    float[] LocalPosition, float[] LocalParameters, bool BridgeSuspended, int DrawnParts,
    int EligibleMeshes, IReadOnlyDictionary<string, int> Refusals, string Scope)
{
    public int PendingMeshes { get; init; }
}
