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
    /// <summary>
    /// The real <see cref="CarCloner.Clone"/> against the install, made to fail — the one thing the scratch
    /// probe above cannot do, since the clone finds its archives through the game folder.
    /// <para>
    /// UNLIKE EVERY OTHER PROBE THIS ONE WRITES TO THE INSTALL: it clones shubert_38 under a probe name with
    /// the winter archive's pack blocked, so the clone fails after the summer archive is built and the shared
    /// tables' working copies are written — and then checks that all of it was taken back. The files it may
    /// touch are snapshotted first and put back by force at the end whatever happens. The game must not be
    /// running. Output: %TEMP%\illusion_car_clone_rollback.txt
    /// </para>
    /// </summary>
    internal static void RunCarCloneRollbackProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_clone_rollback.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        const string Kept = "illusion_probe_kept", Failing = "illusion_probe_fail";
        var snapshot = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var leftovers = new List<string>();
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.PcFolder, "sds");
            var tablesSds = new FileInfo(Path.Combine(sds, "tables", "tables.sds"));
            var ingameSds = new FileInfo(Path.Combine(sds, "tables", "ingame.sds"));
            string tables = SdsMeshLoader.EnsureExtracted(tablesSds), ingame = SdsMeshLoader.EnsureExtracted(ingameSds);
            List<string> text = [.. Directory.GetDirectories(MafiaEnvironment.PcFolder, "sds_*")
                .Select(d => new FileInfo(Path.Combine(d, "text", "text_default.sds"))).Where(f => f.Exists)
                .Select(SdsMeshLoader.EnsureExtracted)];

            // Everything a clone writes in the shared working copies, as it is now.
            var watched = new List<string> { Path.Combine(tables, "tables", "vehicles.tbl"), Path.Combine(ingame, "SDSContent.xml") };
            watched.AddRange(Directory.GetFiles(Path.Combine(ingame, "tables"), "*.tbl"));
            watched.AddRange(text.Select(t => Path.Combine(t, "tables", "TextDatabase.dat")).Where(File.Exists));
            foreach (string file in watched) snapshot[file] = File.ReadAllBytes(file);
            string aiProps = Path.Combine(ingame, "tables", "AiProps");
            HashSet<string> propsBefore = Directory.Exists(aiProps)
                ? new HashSet<string>(Directory.GetFiles(aiProps), StringComparer.OrdinalIgnoreCase) : [];
            (DateTime Tables, DateTime Ingame) packedAt = (tablesSds.LastWriteTimeUtc, ingameSds.LastWriteTimeUtc);
            int backupsBefore = SdsWriter.ListBackups(tablesSds).Count + SdsWriter.ListBackups(ingameSds).Count;

            bool Untouched() => snapshot.All(f => File.Exists(f.Key) && File.ReadAllBytes(f.Key).AsSpan().SequenceEqual(f.Value))
                && (!Directory.Exists(aiProps) || Directory.GetFiles(aiProps).All(propsBefore.Contains));

            // ── A working copy under the new name is somebody's: refused, and left as it is ──
            string keptFolder = MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(sds, "cars", Kept + ".sds")));
            leftovers.Add(keptFolder);
            Directory.CreateDirectory(keptFolder);
            File.WriteAllText(Path.Combine(keptFolder, "SDSContent.xml"), "work that was never built");
            // A title of 49 characters: the refusal that used to arrive AFTER the folder had been deleted.
            CarCloneOutcome? refused = CarCloner.Clone("shubert_38", Kept, true, new string('a', 49), out string? why);
            Check("a clone onto an existing working copy is refused", refused == null && why != null, why ?? "");
            Check("…and the working copy is still there, with what it held",
                File.Exists(Path.Combine(keptFolder, "SDSContent.xml"))
                && File.ReadAllText(Path.Combine(keptFolder, "SDSContent.xml")) == "work that was never built");
            Check("…and nothing else was written", Untouched());

            // ── A clone that fails half-way takes itself back ──
            // The winter archive cannot be packed: a FOLDER stands where its temporary file goes.
            var summer = new FileInfo(Path.Combine(sds, "cars", Failing + ".sds"));
            string blocker = Path.Combine(sds, "cars", Failing + "_z.sds.tmp");
            if (!File.Exists(Path.Combine(sds, "cars", "shubert_38_z.sds")))
            {
                sb.AppendLine("    (shubert_38 has no winter archive here — the failure cannot be staged)");
            }
            else
            {
                leftovers.Add(blocker);
                leftovers.Add(summer.FullName);
                leftovers.Add(MafiaEnvironment.ExtractedDir(summer));
                leftovers.Add(MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(sds, "cars", Failing + "_z.sds"))));
                Directory.CreateDirectory(blocker);
                CarCloneOutcome? failed = CarCloner.Clone("shubert_38", Failing, true, "Probe car", out why);
                Check("the clone fails when its winter archive cannot be packed, and says it was taken back",
                    failed == null && why != null && why.Contains("taken back", StringComparison.Ordinal), why ?? "");
                Check("it reports nothing left out of place",
                    why != null && !why.Contains("NOT everything", StringComparison.Ordinal), why ?? "");
                summer.Refresh();
                Check("the summer archive it had already built is gone", !summer.Exists);
                Check("both clone folders are gone",
                    !Directory.Exists(MafiaEnvironment.ExtractedDir(summer))
                    && !Directory.Exists(MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(sds, "cars", Failing + "_z.sds")))));
                Check("vehicles.tbl, the paint and traffic tables, the cover points, the manifest and the text are as they were",
                    Untouched());
                tablesSds.Refresh();
                ingameSds.Refresh();
                Check("tables.sds and ingame.sds were never packed, and no backup of them was left",
                    tablesSds.LastWriteTimeUtc == packedAt.Tables && ingameSds.LastWriteTimeUtc == packedAt.Ingame
                    && SdsWriter.ListBackups(tablesSds).Count + SdsWriter.ListBackups(ingameSds).Count == backupsBefore);

                // The point of taking it back: the same clone can be tried again (and is refused only by
                // the block that is still standing, not by "already exists").
                CarCloneOutcome? again = CarCloner.Clone("shubert_38", Failing, true, "Probe car", out why);
                Check("the same clone can be tried again — it is not refused as already existing",
                    again == null && why != null && !why.Contains("already", StringComparison.Ordinal), why ?? "");
                Check("…and the second failure leaves nothing behind either", Untouched());
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            // Whatever the checks said, the install is put back by force.
            int forced = 0;
            foreach ((string file, byte[] bytes) in snapshot)
            {
                if (File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes)) continue;
                File.WriteAllBytes(file, bytes);
                forced++;
            }
            foreach (string path in leftovers)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                }
                catch (IOException) { sb.AppendLine("    could not remove " + path); }
            }
            if (forced > 0) sb.AppendLine($"    {forced} shared file(s) had to be put back by the probe itself");
            sb.Insert(0, $"CAR CLONE ROLLBACK PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

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

            // The clone built under another car's name: everything the clone's name keyed is the other name's now.
            {
                const string Other = "Shubert_38_destr";
                string substitute = Path.Combine(scratch, "substitute");
                foreach (string note in CarCloner.SubstituteExtracted(cars[0].To, substitute, Name, Other)) sb.AppendLine("    note: " + note);
                ExtractedSds swapped = ExtractedSds.Load(substitute);
                ExtractedSds clone = ExtractedSds.Load(cars[0].To);
                var names = swapped.FrameResource!.FrameObjects.Values.OfType<FrameObjectBase>().Select(f => f.Name.String).ToList();
                Check("substitute: the root frame carries the other car's name", names.Contains(Other) && !names.Contains(Name));
                Check("substitute: the name table lists it first",
                    swapped.FrameNameTable?.FrameData is { Length: > 0 } listed && listed[0].Name == Other);
                PrefabFile prefab = PrefabFile.Load(swapped.Manifest.GetFiles("PREFAB")[0]);
                Check("substitute: the prefab entry follows it", prefab.Contains(Fnv64.Hash(Other)) && !prefab.Contains(Fnv64.Hash(Name)));
                Check("substitute: the entity data is filed under it in lower case",
                    EntityDataStorageFile.Load(swapped.Manifest.GetFiles("EntityDataStorage")[0]).Hash == Fnv64.Hash(Other.ToLowerInvariant()));
                var cloneBuffers = new HashSet<ulong>(clone.VertexBuffers.Buffers.Keys.Concat(clone.IndexBuffers.Buffers.Keys));
                var lods = swapped.FrameResource.FrameGeometries.Values.SelectMany(g => g.LOD ?? []).ToList();
                Check("substitute: the buffers are named for it and every level of detail finds them",
                    !swapped.VertexBuffers.Buffers.Keys.Concat(swapped.IndexBuffers.Buffers.Keys).Any(cloneBuffers.Contains)
                    && lods.Count > 0 && lods.All(l => swapped.VertexBuffers.GetBuffer(l.VertexBufferRef.Hash) != null
                        && swapped.IndexBuffers.GetBuffer(l.IndexBufferRef.Hash) != null)
                    && swapped.VertexBuffers.GetBuffer(Fnv64.Hash(Other + ".Root.L0.VB0")) != null);
                Check("substitute: packs and reads as a car", Packs(substitute) && Car.ReadFrom(substitute) != null);

                // A source that is not keyed by the name it is said to have: no root frame of that name, no
                // prefab entry, entity data filed elsewhere. It used to be built all the same, with a note.
                string unkeyed = Path.Combine(scratch, "unkeyed");
                string? refusedBecause = null;
                try
                {
                    CarCloner.SubstituteExtracted(cars[0].To, unkeyed, "Not_Its_Name", Other);
                }
                catch (InvalidDataException ex)
                {
                    refusedBecause = ex.Message;
                }
                Check("substitute: a car that is not keyed by the model name given is a failure, not a note",
                    refusedBecause != null, refusedBecause ?? "built without complaint");

                // What a pack would leave out, asked before packing.
                string shortOf = Path.Combine(scratch, "short");
                Directory.CreateDirectory(shortOf);
                foreach (string file in Directory.GetFiles(substitute, "*", SearchOption.AllDirectories))
                {
                    string copy = Path.Combine(shortOf, Path.GetRelativePath(substitute, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(file, copy);
                }
                string? gone = Directory.GetFiles(shortOf, "*.dds").FirstOrDefault();
                if (gone != null)
                {
                    File.Delete(gone);
                    IReadOnlyList<string> missing = Illusion.Assets.Sds.SdsWriter.MissingEntries(shortOf);
                    Check("a working copy short of a file its manifest names says which, before any pack",
                        missing.Count == 1 && missing.Any(m => string.Equals(m.TrimStart('/', '\\'), Path.GetFileName(gone), StringComparison.OrdinalIgnoreCase))
                        && Illusion.Assets.Sds.SdsWriter.MissingEntries(substitute).Count == 0,
                        $"{missing.Count} missing: " + string.Join(", ", missing.Take(4)));
                }
            }

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

            // ════ An operation that writes several files takes itself back ════
            // The journal on scratch files: each kind of write a clone or a substitution makes, then undone.
            {
                string lab = Path.Combine(scratch, "journal");
                string props = Path.Combine(lab, "AiProps"), work = Path.Combine(lab, "target.sds"), made = Path.Combine(lab, "clone.sds");
                Directory.CreateDirectory(props);
                Directory.CreateDirectory(work);
                string kept = Path.Combine(lab, "vehicles.tbl"), absent = Path.Combine(lab, "new.tbl");
                string archive = Path.Combine(lab, "tables.sds"), backup = archive + ".backup", madeFile = Path.Combine(lab, "clone_archive.sds");
                File.WriteAllBytes(kept, [1, 2, 3]);
                File.WriteAllText(Path.Combine(props, "old.xml"), "old");
                File.WriteAllBytes(archive, [9, 9, 9]);
                File.WriteAllText(Path.Combine(work, "SDSContent.xml"), "work that was never built");

                var journal = new GameWriteJournal();
                journal.Remember(kept);
                journal.Remember(absent);
                journal.RememberContents(props);
                journal.WillCreateFolder(made);
                journal.WillCreateFile(madeFile);
                journal.MoveAside(work);
                journal.WillCreateFolder(work);
                Check("a folder moved aside is out of the way, not gone",
                    !Directory.Exists(work) && Directory.GetDirectories(lab, "target.sds.aside-*").Length == 1);

                // …what the operation then does, up to the step that fails.
                File.WriteAllBytes(kept, [7]);
                File.WriteAllBytes(absent, [7]);
                File.WriteAllText(Path.Combine(props, "new.xml"), "new");
                Directory.CreateDirectory(made);
                File.WriteAllText(Path.Combine(made, "SDSContent.xml"), "clone");
                File.WriteAllBytes(madeFile, [5]);
                File.WriteAllBytes(madeFile + ".tmp", [5]);
                Directory.CreateDirectory(work);
                File.WriteAllText(Path.Combine(work, "SDSContent.xml"), "substituted");
                File.Copy(archive, backup);
                File.WriteAllBytes(archive, [4, 4]);
                journal.Replaced(archive, backup);

                List<string> problems = journal.Undo();
                Check("taking it back reports nothing left out of place", problems.Count == 0, string.Join("; ", problems));
                Check("a rewritten file has its bytes back, and one that did not exist is gone",
                    File.ReadAllBytes(kept).AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }) && !File.Exists(absent));
                Check("a file added to a remembered folder is removed, the ones it had are kept",
                    !File.Exists(Path.Combine(props, "new.xml")) && File.Exists(Path.Combine(props, "old.xml")));
                Check("a created folder and a created archive are removed, the half-written .tmp with it",
                    !Directory.Exists(made) && !File.Exists(madeFile) && !File.Exists(madeFile + ".tmp"));
                Check("a replaced archive is restored from its backup, and the backup does not stay behind",
                    File.ReadAllBytes(archive).AsSpan().SequenceEqual(new byte[] { 9, 9, 9 }) && !File.Exists(backup));
                Check("the folder moved aside is back with what it held",
                    File.Exists(Path.Combine(work, "SDSContent.xml"))
                    && File.ReadAllText(Path.Combine(work, "SDSContent.xml")) == "work that was never built"
                    && Directory.GetDirectories(lab, "target.sds.aside-*").Length == 0);

                var kept2 = new GameWriteJournal();
                kept2.MoveAside(work);
                Directory.CreateDirectory(work);
                kept2.Commit();
                Check("on success the folder moved aside is dropped",
                    Directory.Exists(work) && Directory.GetDirectories(lab, "target.sds.aside-*").Length == 0);
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
