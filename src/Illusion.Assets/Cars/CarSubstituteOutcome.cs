namespace Illusion.Assets.Cars;

/// <summary>
/// What building one car under another's name wrote: the model name the archives are now keyed by, each archive
/// replaced with the backup taken of it, and what was left as it was and why.
/// </summary>
public sealed record CarSubstituteOutcome(
    string Model,
    IReadOnlyList<(string Archive, string? Backup)> Packed,
    IReadOnlyList<string> Notes);
