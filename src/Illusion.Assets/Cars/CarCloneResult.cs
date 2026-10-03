namespace Illusion.Assets.Cars;

/// <summary>What a clone wrote into the working copies: the new vehicle id, the traffic rows that now pick it,
/// and what was left out.</summary>
public sealed record CarCloneResult(int VehicleId, int TrafficRows, IReadOnlyList<string> Notes);
