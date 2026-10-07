namespace Illusion.Assets.Cars;

/// <summary>The working copies a car clone writes: tables.sds and ingame.sds, each car archive (the car and
/// its winter twin) with the folder its copy goes to, and the text archive of every installed language
/// (text_default.sds) for the clone's own name.</summary>
public sealed record CarCloneFolders(
    string Tables, string Ingame, IReadOnlyList<(string From, string To)> Cars, IReadOnlyList<string> Text);
