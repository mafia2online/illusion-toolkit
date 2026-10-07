namespace Illusion.Assets.Cars;

/// <summary>
/// What a multiplayer export of one car is made from: its built archive (and the winter twin, when it has
/// one), the vehicle and paint tables it is registered in, the text table of each language, and the car it was
/// cloned from with that car's archive.
/// </summary>
public sealed record CarM2oExportSources(
    FileInfo Archive,
    FileInfo? WinterArchive,
    string? VehiclesTable,
    string? PaintTable,
    IReadOnlyList<(string Language, string Table)> Text,
    string? BasedOn,
    FileInfo? BasedOnArchive);
