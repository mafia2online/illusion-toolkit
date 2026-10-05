using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Assets.Sds;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Hashing;
using Illusion.Formats.ResourceFormats;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// A car exported as a multiplayer resource, from a clone made and packed on scratch copies (nothing of the
/// game is written): the folder's manifest and car list, the archives it ships, a second car joining the list,
/// and what an export refuses.
/// </summary>
internal static class CarM2oExportProbes
{
    private const string Source = "shubert_38";
    private const string Name = "Shubert_38_Export";
    private const string Title = "Shubert 38 Export";

    // Output: %TEMP%\illusion_car_m2o.txt
    internal static void RunCarM2oExportProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_m2o.txt");
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_car_m2o");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);

            string sds = Path.Combine(MafiaEnvironment.PcFolder, "sds");
            var stock = new FileInfo(Path.Combine(sds, "cars", Source + ".sds"));
            var stockWinter = new FileInfo(Path.Combine(sds, "cars", Source + "_z.sds"));

            // A clone on scratch copies: the archives as shipped, the tables as the working copies have them.
            string tables = Copy(SdsMeshLoader.EnsureExtracted(new FileInfo(Path.Combine(sds, "tables", "tables.sds"))),
                Path.Combine(scratch, "tables"));
            string ingame = Copy(SdsMeshLoader.EnsureExtracted(new FileInfo(Path.Combine(sds, "tables", "ingame.sds"))),
                Path.Combine(scratch, "ingame"));
            var cars = new List<(string From, string To)>();
            var memory = new List<SdsMemoryRequirements>();
            foreach (FileInfo archive in new[] { stock, stockWinter })
            {
                if (!archive.Exists) continue;
                string from = Path.Combine(scratch, "stock_" + Path.GetFileNameWithoutExtension(archive.Name));
                SdsArchive.Open(archive.FullName).Extract(from);
                memory.Add(SdsMemoryRequirements.FromArchive(archive.FullName));
                memory[^1].Save(from);
                cars.Add((from, Path.Combine(scratch, "clone_" + Path.GetFileNameWithoutExtension(archive.Name))));
            }
            string text = Path.Combine(scratch, "text", "tables");
            Directory.CreateDirectory(text);
            File.WriteAllText(Path.Combine(text, "TextDatabase.dat"), "", new UTF8Encoding(true));
            var folders = new CarCloneFolders(tables, ingame, cars, [Path.Combine(scratch, "text")]);
            CarCloneResult? clone = CarCloner.CloneExtracted(folders, Source, Name, traffic: false, Title, out string? refused);
            Check("a clone to export", clone != null, refused ?? "");
            if (clone == null) return;

            string built = Path.Combine(scratch, "built");
            Directory.CreateDirectory(built);
            var archives = new List<FileInfo>();
            for (int i = 0; i < cars.Count; i++)
            {
                Check($"{Path.GetFileName(cars[i].To)}: the clone's working copy carries the source's memory requirements",
                    SdsMemoryRequirements.Load(cars[i].To) is { Count: > 0 });
                string path = Path.Combine(built, Name.ToLowerInvariant() + (i == 0 ? "" : "_z") + ".sds");
                Pack(cars[i].To, path, SdsMemoryRequirements.Load(cars[i].To));
                archives.Add(new FileInfo(path));
            }

            var sources = new CarM2oExportSources(
                archives[0],
                archives.Count > 1 ? archives[1] : null,
                Path.Combine(tables, "tables", "vehicles.tbl"),
                Path.Combine(ingame, "tables", "PaintCombinations.tbl"),
                [("en", Path.Combine(text, "TextDatabase.dat"))],
                CarCloner.SourceOf(cars[0].To),
                stock);
            string output = Path.Combine(scratch, "export");

