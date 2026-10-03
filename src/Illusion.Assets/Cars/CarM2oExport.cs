using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Illusion.Assets.Sds;
using Illusion.Assets.Text;
using Illusion.Formats.Archive;
using Illusion.Formats.EntityData;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;
using Illusion.Formats.ResourceFormats;

namespace Illusion.Assets.Cars;

/// <summary>
/// A car as a resource a Mafia II Online server can ship: a folder with the resource's <c>package.json</c>, the
/// car's archive under <c>cars/</c> (and its winter twin) and <c>vehicles.json</c>, which says what the
/// archives hold.
///
/// <para>
/// Only what can travel goes in. The single-player registration — the row in <c>vehicles.tbl</c>, the paint
/// combinations, the title in the text table — sits in archives every player already has and the multiplayer
/// replaces with its own, so it is written into <c>vehicles.json</c> as data instead: the model name the
/// archive is keyed by, the title, the car it was made from, and the table rows as the game holds them.
/// </para>
/// <para>
/// A car is exported only when its archive is its own: the root frame, the first name-table entry, the prefab
/// entry and the entity data all filed under the model name. A folder can hold several cars — a second export
/// into it adds to the list.
/// </para>
/// </summary>
public static partial class CarM2oExport
{
    /// <summary>The list of cars a resource folder holds.</summary>
    public const string VehiclesFile = "vehicles.json";

    /// <summary>The resource's manifest, as the multiplayer reads it.</summary>
    public const string PackageFile = "package.json";

    /// <summary>The version of <see cref="VehiclesFile"/>'s layout.</summary>
    public const int Format = 1;

    private const string CarsFolder = "cars";
    private const int VehicleIdColumn = 0;
    private const int VehicleNameColumn = 2;
    private const int VehicleTextColumn = 3;
    private const int PaintNameColumn = 1;

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
        var ingame = new FileInfo(Path.Combine(sds, "tables", "ingame.sds"));
        var text = new List<(string, string)>();
        foreach (string language in Directory.GetDirectories(MafiaEnvironment.PcFolder, "sds_*"))
        {
            var textSds = new FileInfo(Path.Combine(language, "text", "text_default.sds"));
            if (!textSds.Exists) continue;
            text.Add((Path.GetFileName(language)["sds_".Length..],
                Path.Combine(SdsMeshLoader.EnsureExtracted(textSds), "tables", "TextDatabase.dat")));
        }

