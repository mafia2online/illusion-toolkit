using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Assets.Sds;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.EntityData;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;
using Illusion.Formats.ResourceFormats;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Cloning a car, on scratch copies of the install's working copies (nothing of the game is written): the
/// vehicle, paint, cover-point and traffic rows the clone gains, the renamed root frame, prefab entry and
/// entity data, that every folder still packs, and that the toolkit reads the clone as a car.
/// </summary>
internal static class CarCloneProbes
{
    private const string Source = "shubert_38";
    private const string Name = "Shubert_38_Clone";

    // Output: %TEMP%\illusion_car_clone.txt
    internal static void RunCarCloneProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_clone.txt");
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_car_clone");
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
            string tables = Copy(SdsMeshLoader.EnsureExtracted(new FileInfo(Path.Combine(sds, "tables", "tables.sds"))),
                Path.Combine(scratch, "tables"));
            string ingame = Copy(SdsMeshLoader.EnsureExtracted(new FileInfo(Path.Combine(sds, "tables", "ingame.sds"))),
                Path.Combine(scratch, "ingame"));
            var cars = new List<(string From, string To)>();
            foreach (string suffix in new[] { "", "_z" })
            {
                var archive = new FileInfo(Path.Combine(sds, "cars", Source + suffix + ".sds"));
                if (archive.Exists) cars.Add((SdsMeshLoader.EnsureExtracted(archive), Path.Combine(scratch, "car" + suffix)));
            }
            var folders = new CarCloneFolders(tables, ingame, cars);
            string vehiclesPath = Path.Combine(tables, "tables", "vehicles.tbl");
            GameTable before = GameTable.Load(vehiclesPath);
            int sourceRow = before.FindRow(2, Source);
            int sourceId = (int)before.Cell(sourceRow, 0);

            Check("refuses a name that is not a model name",
                CarCloner.CloneExtracted(folders, Source, "9 bad name", traffic: true, out string? bad) == null && bad != null, bad ?? "");
            Check("refuses a car the vehicle table does not list",
                CarCloner.CloneExtracted(folders, "no_such_car", Name, traffic: true, out string? none) == null && none != null, none ?? "");
            Check("refuses a name the table already has",
                CarCloner.CloneExtracted(folders, Source, "Smith_V8", traffic: true, out string? taken) == null && taken != null, taken ?? "");
            Check("a refusal writes nothing", !Directory.Exists(cars[0].To));

            CarCloneResult? result = CarCloner.CloneExtracted(folders, Source, Name, traffic: true, out string? refused);
            Check("clones shubert_38", result != null, refused ?? "");
            if (result == null) return;
            foreach (string note in result.Notes) sb.AppendLine("    note: " + note);

            GameTable vehicles = GameTable.Load(vehiclesPath);
            int row = vehicles.FindRow(2, Name);
            Check("vehicles.tbl gains one row", vehicles.RowCount == before.RowCount + 1 && row >= 0);
            Check("under a fresh id", row >= 0 && (int)vehicles.Cell(row, 0) == result.VehicleId
                && Enumerable.Range(0, before.RowCount).All(i => (int)before.Cell(i, 0) != result.VehicleId), $"id {result.VehicleId}");
            Check("with the source car's class, price and flags",
                row >= 0 && Enumerable.Range(0, vehicles.ColumnCount).Where(c => c is not 0 and not 2)
                    .All(c => Equals(vehicles.Cell(row, c), before.Cell(sourceRow, c))));
            byte[] raw = File.ReadAllBytes(vehiclesPath);
            Check("the name cell stores FNV64 of the name beside it",
                IndexOf(raw, BitConverter.GetBytes(Fnv64.Hash(Name))) >= 0);
            Check("the source row is untouched",
                Enumerable.Range(0, vehicles.ColumnCount).All(c => Equals(vehicles.Cell(sourceRow, c), before.Cell(sourceRow, c))));

            GameTable paint = GameTable.Load(Path.Combine(ingame, "tables", "PaintCombinations.tbl"));
            int paintRow = paint.FindRow(1, Name);
            Check("PaintCombinations.tbl lists the clone under its id",
                paintRow >= 0 && (int)paint.Cell(paintRow, 0) == result.VehicleId);

            string cover = Path.Combine(ingame, "tables", "AiProps", Name + ".xml");
            Check("cover points copied under the new name",
                File.Exists(cover) && File.ReadAllText(cover).Contains($"name=\"{Name}\"", StringComparison.Ordinal)
                && SdsManifest.Load(ingame).HasFile($"/tables/AiProps/{Name}"));