            Check("refuses a resource name the multiplayer would not take",
                CarM2oExport.ExportFrom(sources, output, "Bad Name", out string? badName) == null && badName != null, badName ?? "");
            string occupied = Path.Combine(scratch, "occupied");
            Directory.CreateDirectory(occupied);
            File.WriteAllText(Path.Combine(occupied, "server.json"), "{}");
            Check("refuses a folder that holds something else",
                CarM2oExport.ExportFrom(sources, occupied, "car-test", out string? busy) == null && busy != null
                && Directory.GetFileSystemEntries(occupied).Length == 1, busy ?? "");
            // The stock archive under another file name: nothing inside it is filed under that name.
            var misnamed = new FileInfo(Path.Combine(built, "not_this_car.sds"));
            File.Copy(stock.FullName, misnamed.FullName);
            Check("refuses an archive that is not filed under its own name",
                CarM2oExport.ExportFrom(sources with { Archive = misnamed, WinterArchive = null }, output, "car-test", out string? alien) == null
                && alien != null && !Directory.Exists(output), alien ?? "");

            // A folder whose manifests are there and do not read: left as it is, not replaced with this one car.
            string broken = Path.Combine(scratch, "broken");
            Directory.CreateDirectory(broken);
            const string BadList = "{ \"vehicles\": [ { \"model\": \"Earlier_Car\" }, ", BadPackage = "{ \"name\": \"mine\", \"custom\": tru";
            File.WriteAllText(Path.Combine(broken, CarM2oExport.VehiclesFile), BadList);
            File.WriteAllText(Path.Combine(broken, CarM2oExport.PackageFile), "{ \"name\": \"mine\" }");
            Check("refuses a folder whose vehicles.json does not read, and leaves it and everything else as it was",
                CarM2oExport.ExportFrom(sources, broken, "car-test", out string? unreadList) == null && unreadList != null
                && File.ReadAllText(Path.Combine(broken, CarM2oExport.VehiclesFile)) == BadList
                && Directory.GetFileSystemEntries(broken).Length == 2, unreadList ?? "exported");
            File.WriteAllText(Path.Combine(broken, CarM2oExport.VehiclesFile), "{ \"vehicles\": [] }");
            File.WriteAllText(Path.Combine(broken, CarM2oExport.PackageFile), BadPackage);
            Check("refuses a folder whose package.json does not read, the same way",
                CarM2oExport.ExportFrom(sources, broken, "car-test", out string? unreadPackage) == null && unreadPackage != null
                && File.ReadAllText(Path.Combine(broken, CarM2oExport.PackageFile)) == BadPackage
                && Directory.GetFileSystemEntries(broken).Length == 2, unreadPackage ?? "exported");

            CarM2oExportResult? result = CarM2oExport.ExportFrom(sources, output, CarM2oExport.DefaultResource(Name), out refused);
            Check("exports the clone", result != null, refused ?? "");
            if (result == null) return;
            foreach (string note in result.Notes) sb.AppendLine("    note: " + note);
            Check("under the model's name, title and source car",
                result.Model == Name && result.Title == Title && result.BasedOn == "Shubert_38" && result.Vehicles == 1,
                $"{result.Model} / {result.Title} / {result.BasedOn}");
            Check("no note of shared buffers or packer figures", result.Notes.Count == 0, string.Join(" | ", result.Notes));

            JsonNode package = JsonNode.Parse(File.ReadAllText(Path.Combine(output, CarM2oExport.PackageFile)))!;
            var shipped = (package["mafiahub"]?["files"] as JsonArray)?.Select(f => f!.GetValue<string>()).ToList() ?? [];
            Check("package.json names the resource and ships the cars and their list",
                package["name"]?.GetValue<string>() == "car-shubert-38-export" && package["version"] != null
                && shipped.Contains("sds/**") && shipped.Contains(CarM2oExport.VehiclesFile), string.Join(", ", shipped));

