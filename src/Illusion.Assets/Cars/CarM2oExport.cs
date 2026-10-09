using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Illusion.Assets.Sds;
using Illusion.Assets.Text;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.EntityData;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;
using Illusion.Formats.Prefab;
using Illusion.Formats.ResourceFormats;

namespace Illusion.Assets.Cars;

/// <summary>
/// A car as a resource a Mafia II Online server streams: a folder with the resource's <c>package.json</c> and the
/// car's archive (with its winter twin) under <c>stream/sds/cars/&lt;name&gt;.sds</c>. That is all a server owner
/// ships: the server registers every archive in that folder by its file name, a stock car's name replacing that
/// car, and checks each one before any player downloads it.
///
/// <para>
/// One file per car means everything the car needs is inside its archive. Textures already are. Materials are
/// not: the meshes name them by hash and the game resolves the hash against <c>edit\materials\default.mtl</c>,
/// which no server can change on a player's machine. So a material the car uses that the game did not ship with
/// — one the toolkit created for it — is embedded: written as an MTL library of just those materials, carried in
/// the archive as a <c>MemFile</c> named <c>&lt;name&gt;.mtl</c>, and added by the multiplayer through the game's
/// own <c>LoadMTL</c> when the car arrives. A server refuses a car that adds textures no material uses, so an
/// archive exported without its materials does not ship.
/// </para>
/// <para>
/// A car is exported only when its archive is its own: the root frame, the first name-table entry, the prefab
/// entry and the entity data all filed under the model name — the server checks the same. A folder can hold
/// several cars; a second export into it adds one.
/// </para>
/// </summary>
public static partial class CarM2oExport
{
    /// <summary>The resource's manifest, as the multiplayer reads it.</summary>
    public const string PackageFile = "package.json";

    /// <summary>Where a resource's car archives go: the stream lane, at the path the game loads a car from.</summary>
    public const string CarsFolder = "stream/sds/cars";

    /// <summary>The MemFile resource version the game's car archives carry.</summary>
    private const ushort MemFileVersion = 2;

    // The layout before cars were streamed; an export folder still holding it is not written into.
    private const string LegacyVehiclesFile = "vehicles.json";
    private const string LegacyCarsFolder = "sds";

    private const int VehicleNameColumn = 2;
    private const int VehicleTextColumn = 3;

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex ResourcePattern();

    /// <summary>The resource name a car gets when none is given: <c>car-shubert-38-custom</c>.</summary>
    public static string DefaultResource(string car) =>
        "car-" + Path.GetFileNameWithoutExtension(car).ToLowerInvariant().Replace('_', '-');

    /// <summary>
    /// Exports a car of the game the toolkit has open. <paramref name="car"/> is its archive or model name;
    /// the archive exported is the one in <c>pc\sds\cars</c> as it stands, so build the car first. Without
    /// <paramref name="output"/> the folder is <c>&lt;game&gt;\_illusion_export\m2o\&lt;resource&gt;</c>.
    /// Nothing of the game is written.
    /// </summary>
    public static CarM2oExportResult? Export(string car, string? output, string? resource, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(car);
        if (!MafiaEnvironment.IsInitialized)
        {
            refusal = "the game folder is not set";
            return null;
        }
        string stem = Path.GetFileNameWithoutExtension(car).ToLowerInvariant();
        string sds = Path.Combine(MafiaEnvironment.PcFolder, "sds");
        var archive = new FileInfo(Path.Combine(sds, "cars", stem + ".sds"));
        if (!archive.Exists)
        {
            refusal = $"there is no car archive {archive.Name} in pc\\sds\\cars";
            return null;
        }
        var winter = new FileInfo(Path.Combine(sds, "cars", stem + "_z.sds"));
        var tables = new FileInfo(Path.Combine(sds, "tables", "tables.sds"));
        var text = new List<(string, string)>();
        foreach (string language in Directory.GetDirectories(MafiaEnvironment.PcFolder, "sds_*"))
        {
            var textSds = new FileInfo(Path.Combine(language, "text", "text_default.sds"));
            if (!textSds.Exists) continue;
            text.Add((Path.GetFileName(language)["sds_".Length..],
                Path.Combine(SdsMeshLoader.EnsureExtracted(textSds), "tables", "TextDatabase.dat")));
        }

        MafiaMaterials.EnsureLoaded();
        MaterialCollection? materials = MafiaMaterials.Collection;
        if (materials == null || materials.Libraries.Count == 0)
        {
            refusal = "no MTL libraries are loaded (is the game folder configured?)";
            return null;
        }
        MaterialVersion version = materials.Libraries.Values.First().Version;

        string? basedOn = CarCloner.SourceOf(MafiaEnvironment.ExtractedDir(archive));
        var basedOnArchive = basedOn == null ? null : new FileInfo(Path.Combine(sds, "cars", basedOn.ToLowerInvariant() + ".sds"));
        resource ??= DefaultResource(stem);
        output ??= Path.Combine(MafiaEnvironment.GameRoot, "_illusion_export", "m2o", resource);
        var sources = new CarM2oExportSources(
            archive,
            winter.Exists ? winter : null,
            tables.Exists ? Path.Combine(SdsMeshLoader.EnsureExtracted(tables), "tables", "vehicles.tbl") : null,
            text,
            basedOn,
            basedOnArchive is { Exists: true } ? basedOnArchive : null,
            materials.FindByHash,
            StockMaterials(materials),
            version);
        return ExportFrom(sources, output, resource, out refusal);
    }