            int trafficRows = 0;
            bool consistent = true;
            foreach (string path in Directory.EnumerateFiles(Path.Combine(ingame, "tables"), "CARM*.tbl"))
            {
                GameTable table = GameTable.Load(path);
                for (int r = 0; r < table.RowCount; r++)
                {
                    int count = (int)table.Cell(r, 1);
                    var ids = Enumerable.Range(0, count).Select(i => (int)table.Cell(r, 2 + i)).ToList();
                    if (!ids.Contains(result.VehicleId)) continue;
                    trafficRows++;
                    consistent &= ids.Contains(sourceId) && ids[^1] == result.VehicleId;
                }
            }
            Check("traffic picks the clone wherever it picks the source", trafficRows == result.TrafficRows && trafficRows > 0 && consistent,
                $"{trafficRows} rows");

            foreach ((string _, string to) in cars)
            {
                string label = Path.GetFileName(to);
                SdsManifest manifest = SdsManifest.Load(to);
                var frame = new FrameResource(manifest.GetFiles("FrameResource")[0]);
                var names = frame.FrameObjects.Values.OfType<FrameObjectBase>().Select(f => f.Name.String).ToList();
                Check($"{label}: the root frame carries the new name",
                    names.Contains(Name) && !names.Contains("Shubert_38"));
                // The name is written twice: as the root's name and as the root Frame's actor link.
                byte[] frameBytes = File.ReadAllBytes(manifest.GetFiles("FrameResource")[0]);
                Check($"{label}: nothing in the frames names the old car",
                    Occurrences(frameBytes, BitConverter.GetBytes(Fnv64.Hash("Shubert_38"))) == 0
                    && Occurrences(frameBytes, BitConverter.GetBytes(Fnv64.Hash(Name))) == 2);
                var nameTable = new FrameNameTable(manifest.GetFiles("FrameNameTable")[0]);
                Check($"{label}: so does the name table", nameTable.Names.Values.Contains(Name),
                    string.Join(", ", nameTable.Names.Values));
                PrefabFile prefab = PrefabFile.Load(manifest.GetFiles("PREFAB")[0]);
                Check($"{label}: the prefab entry follows it",
                    prefab.Contains(Fnv64.Hash(Name)) && !prefab.Contains(Fnv64.Hash("Shubert_38")));
                EntityDataStorageFile storage = EntityDataStorageFile.Load(manifest.GetFiles("EntityDataStorage")[0]);
                Check($"{label}: the entity data is filed under the name in lower case",
                    storage.Hash == Fnv64.Hash(Name.ToLowerInvariant()) && storage.TableCount > 0);
                Check($"{label}: packs", Packs(to));
            }
            Check("the toolkit reads the clone as a car", Car.ReadFrom(cars[0].To) != null);
            Check("tables.sds packs", Packs(tables));
            Check("ingame.sds packs", Packs(ingame));
            // Packing recompiles every XML resource; what it compiles has to decompile back to the same text,
            // or a Build of the table archives quietly rewrites configs nobody touched.
            foreach (string folder in new[] { tables, ingame })
            {
                string back = Path.Combine(scratch, Path.GetFileName(folder) + "_back");
                SdsArchive archive = SdsArchive.Pack(folder, GameProfile.MafiaII);
                using (var stream = new MemoryStream())
                {
                    archive.Save(stream, new SdsWriteOptions());
                    string packed = Path.Combine(scratch, Path.GetFileName(folder) + ".sds");
                    File.WriteAllBytes(packed, stream.ToArray());
                    SdsArchive.Open(packed).Extract(back);
                }
                var changed = new List<string>();
                int xmls = 0;
                foreach (string file in Directory.EnumerateFiles(folder, "*.xml", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(folder, file);
                    if (rel == "SDSContent.xml") continue;
                    xmls++;
                    string again = Path.Combine(back, rel);
                    if (!File.Exists(again) || File.ReadAllText(again) != File.ReadAllText(file)) changed.Add(rel);
                }
                Check($"{Path.GetFileName(folder)}: every XML survives a pack unchanged", xmls > 0 && changed.Count == 0,
                    $"{changed.Count} of {xmls} differ: {string.Join(", ", changed.Take(5))}");
            }
            // A Build prunes manifest entries whose file is gone; an XML resource sits on disk as name + ".xml",
            // and every one of them used to be pruned that way.
            foreach (string folder in new[] { tables, ingame, cars[0].To })
            {
                List<string> pruned = SdsWriter.PruneMissingEntries(folder);
                Check($"{Path.GetFileName(folder)}: a Build drops no manifest entry", pruned.Count == 0,
                    string.Join(", ", pruned.Take(5)));
            }
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
            sb.Insert(0, $"CAR CLONE PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static bool Packs(string folder)
    {
        SdsArchive archive = SdsArchive.Pack(folder, GameProfile.MafiaII);
        using var output = new MemoryStream();
        archive.Save(output, new SdsWriteOptions());
        return output.Length > 0;
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

    private static int Occurrences(byte[] haystack, byte[] needle)
    {
        int count = 0;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) count++;
        }
        return count;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }
}