            JsonNode document = JsonNode.Parse(File.ReadAllText(Path.Combine(output, CarM2oExport.VehiclesFile)))!;
            JsonNode? entry = (document["vehicles"] as JsonArray)?.FirstOrDefault();
            Check("vehicles.json lists the car", document["format"]?.GetValue<int>() == CarM2oExport.Format && entry != null
                && entry["model"]?.GetValue<string>() == Name && entry["title"]?.GetValue<string>() == Title
                && entry["titles"]?["en"]?.GetValue<string>() == Title && entry["basedOn"]?.GetValue<string>() == "Shubert_38");
            if (entry == null) return;
            Check("with the hashes its archive is keyed by",
                entry["hashes"]?["model"]?.GetValue<string>() == "0x" + Fnv64.Hash(Name).ToString("x16")
                && entry["hashes"]?["entityData"]?.GetValue<string>() == "0x" + Fnv64.Hash(Name.ToLowerInvariant()).ToString("x16"));
            GameTable vehicles = GameTable.Load(Path.Combine(tables, "tables", "vehicles.tbl"));
            var values = entry["vehicleTable"]?["values"] as JsonArray;
            Check("with its vehicle-table row, cell for cell",
                entry["vehicleTable"]?["id"]?.GetValue<int>() == clone.VehicleId && values != null
                && values.Count == vehicles.ColumnCount && (entry["vehicleTable"]?["columns"] as JsonArray)?.Count == vehicles.ColumnCount
                && values[0]!.GetValue<int>() == clone.VehicleId && values[2]!.GetValue<string>() == Name,
                $"{values?.Count} cells");
            Check("with its paint combinations", (entry["paintCombinations"]?["values"] as JsonArray)?.Count > 0);

            var files = entry["files"] as JsonArray;
            bool intact = files != null && files.Count == archives.Count;
            for (int i = 0; intact && i < files!.Count; i++)
            {
                string shippedFile = Path.Combine(output, files[i]!["path"]!.GetValue<string>());
                intact = File.Exists(shippedFile)
                    && files[i]!["size"]!.GetValue<long>() == archives[i].Length
                    && files[i]!["sha256"]!.GetValue<string>() == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archives[i].FullName)));
            }
            Check("the archives are in sds/cars/ as they were built, sized and hashed", intact
                && entry["archive"]?.GetValue<string>() == "sds/cars/" + archives[0].Name
                && (archives.Count < 2 || entry["winterArchive"]?.GetValue<string>() == "sds/cars/" + archives[1].Name));
            Check("each named by the path the game loads it from",
                entry["sds"]?.GetValue<string>() == "/sds/cars/" + archives[0].Name
                && (archives.Count < 2 || entry["winterSds"]?.GetValue<string>() == "/sds/cars/" + archives[1].Name));
            Check("nothing else is in the folder",
                Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length == archives.Count + 2);

            // A clone that kept the source's buffer names, and an archive packed with no requirements, are said so.
            string shared = Copy(cars[0].From, Path.Combine(scratch, "shared"));
            string sharedArchive = Path.Combine(built, Source + ".sds");
            Pack(shared, sharedArchive, memory: null);
            CarM2oExportResult? plain = CarM2oExport.ExportFrom(
                sources with { Archive = new FileInfo(sharedArchive), WinterArchive = null, BasedOn = "Shubert_38" },
                output, "car-test", out refused);
            Check("a second car joins the list", plain is { Vehicles: 2, Model: "Shubert_38" }, refused ?? "");
            Check("an archive packed without requirements is noted",
                plain != null && plain.Notes.Any(n => n.Contains("memory", StringComparison.Ordinal)), string.Join(" | ", plain?.Notes ?? []));
            Check("buffers shared with the source car are noted",
                plain != null && plain.Notes.Any(n => n.Contains("buffers", StringComparison.Ordinal)));
            Check("the package.json already there keeps its name",
                JsonNode.Parse(File.ReadAllText(Path.Combine(output, CarM2oExport.PackageFile)))!["name"]?.GetValue<string>() == "car-shubert-38-export");

            CarM2oExportResult? again = CarM2oExport.ExportFrom(sources, output, "car-test", out refused);
            Check("exporting a car again replaces its entry", again is { Vehicles: 2 }, refused ?? "");
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* scratch left behind */ }
            sb.Insert(0, $"CAR M2O EXPORT PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static void Pack(string folder, string path, SdsMemoryRequirements? memory)
    {
        SdsArchive archive = SdsArchive.Pack(folder, GameProfile.MafiaII, memory);
        using FileStream output = File.Create(path);
        archive.Save(output, new SdsWriteOptions());
    }

    private static string Copy(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return to;
    }
}
