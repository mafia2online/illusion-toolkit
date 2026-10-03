namespace Illusion.Assets.Cars;

/// <summary>The working copies a car clone writes: tables.sds and ingame.sds, and each car archive (the
/// car and its winter twin) with the folder its copy goes to.</summary>
public sealed record CarCloneFolders(string Tables, string Ingame, IReadOnlyList<(string From, string To)> Cars);