        string? basedOn = CarCloner.SourceOf(MafiaEnvironment.ExtractedDir(archive));
        var basedOnArchive = basedOn == null ? null : new FileInfo(Path.Combine(sds, "cars", basedOn.ToLowerInvariant() + ".sds"));
        resource ??= DefaultResource(stem);
        output ??= Path.Combine(MafiaEnvironment.GameRoot, "_illusion_export", "m2o", resource);
        var sources = new CarM2oExportSources(
            archive,
            winter.Exists ? winter : null,
            tables.Exists ? Path.Combine(SdsMeshLoader.EnsureExtracted(tables), "tables", "vehicles.tbl") : null,
            ingame.Exists ? Path.Combine(SdsMeshLoader.EnsureExtracted(ingame), "tables", "PaintCombinations.tbl") : null,
            text,
            basedOn,
            basedOnArchive is { Exists: true } ? basedOnArchive : null);
        return ExportFrom(sources, output, resource, out refusal);
    }

    /// <summary>
    /// The export itself, from named sources into <paramref name="output"/>. Refuses — writing nothing — a
    /// resource name the multiplayer would not take, a folder that holds something other than an export, and an
    /// archive that is not filed under its own name throughout.
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
        string vehiclesFile = Path.Combine(output, VehiclesFile);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any() && !File.Exists(vehiclesFile))
        {
            refusal = $"{output} holds something that is not a car export — pick an empty folder";
            return null;
        }

        string stem = Path.GetFileNameWithoutExtension(sources.Archive.Name);
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
        if (sources.WinterArchive is { } winterArchive)
        {
            Inspection winter = Inspect(winterArchive, model, stem);
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

        var entry = new JsonObject
        {
            ["model"] = model,
            ["archive"] = $"{CarsFolder}/{sources.Archive.Name}",
            ["winterArchive"] = sources.WinterArchive != null ? JsonValue.Create($"{CarsFolder}/{sources.WinterArchive.Name}") : null,
        };

        // The title, in every language that has one.
        string? title = null;
        if (row >= 0 && vehicles!.Cell(row, VehicleTextColumn) is int textId)
        {
            var titles = new JsonObject();
            foreach ((string language, string table) in sources.Text)
            {
                if (!File.Exists(table) || GameText.Find(table, textId) is not { } found) continue;
                titles[language] = found;
                if (title == null || language.Equals("en", StringComparison.OrdinalIgnoreCase)) title = found;
            }
            entry["title"] = title;
            entry["titles"] = titles;
        }
        entry["basedOn"] = sources.BasedOn;
        entry["hashes"] = new JsonObject
        {
            ["model"] = Hex(Fnv64.Hash(model)),
            ["entityData"] = Hex(Fnv64.Hash(model.ToLowerInvariant())),
        };

        if (row >= 0)
        {
            entry["vehicleTable"] = RowJson(vehicles!, row, (int)vehicles!.Cell(row, VehicleIdColumn));
        }
        else
        {
            notes.Add($"vehicles.tbl lists no car named {stem} — the export carries no table row");
        }
        if (sources.PaintTable != null && File.Exists(sources.PaintTable))
        {
            GameTable paint = GameTable.Load(sources.PaintTable);
            int paintRow = paint.FindRow(PaintNameColumn, model);
            if (paintRow >= 0) entry["paintCombinations"] = RowJson(paint, paintRow, id: null);
        }

        // Everything is known; now the folder.
        string cars = Path.Combine(output, CarsFolder);
        Directory.CreateDirectory(cars);
        var written = new List<string>();
        var files = new JsonArray();
        foreach (FileInfo? source in new[] { sources.Archive, sources.WinterArchive })
        {
            if (source == null) continue;
            string target = Path.Combine(cars, source.Name);
            File.Copy(source.FullName, target, overwrite: true);
            written.Add(target);
            files.Add(new JsonObject
            {
                ["path"] = $"{CarsFolder}/{source.Name}",
                ["size"] = new FileInfo(target).Length,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(target))),
            });
        }
        entry["files"] = files;

        JsonArray list = ReadList(vehiclesFile);
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i]?["model"]?.GetValue<string>(), model, StringComparison.OrdinalIgnoreCase)) list.RemoveAt(i);
        }
        list.Add(entry);
        var document = new JsonObject
        {
            ["format"] = Format,
            ["about"] = "Car models exported by Illusion Toolkit. Each archive is filed under its model name throughout "
                + "(root frame, name table, prefab entry, entity data, geometry buffers); vehicleTable and "
                + "paintCombinations are the rows the single-player game registers the model with.",
            ["vehicles"] = list,
        };
        WriteJson(vehiclesFile, document);
        written.Add(vehiclesFile);

        string packageFile = Path.Combine(output, PackageFile);
        WritePackage(packageFile, resource, model, title);
        written.Add(packageFile);

        refusal = null;
        return new CarM2oExportResult(output, resource, model, title, sources.BasedOn, list.Count, written, notes);
    }

    private static string PackerFigures(string archive) =>
        $"{archive} states a packer's memory figures, less than a shipped car asks for — build it with the car it was made from as the memory reference before shipping";

    private static string Hex(ulong hash) => "0x" + hash.ToString("x16");

    private static JsonObject RowJson(GameTable table, int row, int? id)
    {
        var columns = new JsonArray();
        var values = new JsonArray();
        for (int c = 0; c < table.ColumnCount; c++)
        {
            columns.Add(table.ColumnType(c));
            values.Add(table.Cell(row, c) switch
            {
                int i => JsonValue.Create(i),
                uint u => JsonValue.Create(u),
                float f => JsonValue.Create(f),
                bool b => JsonValue.Create(b),
                ulong h => JsonValue.Create(Hex(h)),
                object other => JsonValue.Create(other.ToString()),
            });
        }
        var json = new JsonObject();
        if (id is { } given) json["id"] = given;
        json["columns"] = columns;
        json["values"] = values;
        return json;
    }

    // The cars a folder already lists; a file that does not read as one starts the list over.
    private static JsonArray ReadList(string vehiclesFile)
    {
        if (!File.Exists(vehiclesFile)) return [];
        try
        {
            if (JsonNode.Parse(File.ReadAllText(vehiclesFile))?["vehicles"] is JsonArray existing)
            {
                return [.. existing.Select(v => v?.DeepClone())];
            }
        }
        catch (JsonException)
        {
            // not a list — replaced
        }
        return [];
    }

    // A package.json somebody already wrote keeps what it says; it only has to ship the cars.
    private static void WritePackage(string packageFile, string resource, string model, string? title)
    {
        JsonObject? package = null;
        if (File.Exists(packageFile))
        {
            try
            {
                package = JsonNode.Parse(File.ReadAllText(packageFile)) as JsonObject;
            }
            catch (JsonException)
            {
                // not a manifest — replaced
            }
        }
        package ??= new JsonObject
        {
            ["name"] = resource,
            ["version"] = "1.0.0",
            ["description"] = $"Vehicle model {model}{(title != null ? $" ({title})" : "")}",
        };
        if (package["mafiahub"] is not JsonObject hub) package["mafiahub"] = hub = new JsonObject { ["priority"] = 0 };
        if (hub["files"] is not JsonArray shipped) hub["files"] = shipped = [];
        foreach (string pattern in new[] { $"{CarsFolder}/**", VehiclesFile })
        {
            if (!shipped.Any(f => f?.GetValueKind() == JsonValueKind.String && f.GetValue<string>() == pattern)) shipped.Add(pattern);
        }
        WriteJson(packageFile, package);
    }

    private static void WriteJson(string path, JsonNode node) =>
        AtomicFile.WriteAllBytes(path, new System.Text.UTF8Encoding(false).GetBytes(node.ToJsonString(Indented) + "\n"));

    private sealed record Inspection(string? Model, List<string> Problems, HashSet<ulong> Buffers, bool ShippedMemory);

    // What the archive itself says: whether every key in it is the model's, the names its buffers bear, and
    // whether it states memory figures like a shipped archive.
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
            if (model == null)
            {
                problems.Add($"no frame in it is named {stem}");
                return new Inspection(null, problems, buffers, shipped);
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
            return new Inspection(model, problems, buffers, shipped);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* scratch left behind */ }
        }
    }
}
