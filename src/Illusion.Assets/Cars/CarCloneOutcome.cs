namespace Illusion.Assets.Cars;

/// <summary>
/// What cloning a car wrote: the new model name and the vehicle id the tables give it, the archives built (the
/// car, its winter twin, the two table archives) with the backup taken of each one that already existed, how many
/// traffic rows now pick the clone, and what was left out and why.
/// </summary>
public sealed record CarCloneOutcome(
    string Name,
    int VehicleId,
    IReadOnlyList<(string Archive, string? Backup)> Packed,
    int TrafficRows,
    IReadOnlyList<string> Notes);
