using System.Text.RegularExpressions;
using Illusion.Assets.Sds;
using Illusion.Assets.Text;
using Illusion.Formats.Archive;
using Illusion.Formats.EntityData;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;
using Illusion.Formats.ResourceFormats;

namespace Illusion.Assets.Cars;

/// <summary>
/// Makes a new car out of an existing one: the same model, tuning and sounds under a new name, registered so the
/// game knows it.
///
/// <para>
/// What ties a car together, measured on the shipped cars: <c>vehicles.tbl</c> (tables.sds) lists it by id and
/// model name, and the game loads <c>/sds/cars/&lt;name in lower case&gt;.sds</c> for it. Inside, the root
/// frame carries the model name, the PREFAB entry (seats, doors, wheels, deformation) is keyed by FNV64 of that
/// name, and the entity-data storage (the tuning tables) by FNV64 of the name in lower case. Paint combinations
/// (<c>PaintCombinations.tbl</c>) and the cover points around the car (<c>AiProps/&lt;name&gt;.xml</c>) live in
/// ingame.sds under the id and the name; traffic (<c>CARM*.tbl</c>) picks cars by id.
/// </para>
/// <para>
/// The vertex and index buffers get names of their own as well. The game keeps buffers by name across every
/// archive it has loaded, so a clone that kept the source's names would draw whichever of the two shapes
/// streamed first the moment its model is edited — and so would the car it was cloned from. Textures, materials
/// and sounds stay shared with the source: the clone looks and sounds like it until those are changed too.
/// </para>
/// </summary>
public static partial class CarCloner
{
    /// <summary>A model name fits a String32 cell with its terminator.</summary>
    public const int MaxNameLength = 31;

    private const int VehicleIdColumn = 0;
    private const int VehicleNameColumn = 2;
    private const int VehicleTextColumn = 3;

    /// <summary>Where the ids of clone names start: clear of the car names the game and its DLCs ship
    /// (60000000–60000059).</summary>
    private const int FirstTitleId = 60001000;
    private const int PaintIdColumn = 0;
    private const int PaintNameColumn = 1;
    private const int TrafficCountColumn = 1;
    private const int TrafficFirstIdColumn = 2;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    /// <summary>Why <paramref name="name"/> cannot be a model name, or null when it can.</summary>
    public static string? Refuse(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "give the new car a name";
        if (name.Length > MaxNameLength) return $"'{name}' is longer than the {MaxNameLength} characters a model name holds";
        if (!NamePattern().IsMatch(name)) return $"'{name}' — a model name is a letter, then letters, digits and '_'";
        return null;
    }