    /// <summary>
    /// The materials the game shipped with: every library in <c>edit\materials</c> as it was before the toolkit
    /// first wrote it. The toolkit keeps each previous version of a library in a <c>backups</c> folder beside
    /// it (see <c>GameMaterialCreator</c>), stamped so that names sort by time, so the oldest backup is the
    /// library as it came; a library with no backup was never written and is as it came.
    /// </summary>
    public static IReadOnlySet<ulong> StockMaterials(MaterialCollection materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var stock = new HashSet<ulong>();
        foreach ((string path, MaterialLibrary library) in materials.Libraries)
        {
            string folder = Path.Combine(Path.GetDirectoryName(path) ?? ".", "backups");
            string stem = Path.GetFileNameWithoutExtension(path);
            string? oldest = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, stem + "_*.mtl").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
            if (oldest == null)
            {
                stock.UnionWith(library.Materials.Keys);
                continue;
            }
            var pristine = new MaterialLibrary(library.Version);
            pristine.ReadMatFile(oldest);
            stock.UnionWith(pristine.Materials.Keys);
        }
        return stock;
    }

    /// <summary>
    /// The export itself, from named sources into <paramref name="output"/>. Refuses — writing nothing — a
    /// resource name the multiplayer would not take, a folder that holds something other than a car export, an
    /// archive that is not filed under its own name throughout, and a car using a material no library has.
    /// </summary>
    public static CarM2oExportResult? ExportFrom(CarM2oExportSources sources, string output, string resource, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(resource);
        if (!ResourcePattern().IsMatch(resource))
        {
            refusal = $"'{resource}' — a resource name is lower-case letters, digits, '-', '_' and '.', starting with a letter or digit";
            return null;
        }
        if (!sources.Archive.Exists)
        {
            refusal = $"no such archive: {sources.Archive.FullName}";
            return null;
        }
        // The manifest is read BEFORE anything is copied: one that is there and does not read is a refusal and
        // the folder is left as it was, rather than taken for "no manifest yet" and written over.
        string packageFile = Path.Combine(output, PackageFile);
        if (Unreadable(packageFile, node => node is JsonObject, "a package manifest") is { } badPackage)
        {
            refusal = badPackage;
            return null;
        }
        if (File.Exists(Path.Combine(output, LegacyVehiclesFile)) || Directory.Exists(Path.Combine(output, LegacyCarsFolder)))
        {
            refusal = $"{output} holds an export in the old layout (sds/cars and vehicles.json), which the multiplayer no longer reads — export into an empty folder";
            return null;
        }
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any() && !File.Exists(packageFile))
        {
            refusal = $"{output} holds something that is not a car export — pick an empty folder";
            return null;
        }

        string stem = Path.GetFileNameWithoutExtension(sources.Archive.Name).ToLowerInvariant();
        var notes = new List<string>();

        // The vehicle table names the model as the archive's insides must; an unregistered car is named by its frame.
        GameTable? vehicles = sources.VehiclesTable != null && File.Exists(sources.VehiclesTable)
            ? GameTable.Load(sources.VehiclesTable)
            : null;
        int row = vehicles?.FindRow(VehicleNameColumn, stem) ?? -1;
        string? listed = row >= 0 ? (string)vehicles!.Cell(row, VehicleNameColumn) : null;

        Inspection car = Inspect(sources.Archive, listed, stem);
        if (car.Model == null || car.Problems.Count > 0)
        {
            refusal = $"{sources.Archive.Name} is not a car of its own: {string.Join("; ", car.Problems)}";
            return null;
        }
        string model = car.Model;
        Inspection? winter = null;
        if (sources.WinterArchive is { } winterArchive)
        {
            winter = Inspect(winterArchive, model, stem);
            if (winter.Problems.Count > 0)
            {
                refusal = $"{winterArchive.Name} is not the same car: {string.Join("; ", winter.Problems)}";
                return null;
            }
            if (!winter.ShippedMemory) notes.Add(PackerFigures(winterArchive.Name));
        }
        if (!car.ShippedMemory) notes.Add(PackerFigures(sources.Archive.Name));
        if (sources.BasedOnArchive is { Exists: true } baseArchive)
        {
            int shared = Inspect(baseArchive, sources.BasedOn, Path.GetFileNameWithoutExtension(baseArchive.Name)).Buffers
                .Count(car.Buffers.Contains);
            if (shared > 0)
            {
                notes.Add($"{shared} geometry buffers bear the names {sources.BasedOn}'s do — with both cars loaded, one draws the other's shape");
            }
        }

        // The materials the car adds, read as the libraries hold them now; one no library has cannot travel.
        var added = new SortedDictionary<ulong, IMaterial>();
        if (sources.FindMaterial != null && sources.StockMaterials != null)
        {
            var missing = new List<string>();
            foreach (ulong hash in car.Materials.Concat(winter?.Materials ?? []).Distinct())
            {
                if (sources.StockMaterials.Contains(hash)) continue;
                if (sources.FindMaterial(hash) is { } material) added[hash] = material;
                else missing.Add("0x" + hash.ToString("x16"));
            }
            if (missing.Count > 0)
            {
                refusal = $"{sources.Archive.Name} uses materials no library has ({string.Join(", ", missing)}) — create them before exporting";
                return null;
            }
        }
        else
        {
            notes.Add("no material libraries were given — any material the car adds is not embedded, and a server refuses a car whose new textures no material uses");
        }

        string? title = null;
        if (row >= 0 && vehicles!.Cell(row, VehicleTextColumn) is int textId)
        {
            foreach ((string language, string table) in sources.Text)
            {
                if (!File.Exists(table) || GameText.Find(table, textId) is not { } found) continue;
                if (title == null || language.Equals("en", StringComparison.OrdinalIgnoreCase)) title = found;
            }
        }

        // Everything is known; now the folder.
        string cars = Path.Combine(output, CarsFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(cars);
        var written = new List<string>();
        foreach ((FileInfo? source, Inspection? inspected) in new[] { (sources.Archive, car), (sources.WinterArchive, winter) })
        {
            if (source == null || inspected == null) continue;
            string target = Path.Combine(cars, source.Name.ToLowerInvariant());
            var needed = inspected.Materials.Where(added.ContainsKey).Select(h => added[h]).ToList();
            if (needed.Count == 0)
            {
                File.Copy(source.FullName, target, overwrite: true);
            }
            else
            {
                EmbedMaterials(source, target, stem + ".mtl", needed, sources.MaterialVersion);
            }
            written.Add(target);
        }

        WritePackage(packageFile, resource, model, title);
        written.Add(packageFile);

        int carsInFolder = Directory.EnumerateFiles(cars, "*.sds")
            .Count(path => !Path.GetFileNameWithoutExtension(path).EndsWith("_z", StringComparison.OrdinalIgnoreCase));
        refusal = null;
        return new CarM2oExportResult(output, resource, model, title, sources.BasedOn, carsInFolder,
            [.. added.Values.Select(m => m.MaterialName.String)], written, notes);
    }

    // The archive with the materials written into it as a MemFile named `name`, which the multiplayer reads back
    // and hands to the game's LoadMTL. The rest of the archive is repacked as it was, with the memory each
    // resource asks for kept from the original.
    private static void EmbedMaterials(FileInfo source, string target, string name, IReadOnlyList<IMaterial> materials, MaterialVersion version)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_m2o_" + Guid.NewGuid().ToString("N"));
        try
        {
            SdsMemoryRequirements memory = SdsMemoryRequirements.Extract(SdsArchive.Open(source.FullName), scratch);
            var library = new MaterialLibrary(version)
            {
                Materials = materials.ToDictionary(m => m.GetMaterialHash()),
            };
            library.WriteMatFile(Path.Combine(scratch, name));
            SdsManifest.Load(scratch).AddEntry("MemFile", name, MemFileVersion, [("Unk2_V4", "0")]);

            SdsArchive packed = SdsArchive.Pack(scratch, GameProfile.MafiaII, memory);
            string temp = target + ".tmp";
            using (FileStream stream = File.Create(temp))
            {
                packed.Save(stream, new SdsWriteOptions());
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* scratch left behind */ }
        }
    }

    private static string PackerFigures(string archive) =>
        $"{archive} states a packer's memory figures, less than a shipped car asks for — build it with the car it was made from as the memory reference before shipping";

    // Why an existing manifest cannot be built on, or null when it is absent or reads as what it should be.
    private static string? Unreadable(string path, Func<JsonNode?, bool> isWhatItShouldBe, string what)
    {
        if (!File.Exists(path)) return null;
        string name = Path.GetFileName(path);
        try
        {
            return isWhatItShouldBe(JsonNode.Parse(File.ReadAllText(path)))
                ? null
                : $"{name} in the output folder is not {what} — it is left as it is; fix or remove it and export again";
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return $"{name} in the output folder cannot be read ({ex.Message}) — it is left as it is; fix or remove it and export again";
        }
    }

    // A package.json somebody already wrote keeps what it says. A new one needs nothing beyond its name: the
    // stream folder ships on its own, no file list is needed.
    private static void WritePackage(string packageFile, string resource, string model, string? title)
    {
        if (File.Exists(packageFile)) return;
        var package = new JsonObject
        {
            ["name"] = resource,
            ["version"] = "1.0.0",
            ["description"] = $"Vehicle model {model}{(title != null ? $" ({title})" : "")}",
        };
        WriteJson(packageFile, package);
    }

    private static void WriteJson(string path, JsonNode node) =>
        AtomicFile.WriteAllBytes(path, new System.Text.UTF8Encoding(false).GetBytes(node.ToJsonString(Indented) + "\n"));

    private sealed record Inspection(string? Model, List<string> Problems, HashSet<ulong> Buffers, HashSet<ulong> Materials, bool ShippedMemory);

    // What the archive itself says: whether every key in it is the model's, the names its buffers bear, the
    // materials its meshes use, and whether it states memory figures like a shipped archive.
    private static Inspection Inspect(FileInfo archive, string? model, string stem)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_m2o_" + Guid.NewGuid().ToString("N"));
        try
        {
            SdsArchive opened = SdsArchive.Open(archive.FullName);
            bool shipped = SdsMemoryRequirements.Extract(opened, scratch).LooksShipped;
            ExtractedSds loaded = ExtractedSds.Load(scratch);
            var problems = new List<string>();
            List<string> frames = loaded.FrameResource?.FrameObjects.Values.OfType<FrameObjectBase>()
                .Select(f => f.Name.String ?? "").ToList() ?? [];
            model ??= frames.FirstOrDefault(n => string.Equals(n, stem, StringComparison.OrdinalIgnoreCase));
            var buffers = new HashSet<ulong>(loaded.VertexBuffers.Buffers.Keys.Concat(loaded.IndexBuffers.Buffers.Keys));
            var materials = new HashSet<ulong>(loaded.FrameResource?.FrameMaterials.Values
                .SelectMany(block => block.Materials.SelectMany(lod => lod))
                .Select(slot => slot.MaterialHash).Where(hash => hash != 0) ?? []); // 0: a slot with no material
            if (model == null)
            {
                problems.Add($"no frame in it is named {stem}");
                return new Inspection(null, problems, buffers, materials, shipped);
            }

            if (!frames.Contains(model)) problems.Add($"no frame in it is named {model}");
            FrameNameTable.Data[] listed = loaded.FrameNameTable?.FrameData ?? [];
            if (listed.Length == 0 || listed[0].Name != model)
            {
                problems.Add($"its name table starts with {(listed.Length > 0 ? listed[0].Name : "nothing")}, not {model}");
            }
            foreach (string path in loaded.Manifest.GetFiles("PREFAB"))
            {
                if (!PrefabFile.Load(path).Contains(Fnv64.Hash(model))) problems.Add($"its prefab has no entry for {model}");
            }
            foreach (string path in loaded.Manifest.GetFiles("EntityDataStorage"))
            {
                if (EntityDataStorageFile.Load(path).Hash != Fnv64.Hash(model.ToLowerInvariant()))
                {
                    problems.Add($"its entity data is not filed under {model.ToLowerInvariant()}");
                }
            }
            return new Inspection(model, problems, buffers, materials, shipped);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* scratch left behind */ }
        }
    }
}
