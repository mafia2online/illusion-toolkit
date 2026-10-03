namespace Illusion.Assets.Cars;

/// <summary>What a clone wrote into the working copies: the new vehicle id, the traffic rows that now pick it,
/// the id of the text its name is filed under (null when it shares the source car's name), and what was left
/// out.</summary>
public sealed record CarCloneResult(int VehicleId, int TrafficRows, int? TextId, IReadOnlyList<string> Notes);