    /// <summary>
    /// Clones a car of the game the toolkit has open, then builds the new archives and the two table archives
    /// (keeping a backup of each that existed). <paramref name="source"/> is the car's archive or model name
    /// (<c>shubert_38</c>). With <paramref name="traffic"/>, every traffic row that can pick the source car can
    /// pick the clone as well. With <paramref name="title"/>, the clone gets a name of its own in the text of
    /// every installed language — without it, it is called what the source car is called.
    /// </summary>
    public static CarCloneOutcome? Clone(string source, string name, bool traffic, string? title, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(name);
        refusal = Refuse(name);
        if (refusal != null) return null;
        if (!MafiaEnvironment.IsInitialized)
        {
            refusal = "the game folder is not set";
            return null;
        }

        string sds = Path.Combine(MafiaEnvironment.PcFolder, "sds");
        var tablesSds = new FileInfo(Path.Combine(sds, "tables", "tables.sds"));
        var ingameSds = new FileInfo(Path.Combine(sds, "tables", "ingame.sds"));
        string stem = Path.GetFileNameWithoutExtension(source).ToLowerInvariant();
        var cars = new List<(FileInfo From, FileInfo To)>();
        foreach (string suffix in new[] { "", "_z" })
        {
            var from = new FileInfo(Path.Combine(sds, "cars", stem + suffix + ".sds"));
            var to = new FileInfo(Path.Combine(sds, "cars", name.ToLowerInvariant() + suffix + ".sds"));
            if (!from.Exists)
            {
                if (suffix.Length == 0)
                {
                    refusal = $"there is no car archive {from.Name} in pc\\sds\\cars";
                    return null;
                }
                continue;
            }
            if (to.Exists)
            {
                refusal = $"{to.Name} already exists — pick another name";
                return null;
            }
            cars.Add((from, to));
        }
        if (!tablesSds.Exists || !ingameSds.Exists)
        {
            refusal = "the game's tables.sds / ingame.sds are not where they belong (pc\\sds\\tables)";
            return null;
        }

        List<FileInfo> textSds = title == null
            ? []
            : [.. Directory.GetDirectories(MafiaEnvironment.PcFolder, "sds_*")
                .Select(d => new FileInfo(Path.Combine(d, "text", "text_default.sds"))).Where(f => f.Exists)];

        var folders = new CarCloneFolders(
            SdsMeshLoader.EnsureExtracted(tablesSds),
            SdsMeshLoader.EnsureExtracted(ingameSds),
            [.. cars.Select(c => (SdsMeshLoader.EnsureExtracted(c.From), MafiaEnvironment.ExtractedDir(c.To)))],
            [.. textSds.Select(SdsMeshLoader.EnsureExtracted)]);

        // The clone's resources bear the source's names, so what the source asks the engine to budget is what
        // the clone asks too — written beside the source's working copy here, it is copied with the folder.
        foreach ((FileInfo from, FileInfo _) in cars) SdsWriter.EnsureMemoryRequirements(from);

        // A folder left behind by an attempt that never produced its archive is nobody's working copy.
        foreach ((string _, string to) in folders.Cars) SdsWriter.DeleteExtracted(to);

        CarCloneResult? result = CloneExtracted(folders, stem, name, traffic, title, out refusal);
        if (result == null) return null;

        DateTime when = DateTime.Now;
        var packed = new List<(string Archive, string? Backup)>();
        foreach ((FileInfo _, FileInfo to) in cars)
        {
            SdsWriter.PackResult made = SdsWriter.PackSds(to, createBackup: false, when);
            packed.Add((made.Archive, made.Backup));
        }
        IEnumerable<FileInfo> changed = result.TextId == null ? [tablesSds, ingameSds] : [tablesSds, ingameSds, .. textSds];
        foreach (FileInfo table in changed)
        {
            SdsWriter.PackResult made = SdsWriter.PackSds(table, createBackup: true, when);
            packed.Add((made.Archive, made.Backup));
        }
        // The game finds archives through its cached file list, and a new one is not in it.
        var notes = new List<string>(result.Notes);
        notes.Add(GameFileIndex.Reset()
            ? "the game's file list (vfs.bin) was reset — the next start rebuilds it with the new archives"
            : $"the game's file list was not reset — remove {GameFileIndex.Path} before starting the game, or it will not find the new archives");
        return new CarCloneOutcome(name, result.VehicleId, packed, result.TrafficRows, result.TextId, notes);
    }

