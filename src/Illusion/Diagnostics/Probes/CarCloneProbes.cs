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
    private const string Title = "Shubert 38 Clone";

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
            var text = new List<string>();
            foreach (string language in Directory.GetDirectories(MafiaEnvironment.PcFolder, "sds_*"))
            {
                var archive = new FileInfo(Path.Combine(language, "text", "text_default.sds"));
                if (archive.Exists) text.Add(Copy(SdsMeshLoader.EnsureExtracted(archive), Path.Combine(scratch, "text_" + Path.GetFileName(language))));
            }
            var folders = new CarCloneFolders(tables, ingame, cars, text);
            string vehiclesPath = Path.Combine(tables, "tables", "vehicles.tbl");
            GameTable before = GameTable.Load(vehiclesPath);
            int sourceRow = before.FindRow(2, Source);
            int sourceId = (int)before.Cell(sourceRow, 0);

            Check("refuses a name that is not a model name",
                CarCloner.CloneExtracted(folders, Source, "9 bad name", traffic: true, Title, out string? bad) == null && bad != null, bad ?? "");
            Check("refuses a car the vehicle table does not list",
                CarCloner.CloneExtracted(folders, "no_such_car", Name, traffic: true, Title, out string? none) == null && none != null, none ?? "");
            Check("refuses a name the table already has",
                CarCloner.CloneExtracted(folders, Source, "Smith_V8", traffic: true, Title, out string? taken) == null && taken != null, taken ?? "");
            Check("a refusal writes nothing", !Directory.Exists(cars[0].To));

            Check("refuses a title that is not one short line",
                CarCloner.CloneExtracted(folders, Source, Name, traffic: true, "two\nlines", out string? badTitle) == null && badTitle != null, badTitle ?? "");
            string[] textBefore = [.. text.Select(t => File.ReadAllText(Path.Combine(t, "tables", "TextDatabase.dat")))];
            CarCloneResult? result = CarCloner.CloneExtracted(folders, Source, Name, traffic: true, Title, out string? refused);
            Check("clones shubert_38", result != null, refused ?? "");
            if (result == null) return;
            foreach (string note in result.Notes) sb.AppendLine("    note: " + note);

            GameTable vehicles = GameTable.Load(vehiclesPath);
            int row = vehicles.FindRow(2, Name);
            Check("vehicles.tbl gains one row", vehicles.RowCount == before.RowCount + 1 && row >= 0);
            Check("under a fresh id", row >= 0 && (int)vehicles.Cell(row, 0) == result.VehicleId
                && Enumerable.Range(0, before.RowCount).All(i => (int)before.Cell(i, 0) != result.VehicleId), $"id {result.VehicleId}");
            // A name of its own: one new line per language, under an id nothing else uses, and the row points at it.
            Check("the clone has a text id of its own", text.Count > 0 && result.TextId is { } own
                && row >= 0 && (int)vehicles.Cell(row, 3) == own && own != (int)before.Cell(sourceRow, 3), $"id {result.TextId}");
            for (int t = 0; t < text.Count && result.TextId is { } titleId; t++)
            {
                string table = Path.Combine(text[t], "tables", "TextDatabase.dat");
                string after = File.ReadAllText(table);
                string line = Illusion.Assets.Text.GameText.Key(titleId) + ":" + Title + "\r\n";
                Check($"{Path.GetFileName(text[t])}: the title is one added line, everything else as it was",
                    Illusion.Assets.Text.GameText.Find(table, titleId) == Title
                    && after.Length == textBefore[t].Length + line.Length && after.Replace(line, "") == textBefore[t]);
                Check($"{Path.GetFileName(text[t])}: the text archive packs", Packs(text[t]));
            }
            Check("with the source car's class, price and flags",
                row >= 0 && Enumerable.Range(0, vehicles.ColumnCount).Where(c => c is not 0 and not 2 and not 3)
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
                // Buffers of its own: none of the names the source car's pools hold, every LOD still resolved,
                // and the same bytes buffer for buffer.
                ExtractedSds cloned = ExtractedSds.Load(to);
                ExtractedSds source = ExtractedSds.Load(cars.First(c => c.To == to).From);
                var sourceNames = new HashSet<ulong>(source.VertexBuffers.Buffers.Keys.Concat(source.IndexBuffers.Buffers.Keys));
                var cloneNames = cloned.VertexBuffers.Buffers.Keys.Concat(cloned.IndexBuffers.Buffers.Keys).ToList();
                Check($"{label}: the clone's buffers have names of their own",
                    cloneNames.Count == sourceNames.Count && cloneNames.Count > 0 && !cloneNames.Any(sourceNames.Contains),
                    $"{cloneNames.Count} buffers, {cloneNames.Count(sourceNames.Contains)} still shared");
                var lods = cloned.FrameResource!.FrameGeometries.Values.SelectMany(g => g.LOD ?? []).ToList();
                Check($"{label}: every level of detail finds its buffers",
                    lods.Count > 0 && lods.All(l => cloned.VertexBuffers.GetBuffer(l.VertexBufferRef.Hash) != null
                        && cloned.IndexBuffers.GetBuffer(l.IndexBufferRef.Hash) != null), $"{lods.Count} levels");
                Check($"{label}: the buffers hold the source's bytes, in the source's order",
                    cloned.VertexBuffers.Buffers.Values.Select(b => b.Data.Length)
                        .SequenceEqual(source.VertexBuffers.Buffers.Values.Select(b => b.Data.Length))
                    && cloned.VertexBuffers.Buffers.Values.Zip(source.VertexBuffers.Buffers.Values)
                        .All(pair => pair.First.Data.AsSpan().SequenceEqual(pair.Second.Data))
                    && cloned.IndexBuffers.Buffers.Values.Zip(source.IndexBuffers.Buffers.Values)
                        .All(pair => pair.First.GetData().AsSpan().SequenceEqual(pair.Second.GetData())));
                // The name is written twice: as the root's name and as the root Frame's actor link.
                byte[] frameBytes = File.ReadAllBytes(manifest.GetFiles("FrameResource")[0]);
                Check($"{label}: nothing in the frames names the old car",
                    Occurrences(frameBytes, BitConverter.GetBytes(Fnv64.Hash("Shubert_38"))) == 0
                    && Occurrences(frameBytes, BitConverter.GetBytes(Fnv64.Hash(Name))) == 2);
                var nameTable = new FrameNameTable(manifest.GetFiles("FrameNameTable")[0]);
                Check($"{label}: so does the name table", nameTable.Names.Values.Contains(Name),
                    string.Join(", ", nameTable.Names.Values));
                // Every shipped car lists its root first; a clone that led with another frame had no model in game.
                Check($"{label}: the name table still lists the root first",
                    nameTable.FrameData is { Length: > 0 } entries && entries[0].Name == Name,
                    string.Join(", ", (nameTable.FrameData ?? []).Select(e => e.Name)));
                PrefabFile prefab = PrefabFile.Load(manifest.GetFiles("PREFAB")[0]);
                Check($"{label}: the prefab entry follows it",
                    prefab.Contains(Fnv64.Hash(Name)) && !prefab.Contains(Fnv64.Hash("Shubert_38")));
                EntityDataStorageFile storage = EntityDataStorageFile.Load(manifest.GetFiles("EntityDataStorage")[0]);
                Check($"{label}: the entity data is filed under the name in lower case",
                    storage.Hash == Fnv64.Hash(Name.ToLowerInvariant()) && storage.TableCount > 0);
                Check($"{label}: packs", Packs(to));
            }
            Check("the toolkit reads the clone as a car", Car.ReadFrom(cars[0].To) != null);

            // A rebuilt name table has to be the table the archive shipped with — order included.
            int cars_ = 0, same = 0;
            var differing = new List<string>();
            foreach (string archive in Directory.EnumerateFiles(Path.Combine(sds, "cars"), "*.sds"))
            {
                string folder = MafiaEnvironment.ExtractedDir(new FileInfo(archive));
                if (!File.Exists(Path.Combine(folder, "SDSContent.xml"))) continue;
                ExtractedSds loaded = ExtractedSds.Load(folder);
                IReadOnlyList<string> tableFiles = loaded.Manifest.GetFiles("FrameNameTable");
                if (loaded.FrameResource == null || tableFiles.Count == 0) continue;
                // Five shipped tables list a frame twice or name a frame the resource does not hold; a table
                // built from the frames has one entry per frame, so those are out of this comparison.
                FrameNameTable.Data[] shipped = loaded.FrameNameTable?.FrameData ?? [];
                if (shipped.Any(e => e.FrameIndex < 0) || shipped.Select(e => e.FrameIndex).Distinct().Count() != shipped.Length)
                {
                    continue;
                }
                var rebuilt = new FrameNameTable();
                rebuilt.BuildDataFromResource(loaded.FrameResource);
                using var ms = new MemoryStream();
                using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) rebuilt.WriteToFile(writer);
                cars_++;
                if (ms.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(tableFiles[0]))) same++;
                else differing.Add(Path.GetFileNameWithoutExtension(archive));
            }
            Check("rebuilding a car's name table reproduces it byte for byte", cars_ > 50 && same == cars_,
                $"{same} of {cars_}; differ: {string.Join(", ", differing.Take(8))}");
            Check("tables.sds packs", Packs(tables));
            Check("ingame.sds packs", Packs(ingame));
            // Packing recompiles every XML resource; what it compiles has to decompile back to the same text,
            // or a Build of the table archives quietly rewrites configs nobody touched.
            foreach (string folder in new[] { tables, ingame }.Concat(text))
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
            // What a rebuilt archive asks the engine to budget: the figures of the archive that shipped, not the
            // payload sizes a packing handler can measure.
            foreach (string suffix in new[] { "", "_z" })
            {
                var shippedFile = new FileInfo(Path.Combine(sds, "cars", Source + suffix + ".sds"));
                if (!shippedFile.Exists) continue;
                string label = Source + suffix;
                SdsArchive shipped = SdsArchive.Open(shippedFile.FullName);
                string unpacked = Path.Combine(scratch, "memory" + suffix);
                SdsMemoryRequirements stated = SdsMemoryRequirements.Extract(shipped, unpacked);
                Check($"{label}: the shipped archive's figures read as shipped", stated.LooksShipped && stated.Count == shipped.Entries.Count,
                    $"{stated.Count} resources");

                SdsArchive plain = SdsArchive.Pack(unpacked, GameProfile.MafiaII);
                Check($"{label}: a packer left to itself asks for less", plain.SlotRamRequired < shipped.SlotRamRequired
                    && plain.OtherRamRequired == 0, $"slot RAM {plain.SlotRamRequired} against {shipped.SlotRamRequired}");

                SdsArchive rebuilt = SdsArchive.Pack(unpacked, GameProfile.MafiaII, stated);
                Check($"{label}: rebuilt with them it asks for what the shipped archive did",
                    (rebuilt.SlotRamRequired, rebuilt.SlotVramRequired, rebuilt.OtherRamRequired, rebuilt.OtherVramRequired)
                    == (shipped.SlotRamRequired, shipped.SlotVramRequired, shipped.OtherRamRequired, shipped.OtherVramRequired),
                    $"{rebuilt.SlotRamRequired}/{rebuilt.SlotVramRequired}/{rebuilt.OtherRamRequired}/{rebuilt.OtherVramRequired}"
                    + $" against {shipped.SlotRamRequired}/{shipped.SlotVramRequired}/{shipped.OtherRamRequired}/{shipped.OtherVramRequired}");
                static (uint, uint, uint, uint) Figures(ResourceEntry e) =>
                    (e.SlotRamRequired, e.SlotVramRequired, e.OtherRamRequired, e.OtherVramRequired);
                string Describe(SdsArchive a, ResourceEntry e) =>
                    $"{a.ResourceTypes[e.TypeId].Name} {e.Data?.Length} bytes {Figures(e)}";
                Check($"{label}: resource for resource",
                    rebuilt.Entries.Select(Figures).OrderBy(f => f).SequenceEqual(shipped.Entries.Select(Figures).OrderBy(f => f)),
                    string.Join("; ", rebuilt.Entries.Select(e => Describe(rebuilt, e)).Except(shipped.Entries.Select(e => Describe(shipped, e))).Take(6))
                    + " | shipped: " + string.Join("; ", shipped.Entries.Select(e => Describe(shipped, e)).Except(rebuilt.Entries.Select(e => Describe(rebuilt, e))).Take(6)));

                // They survive the round trip through the working copy's side file, and the header of the saved archive.
                stated.Save(unpacked);
                SdsMemoryRequirements? kept = SdsMemoryRequirements.Load(unpacked);
                string saved = Path.Combine(scratch, label + "_rebuilt.sds");
                using (FileStream output = File.Create(saved))
                {
                    SdsArchive.Pack(unpacked, GameProfile.MafiaII, kept).Save(output, new SdsWriteOptions());
                }
                SdsArchive reopened = SdsArchive.Open(saved);
                Check($"{label}: and says so again when saved and reopened", kept is { } again && again.Count == stated.Count
                    && (reopened.SlotRamRequired, reopened.OtherRamRequired) == (shipped.SlotRamRequired, shipped.OtherRamRequired)
                    && reopened.Entries.Select(Figures).OrderBy(f => f).SequenceEqual(shipped.Entries.Select(Figures).OrderBy(f => f)));
            }
            // The clone asks for what its source does: the resources are the same but for a longer name.
            {
                SdsMemoryRequirements stated = SdsMemoryRequirements.FromArchive(Path.Combine(sds, "cars", Source + ".sds"));
                SdsArchive shipped = SdsArchive.Open(Path.Combine(sds, "cars", Source + ".sds"));
                SdsArchive clone = SdsArchive.Pack(cars[0].To, GameProfile.MafiaII, stated);
                Check("the clone, built with its source's requirements, asks for no less than the source",
                    clone.SlotRamRequired >= shipped.SlotRamRequired && clone.OtherRamRequired >= shipped.OtherRamRequired
                    && clone.SlotVramRequired >= shipped.SlotVramRequired,
                    $"slot RAM {clone.SlotRamRequired} against {shipped.SlotRamRequired}, other {clone.OtherRamRequired} against {shipped.OtherRamRequired}");
            }
            // A Build prunes manifest entries whose file is gone; an XML resource sits on disk as name + ".xml",
            // and every one of them used to be pruned that way.
            foreach (string folder in new[] { tables, ingame, cars[0].To }.Concat(text))
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
