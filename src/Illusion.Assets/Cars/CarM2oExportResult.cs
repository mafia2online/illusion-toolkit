namespace Illusion.Assets.Cars;

/// <summary>
/// A car exported as a multiplayer resource: the folder, the resource's name, the model and its title, the car
/// it was cloned from, how many cars the folder now holds, the materials embedded in the car's archive, the
/// files written and what the one shipping it should know.
/// </summary>
public sealed record CarM2oExportResult(
    string Folder,
    string Resource,
    string Model,
    string? Title,
    string? BasedOn,
    int Vehicles,
    IReadOnlyList<string> EmbeddedMaterials,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Notes);