    /// <summary>
    /// The clone itself, on working copies only: copies each car folder to its new place and renames what the
    /// name keys there, then adds the vehicle, its paint combinations, its cover points and (optionally) its
    /// traffic slots to the table folders. Nothing is packed. Refuses before writing anything when the source
    /// car is not in the vehicle table or the name is already taken.
    /// </summary>
    public static CarCloneResult? CloneExtracted(
        CarCloneFolders folders, string source, string name, bool traffic, string? title, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(folders);
        refusal = Refuse(name);
        if (refusal != null) return null;
        title = title?.Trim();
        if (title != null && (title.Length == 0 || title.Length > 48 || title.Any(char.IsControl) || title.Contains(':')))
        {
            refusal = "a car's title is one line of at most 48 characters, without ':'";
            return null;
        }

        string vehiclesPath = Path.Combine(folders.Tables, "tables", "vehicles.tbl");
        if (!File.Exists(vehiclesPath))
        {
            refusal = "tables.sds has no vehicles.tbl";
            return null;
        }
        GameTable vehicles = GameTable.Load(vehiclesPath);
        int sourceRow = vehicles.FindRow(VehicleNameColumn, source);
        if (sourceRow < 0)
        {
            refusal = $"vehicles.tbl has no car named '{source}'";
            return null;
        }
        if (vehicles.FindRow(VehicleNameColumn, name) >= 0)
        {
            refusal = $"vehicles.tbl already has a car named '{name}'";
            return null;
        }
        foreach ((string from, string to) in folders.Cars)
        {
            if (!File.Exists(Path.Combine(from, "SDSContent.xml")))
            {
                refusal = $"{from} is not an extracted archive";
                return null;
            }
            if (Directory.Exists(to))
            {
                refusal = $"{to} already exists";
                return null;
            }
        }

        string model = (string)vehicles.Cell(sourceRow, VehicleNameColumn);
        int sourceId = (int)vehicles.Cell(sourceRow, VehicleIdColumn);
        int id = 0;
        for (int i = 0; i < vehicles.RowCount; i++) id = Math.Max(id, (int)vehicles.Cell(i, VehicleIdColumn) + 1);
        var notes = new List<string>();

        // The archives first: they are new folders, so a failure there leaves the tables as they were.
        foreach ((string from, string to) in folders.Cars)
        {
            CopyTree(from, to);
            notes.AddRange(RenameInside(to, model, name));
        }

        int added = vehicles.CopyRow(sourceRow);
        vehicles.SetCell(added, VehicleIdColumn, id);
        vehicles.SetCell(added, VehicleNameColumn, name);

        // A name of its own: one new string, under the same id in every language's text table.
        int? textId = null;
        List<string> textTables = [.. folders.Text.Select(t => Path.Combine(t, "tables", "TextDatabase.dat")).Where(File.Exists)];
        if (title != null && textTables.Count > 0)
        {
            int free = FirstTitleId + id;
            while (textTables.Any(t => GameText.Find(t, free) != null)) free++;
            textId = free;
            vehicles.SetCell(added, VehicleTextColumn, free);
        }
        else if (title != null)
        {
            notes.Add("no text archive (sds_<language>\\text\\text_default.sds) — the clone keeps the source car's name");
        }

        string paintPath = Path.Combine(folders.Ingame, "tables", "PaintCombinations.tbl");
        GameTable? paint = File.Exists(paintPath) ? GameTable.Load(paintPath) : null;
        int paintRow = paint?.FindRow(PaintNameColumn, model) ?? -1;
        if (paint != null && paintRow >= 0)
        {
            int copy = paint.CopyRow(paintRow);
            paint.SetCell(copy, PaintIdColumn, id);
            paint.SetCell(copy, PaintNameColumn, name);
        }
        else
        {
            notes.Add($"PaintCombinations.tbl has no row for {model} — the clone takes the game's default colours");
        }

        int trafficRows = 0;
        var trafficTables = new List<(string Path, GameTable Table)>();
        if (traffic)
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(folders.Ingame, "tables"), "CARM*.tbl"))
            {
                GameTable table = GameTable.Load(path);
                int rows = AddToTraffic(table, sourceId, id);
                if (rows == 0) continue;
                trafficRows += rows;
                trafficTables.Add((path, table));
            }
        }

        AtomicFile.WriteAllBytes(vehiclesPath, vehicles.ToBytes());
        if (textId is { } titleId)
        {
            foreach (string table in textTables) GameText.Add(table, titleId, title!);
        }
        if (paint != null && paintRow >= 0) AtomicFile.WriteAllBytes(paintPath, paint.ToBytes());
        foreach ((string path, GameTable table) in trafficTables) AtomicFile.WriteAllBytes(path, table.ToBytes());
        if (!CopyCoverPoints(folders.Ingame, model, name))
        {
            notes.Add($"ingame.sds has no AiProps/{model} — the clone has no cover points around it");
        }

        refusal = null;
        return new CarCloneResult(id, trafficRows, textId, notes);
    }

    // Every traffic row that can pick the source car can pick the clone too: a row is a weight, a count and
    // that many vehicle ids, in a fixed number of columns.
    private static int AddToTraffic(GameTable table, int sourceId, int id)
    {
        int rows = 0;
        for (int row = 0; row < table.RowCount; row++)
        {
            int count = (int)table.Cell(row, TrafficCountColumn);
            if (TrafficFirstIdColumn + count >= table.ColumnCount) continue;
            bool picksSource = false;
            for (int i = 0; i < count; i++)
            {
                if ((int)table.Cell(row, TrafficFirstIdColumn + i) == sourceId) picksSource = true;
            }
            if (!picksSource) continue;
            table.SetCell(row, TrafficFirstIdColumn + count, id);
            table.SetCell(row, TrafficCountColumn, count + 1);
            rows++;
        }
        return rows;
    }

    private static bool CopyCoverPoints(string ingame, string model, string name)
    {
        string from = $"/tables/AiProps/{model}";
        string to = $"/tables/AiProps/{name}";
        SdsManifest manifest = SdsManifest.Load(ingame);
        IReadOnlyList<(string Name, string Value)>? fields = manifest.EntryFields(from);
        string source = Path.Combine(ingame, "tables", "AiProps", model + ".xml");
        if (fields is not { Count: >= 3 } || fields[^1].Name != "Version"
            || !int.TryParse(fields[^1].Value, out int version) || !File.Exists(source))
        {
            return false;
        }
        string text = File.ReadAllText(source).Replace($"name=\"{model}\"", $"name=\"{name}\"", StringComparison.Ordinal);
        AtomicFile.WriteAllBytes(Path.Combine(ingame, "tables", "AiProps", name + ".xml"),
            System.Text.Encoding.UTF8.GetBytes(text));
        manifest.AddEntry(fields[0].Value, to, version, [.. fields.Skip(2).Take(fields.Count - 3)]);
        return true;
    }

    /// <summary>The note a clone's working copy keeps of the car it was made from.</summary>
    public const string ProvenanceFile = "illusion_clone.json";

    /// <summary>The model a cloned car's working copy says it was made from, or null.</summary>
    public static string? SourceOf(string folder)
    {
        string path = Path.Combine(folder, ProvenanceFile);
        if (!File.Exists(path)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("source", out System.Text.Json.JsonElement source) ? source.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // The root frame takes the new name (and with it the name table), the prefab entry and the entity-data
    // storage follow it under its new hash.
    private static List<string> RenameInside(string folder, string model, string name)
    {
        var notes = new List<string>();
        File.WriteAllText(Path.Combine(folder, ProvenanceFile),
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["source"] = model, ["model"] = name }));
        SdsManifest manifest = SdsManifest.Load(folder);
        string label = Path.GetFileName(folder);

        IReadOnlyList<string> frames = manifest.GetFiles("FrameResource");
        // Loaded with its name table, which is what marks the frames the rebuilt table has to list.
        if (frames.Count > 0 && ExtractedSds.Load(folder).FrameResource is { } frame)
        {
            FrameObjectBase? root = frame.FrameObjects.Values.OfType<FrameObjectBase>()
                .FirstOrDefault(f => string.Equals(f.Name.String, model, StringComparison.OrdinalIgnoreCase));
            int buffers = GiveOwnBuffers(manifest, frame, model, name);
            if (buffers == 0) notes.Add($"{label}: no buffer was renamed — the clone draws from the source car's buffers");

            if (root != null)
            {
                root.Name = new HashName(name);
                // The root is a Frame whose actor link names the car as well.
                if (root is FrameObjectFrame linked
                    && string.Equals(linked.ActorHash?.String, model, StringComparison.OrdinalIgnoreCase))
                {
                    linked.ActorHash = new HashName(name);
                }
            }
            else
            {
                notes.Add($"{label}: no frame is named {model} — the root keeps its name");
            }

            AtomicFile.WriteAllBytes(frames[0], frame.WriteToStream());
            IReadOnlyList<string> tables = manifest.GetFiles("FrameNameTable");
            if (tables.Count > 0)
            {
                var table = new FrameNameTable();
                table.BuildDataFromResource(frame);
                using var ms = new MemoryStream();
                using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) table.WriteToFile(writer);
                AtomicFile.WriteAllBytes(tables[0], ms.ToArray());
            }
        }

        foreach (string path in manifest.GetFiles("PREFAB"))
        {
            PrefabFile prefab = PrefabFile.Load(path);
            if (prefab.Rekey(Fnv64.Hash(model), Fnv64.Hash(name))) AtomicFile.WriteAllBytes(path, prefab.ToBytes());
            else notes.Add($"{label}: the prefab has no entry for {model}");
        }

        foreach (string path in manifest.GetFiles("EntityDataStorage"))
        {
            EntityDataStorageFile storage = EntityDataStorageFile.Load(path);
            if (storage.Hash != Fnv64.Hash(model.ToLowerInvariant()))
            {
                notes.Add($"{label}: the entity data is not filed under {model.ToLowerInvariant()}");
                continue;
            }
            storage.Hash = Fnv64.Hash(name.ToLowerInvariant());
            AtomicFile.WriteAllBytes(path, storage.ToBytes());
        }
        return notes;
    }

    // Renames every buffer the geometry draws from — in the geometry blocks and in the pool files, where a
    // buffer keeps its place. "Shubert_38.Root.L0.VB0" becomes "<name>.Root.L0.VB0"; a name that does not start
    // with the model's gets the new name in front.
    private static int GiveOwnBuffers(SdsManifest manifest, FrameResource frame, string model, string name)
    {
        var renamed = new Dictionary<ulong, HashName>();
        HashName Renamed(HashName old)
        {
            if (!renamed.TryGetValue(old.Hash, out HashName? fresh))
            {
                string text = old.String ?? "";
                string given = text.StartsWith(model, StringComparison.OrdinalIgnoreCase)
                    ? name + text[model.Length..]
                    : $"{name}.{(text.Length > 0 ? text : old.Hash.ToString("x16"))}";
                renamed[old.Hash] = fresh = new HashName(given);
            }
            return fresh;
        }

        foreach (FrameGeometry geometry in frame.FrameGeometries.Values)
        {
            foreach (FrameLOD lod in geometry.LOD ?? [])
            {
                lod.VertexBufferRef = Renamed(lod.VertexBufferRef);
                lod.IndexBufferRef = Renamed(lod.IndexBufferRef);
            }
        }

        foreach (string path in manifest.GetFiles("VertexBufferPool"))
        {
            var pool = new VertexBufferPool(new MemoryStream(File.ReadAllBytes(path), writable: false));
            var rewritten = new VertexBufferPool();
            foreach (VertexBuffer buffer in pool.Buffers.Values)
            {
                if (renamed.TryGetValue(buffer.Hash, out HashName? fresh)) buffer.Hash = fresh.Hash;
                rewritten.Buffers[buffer.Hash] = buffer;
            }
            using var stream = new MemoryStream();
            rewritten.WriteToFile(stream);
            AtomicFile.WriteAllBytes(path, stream.ToArray());
        }
        foreach (string path in manifest.GetFiles("IndexBufferPool"))
        {
            var pool = new IndexBufferPool(new MemoryStream(File.ReadAllBytes(path), writable: false));
            var rewritten = new IndexBufferPool();
            foreach (IndexBuffer buffer in pool.Buffers.Values)
            {
                if (renamed.TryGetValue(buffer.Hash, out HashName? fresh)) buffer.Hash = fresh.Hash;
                rewritten.Buffers[buffer.Hash] = buffer;
            }
            using var stream = new MemoryStream();
            rewritten.WriteToFile(stream);
            AtomicFile.WriteAllBytes(path, stream.ToArray());
        }
        return renamed.Count;
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
