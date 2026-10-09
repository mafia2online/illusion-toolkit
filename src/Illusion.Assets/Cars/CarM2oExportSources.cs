using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;

namespace Illusion.Assets.Cars;

/// <summary>
/// What a multiplayer export of one car is made from: its built archive (and the winter twin, when it has
/// one), the vehicle table and the text table of each language its title is read from, the car it was cloned
/// from with that car's archive, and the materials.
/// </summary>
/// <param name="FindMaterial">A material by hash, as the libraries hold it now — where a material the car
/// adds is read from. Null exports no materials.</param>
/// <param name="StockMaterials">The materials every player already has: the hashes of the libraries as the
/// game shipped them. A material the car uses and this set lacks goes into the car's library.</param>
/// <param name="MaterialVersion">The library format the car's material library is written in.</param>
public sealed record CarM2oExportSources(
    FileInfo Archive,
    FileInfo? WinterArchive,
    string? VehiclesTable,
    IReadOnlyList<(string Language, string Table)> Text,
    string? BasedOn,
    FileInfo? BasedOnArchive,
    Func<ulong, IMaterial?>? FindMaterial = null,
    IReadOnlySet<ulong>? StockMaterials = null,
    MaterialVersion MaterialVersion = MaterialVersion.V_57);
