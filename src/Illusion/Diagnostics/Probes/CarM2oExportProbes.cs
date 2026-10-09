using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Assets.Sds;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Materials;
using Illusion.Formats.ResourceFormats;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// A car exported as a multiplayer resource, from a clone made and packed on scratch copies (nothing of the
/// game is written): the folder's manifest, the archives it streams with the clone's own vehicles.tbl row, the
/// material library of a car that adds materials, a second car joining the folder, and what an export refuses.
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

            MafiaMaterials.EnsureLoaded();
            MaterialCollection materials = MafiaMaterials.Collection!;
            IReadOnlySet<ulong> stockMaterials = CarM2oExport.StockMaterials(materials);
            var sources = new CarM2oExportSources(
                archives[0],
                archives.Count > 1 ? archives[1] : null,
                Path.Combine(tables, "tables", "vehicles.tbl"),
                [("en", Path.Combine(text, "TextDatabase.dat"))],
                CarCloner.SourceOf(cars[0].To),
                stock,
                materials.FindByHash,
                stockMaterials,
                materials.Libraries.Values.First().Version);
            string output = Path.Combine(scratch, "export");
            string streamed = Path.Combine(output, CarM2oExport.CarsFolder.Replace('/', Path.DirectorySeparatorChar));

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
            // A name a server's asset policy would not take, refused before anything is read.
            var unstreamable = new FileInfo(Path.Combine(built, "not-this-car.sds"));
            File.Copy(stock.FullName, unstreamable.FullName);
            Check("refuses a car name a server does not stream",
                CarM2oExport.ExportFrom(sources with { Archive = unstreamable, WinterArchive = null }, output, "car-test", out string? unnamed) == null
                && unnamed != null && unnamed.Contains("a-z, 0-9 and _") && !Directory.Exists(output), unnamed ?? "");

            string legacy = Path.Combine(scratch, "legacy");
            Directory.CreateDirectory(Path.Combine(legacy, "sds", "cars"));
            File.WriteAllText(Path.Combine(legacy, "vehicles.json"), "{ \"vehicles\": [] }");
            File.WriteAllText(Path.Combine(legacy, CarM2oExport.PackageFile), "{ \"name\": \"mine\" }");
            Check("refuses a folder in the old layout, and leaves it as it was",
                CarM2oExport.ExportFrom(sources, legacy, "car-test", out string? old) == null && old != null
                && Directory.GetFileSystemEntries(legacy).Length == 3, old ?? "exported");

            string broken = Path.Combine(scratch, "broken");
            Directory.CreateDirectory(broken);
            const string BadPackage = "{ \"name\": \"mine\", \"custom\": tru";
            File.WriteAllText(Path.Combine(broken, CarM2oExport.PackageFile), BadPackage);
            Check("refuses a folder whose package.json does not read, and leaves it as it was",
                CarM2oExport.ExportFrom(sources, broken, "car-test", out string? unreadPackage) == null && unreadPackage != null
                && File.ReadAllText(Path.Combine(broken, CarM2oExport.PackageFile)) == BadPackage
                && Directory.GetFileSystemEntries(broken).Length == 1, unreadPackage ?? "exported");

            // A clone of a stock car uses only stock materials: its archives ship as they were built plus its own
            // vehicles.tbl row, a table patch keeping the source car's id and title.
            CarM2oExportResult? result = CarM2oExport.ExportFrom(sources, output, CarM2oExport.DefaultResource(Name), out refused);
            Check("exports the clone", result != null, refused ?? "");
            if (result == null) return;
            foreach (string note in result.Notes) sb.AppendLine("    note: " + note);
            Check("under the model's name, title and source car",
                result.Model == Name && result.Title == Title && result.BasedOn == "Shubert_38" && result.Vehicles == 1,
                $"{result.Model} / {result.Title} / {result.BasedOn}");
            Check("no note of shared buffers or packer figures", result.Notes.Count == 0, string.Join(" | ", result.Notes));
            Check("a car using only stock materials ships no library", result.Materials.Count == 0
                && !Directory.Exists(Path.Combine(output, CarM2oExport.MaterialsFolder.Replace('/', Path.DirectorySeparatorChar))), string.Join(", ", result.Materials));

            JsonNode package = JsonNode.Parse(File.ReadAllText(Path.Combine(output, CarM2oExport.PackageFile)))!;
            Check("package.json names the resource and lists no files (the stream folder ships on its own)",
                package["name"]?.GetValue<string>() == "car-shubert-38-export" && package["version"] != null && package["mafiahub"] == null);
            GameTable stockVehicles = GameTable.Load(sources.VehiclesTable!);
            int sourceRow = stockVehicles.FindRow(2, "Shubert_38");
            (int Id, int Text) source = ((int)stockVehicles.Cell(sourceRow, 0), (int)stockVehicles.Cell(sourceRow, 3));
            string rows = string.Join(" | ", archives.Select(a => OwnRow(Path.Combine(streamed, a.Name), a.FullName, scratch)));
            Check("each archive is the built one plus its vehicles.tbl row: the clone's name, the source car's id and title",
                archives.All(a => OwnRow(Path.Combine(streamed, a.Name), a.FullName, scratch) == (Name, source.Id, source.Text)), rows);
            Check("nothing else is in the folder",
                Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length == archives.Count + 1
                && !File.Exists(Path.Combine(output, "vehicles.json")));

            // One of the car's own materials, as if the toolkit had created it: it is not stock, so it goes into
            // the car's library in stream/materials/, alone, and the archives are the same as without it.
            ulong created = CarMaterials(cars[0].To).First(stockMaterials.Contains);
            var withoutIt = new HashSet<ulong>(stockMaterials);
            withoutIt.Remove(created);
            string adds = Path.Combine(scratch, "adds");
            CarM2oExportResult? withMaterial = CarM2oExport.ExportFrom(sources with { StockMaterials = withoutIt }, adds, "car-test", out refused);
            Check("exports a car that adds a material", withMaterial != null, refused ?? "");
            if (withMaterial == null) return;
            Check("the result names the material",
                withMaterial.Materials.SequenceEqual([materials.FindByHash(created)!.MaterialName.String]), string.Join(", ", withMaterial.Materials));
            string library = Path.Combine(adds, CarM2oExport.MaterialsFolder.Replace('/', Path.DirectorySeparatorChar), Name.ToLowerInvariant() + ".mtl");
            var read = new MaterialLibrary(MaterialVersion.V_57);
            if (File.Exists(library)) read.ReadMatFile(library);
            Check("stream/materials/<car>.mtl holds exactly that material, as the game wrote it",
                read.Materials.Keys.SequenceEqual([created])
                && read.Materials[created].MaterialName.String == materials.FindByHash(created)!.MaterialName.String,
                string.Join(", ", read.Materials.Keys.Select(h => "0x" + h.ToString("x16"))));
            string addsStreamed = Path.Combine(adds, CarM2oExport.CarsFolder.Replace('/', Path.DirectorySeparatorChar));
            Check("the archives are the same as without it", archives.All(a =>
                File.ReadAllBytes(Path.Combine(addsStreamed, a.Name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(streamed, a.Name)))));
            Check("the library is the only thing added",
                Directory.GetFiles(adds, "*", SearchOption.AllDirectories).Length == archives.Count + 2);

            // Exported again with nothing to add, the car's library goes away instead of shipping stale materials.
            CarM2oExport.ExportFrom(sources, adds, "car-test", out refused);
            Check("a re-export that adds nothing removes the stale library", !File.Exists(library), refused ?? "");

            // A material the car uses that no library has cannot travel: refused, nothing written.
            string unknown = Path.Combine(scratch, "unknown");
            Check("refuses a car using a material no library has, writing nothing",
                CarM2oExport.ExportFrom(sources with { StockMaterials = withoutIt, FindMaterial = _ => null }, unknown, "car-test", out string? noMaterial) == null
                && noMaterial != null && !Directory.Exists(unknown), noMaterial ?? "exported");

            // A clone that kept the source's buffer names, and an archive packed with no requirements, are said so.
            string shared = Copy(cars[0].From, Path.Combine(scratch, "shared"));
            string sharedArchive = Path.Combine(built, Source + ".sds");
            Pack(shared, sharedArchive, memory: null);
            CarM2oExportResult? plain = CarM2oExport.ExportFrom(
                sources with { Archive = new FileInfo(sharedArchive), WinterArchive = null, BasedOn = "Shubert_38" },
                output, "car-test", out refused);
            Check("a second car joins the folder", plain is { Vehicles: 2, Model: "Shubert_38" }, refused ?? "");
            Check("an archive packed without requirements is noted",
                plain != null && plain.Notes.Any(n => n.Contains("memory", StringComparison.Ordinal)), string.Join(" | ", plain?.Notes ?? []));
            Check("buffers shared with the source car are noted",
                plain != null && plain.Notes.Any(n => n.Contains("buffers", StringComparison.Ordinal)));
            Check("the package.json already there keeps its name",
                JsonNode.Parse(File.ReadAllText(Path.Combine(output, CarM2oExport.PackageFile)))!["name"]?.GetValue<string>() == "car-shubert-38-export");

            CarM2oExportResult? again = CarM2oExport.ExportFrom(sources, output, "car-test", out refused);
            Check("exporting a car again replaces its archive", again is { Vehicles: 2 }, refused ?? "");
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

    // The material hashes a working copy's meshes use.
    // The vehicles.tbl row an exported archive carries (name, id, text), or null when the archive is anything but
    // the built one plus one Table entry holding that row.
    private static (string Name, int Id, int Text)? OwnRow(string exported, string built, string scratch)
    {
        if (!File.Exists(exported)) return null;
        SdsArchive shipped = SdsArchive.Open(exported), original = SdsArchive.Open(built);
        uint table = shipped.ResourceTypes.FirstOrDefault(t => t.Name == "Table").Id;
        var others = shipped.Entries.Where(e => (uint)e.TypeId != table).ToList();
        if (shipped.Entries.Count != original.Entries.Count + 1 || others.Count != original.Entries.Count
            || !others.Zip(original.Entries).All(p => p.First.Data!.AsSpan().SequenceEqual(p.Second.Data)))
        {
            return null;
        }
        string folder = Path.Combine(scratch, "own_row_" + Guid.NewGuid().ToString("N"));
        shipped.Extract(folder);
        string[] patches = Directory.GetFiles(Path.Combine(folder, "tables"), "patch_*_vehicles.tbl");
        if (patches.Length != 1) return null;
        GameTable row = GameTable.Load(patches[0]);
        return row.RowCount == 1 ? ((string)row.Cell(0, 2), (int)row.Cell(0, 0), (int)row.Cell(0, 3)) : null;
    }

    private static IEnumerable<ulong> CarMaterials(string extracted) =>
        ExtractedSds.Load(extracted).FrameResource!.FrameMaterials.Values
            .SelectMany(block => block.Materials.SelectMany(lod => lod)).Select(slot => slot.MaterialHash).Where(hash => hash != 0).Distinct();

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
