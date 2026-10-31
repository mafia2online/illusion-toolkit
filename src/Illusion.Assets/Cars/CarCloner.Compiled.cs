using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Cars;

public static partial class CarCloner
{
    private static CarCloneOutcome? CloneCompiled(string source, string name, bool traffic, string? title, out string? refusal)
    {
        refusal = null;
        if (title is not null && (string.IsNullOrWhiteSpace(title) || title.Contains('\0')))
        { refusal = "The custom vehicle title must be nonempty and contain no null characters."; return null; }
        string stem = Path.GetFileNameWithoutExtension(source).ToLowerInvariant();
        name = name.ToLowerInvariant();
        var original = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "cars", stem + ".sds"));
        var target = new FileInfo(Path.Combine(original.DirectoryName!, name + ".sds"));
        var tables = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "tables", "tables.sds"));
        string destination = MafiaEnvironment.ExtractedDir(target);
        if (!original.Exists || !tables.Exists) { refusal = "The source car and tables.sds must exist."; return null; }
        if (target.Exists || Directory.Exists(destination)) { refusal = "The target archive or working copy already exists."; return null; }
        string sourceFolder = SdsMeshLoader.EnsureExtracted(original), tableFolder = SdsMeshLoader.EnsureExtracted(tables);
        var tableFiles = SdsManifest.Load(tableFolder).GetFiles("SystemObjectDatabase");
        var registrations = tableFiles.Select(p => (Path: p, Table: CompiledVehicleRegistration.TryRead(File.ReadAllBytes(p))))
            .Where(t => t.Table is not null).ToArray();
        var vehicles = registrations.FirstOrDefault(t => BinaryPrimitives.ReadUInt64LittleEndian(File.ReadAllBytes(t.Path).AsSpan(12)) == 0xa869f8a3ac7656e1);
        var paint = registrations.FirstOrDefault(t => BinaryPrimitives.ReadUInt64LittleEndian(File.ReadAllBytes(t.Path).AsSpan(12)) == 0xdc3279441da347e2);
        if (vehicles.Table is null || paint.Table is null) { refusal = "The PC vehicle and paint registration tables are missing."; return null; }
        var vehicleTables = new List<CompiledVehicleRegistration> { vehicles.Table };
        foreach (string path in Directory.EnumerateFiles(tables.DirectoryName!, "tables-*.sds"))
            foreach (string file in SdsManifest.Load(SdsMeshLoader.EnsureExtracted(new FileInfo(path))).GetFiles("SystemObjectDatabase"))
            {
                byte[] data = File.ReadAllBytes(file);
                if (data.Length >= 20 && BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(12)) == 0xa869f8a3ac7656e1
                    && CompiledVehicleRegistration.TryRead(data) is { } dedicated) vehicleTables.Add(dedicated);
            }
        var sourceTable = vehicles.Table;
        int sourceRow = sourceTable.Find(stem);
        if (sourceRow < 0)
        {
            foreach (var candidate in vehicleTables)
                if (candidate.Find(stem) is int row && row >= 0) { sourceTable = candidate; sourceRow = row; break; }
        }
        int paintRow = paint.Table.Find(stem);
        if (sourceRow < 0 || paintRow < 0) { refusal = "The source car has no verified vehicle/paint registration row."; return null; }
        if (vehicleTables.Any(t => t.Find(name) >= 0) || paint.Table.Find(name) >= 0) { refusal = "The new vehicle name is already registered."; return null; }
        uint id = checked(1 + vehicleTables.SelectMany(t => Enumerable.Range(0, t.Count).Select(t.Id))
            .Concat(Enumerable.Range(0, paint.Table.Count).Select(paint.Table.Id)).Append(sourceTable.Id(sourceRow)).Max());
        vehicles.Table.Append(sourceTable, sourceRow, id, name);
        if (!traffic) vehicles.Table.ExcludeFromTraffic(vehicles.Table.Find(name));
        paint.Table.Append(paint.Table, paintRow, id, name);
        var localized = new List<(FileInfo Archive, string Path, CompiledStringTable Table)>();
        string? titleKey = null;
        if (title is not null)
        {
            titleKey = "itk_car_" + Fnv64.Hash(name, Encoding.UTF8).ToString("x16", CultureInfo.InvariantCulture);
            ulong textId = Fnv64.Hash(titleKey, Encoding.UTF8), sourceTextId = sourceTable.DisplayTextId(sourceRow);
            string languages = Path.Combine(MafiaEnvironment.ArchiveFolder, "string_tables");
            if (Directory.Exists(languages))
                foreach (string path in Directory.EnumerateFiles(languages, "stringtable_*.sds"))
                {
                    var language = new FileInfo(path);
                    var candidates = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(language)).GetFiles("SystemObjectDatabase")
                        .Select(p => (Path: p, Table: CompiledStringTable.TryRead(File.ReadAllBytes(p))))
                        .Where(t => t.Table is not null && t.Table.Find(sourceTextId) >= 0).ToArray();
                    if (candidates.Length != 1) { refusal = "The source vehicle title is not uniquely present in " + language.Name; return null; }
                    if (candidates[0].Table!.Find(textId) >= 0) { refusal = "The custom localization key already exists in " + language.Name; return null; }
                    candidates[0].Table!.Add(textId, title, sourceTextId);
                    localized.Add((language, candidates[0].Path, candidates[0].Table!));
                }
            if (localized.Count == 0) { refusal = "No installed Mafia III language archive is available for the custom title."; return null; }
            vehicles.Table.SetDisplayName(vehicles.Table.Find(name), textId, titleKey);
        }
        var journal = new GameWriteJournal();
        try
        {
            journal.WillCreateFolder(destination); CopyTree(sourceFolder, destination);
            var manifestPath = Path.Combine(destination, "SDSContent.xml");
            var manifest = XDocument.Load(manifestPath);
            var generics = manifest.Root!.Elements("ResourceEntry").Where(e => (string?)e.Element("Type") == "Generic").ToArray();
            var resources = generics.Select(e => CompiledResource.Load(Path.Combine(destination, e.Element("File")!.Value))).ToArray();
            var graph = new CompiledGraph(resources);
            var ids = new Dictionary<ulong, ulong>();
            var used = new HashSet<ulong>(graph.Objects.Keys);
            ulong Fresh()
            {
                ulong value;
                do value = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)); while (value == 0 || !used.Add(value));
                return value;
            }
            foreach (ulong old in graph.Objects.Keys) ids[old] = Fresh();
            foreach (var node in graph.Nodes)
                foreach (string fieldName in new[] { "m_HashId", "mHashId", "m_ID" })
                    if (graph.Field(node, fieldName) is { DataSize: 8 } field && graph.Integer(field) is ulong old && old != 0)
                        ids.TryAdd(old, Fresh());
            foreach (var entry in generics)
                if (ulong.TryParse((string?)entry.Attribute("FileHash"), CultureInfo.InvariantCulture, out ulong hash) && hash != 0)
                    ids.TryAdd(hash, Fresh());
            ulong oldName = Fnv64.Hash(stem, Encoding.UTF8), newName = Fnv64.Hash(name, Encoding.UTF8);
            ids[oldName] = newName;
            for (int i = 0; i < resources.Length; i++)
            {
                var resource = resources[i].RemapIds(ids);
                var local = new CompiledGraph([resource]);
                foreach (var element in resource.Elements.Where(e => e.Event == 4 && e.TypeId == 0x153e26b2))
                    if (local.Text(element).Equals(stem, StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] prior = local.Data(element).ToArray();
                        int terminators = prior.Length - prior.AsSpan().TrimEnd((byte)0).Length;
                        byte[] replacement = new byte[Encoding.UTF8.GetByteCount(name) + terminators];
                        Encoding.UTF8.GetBytes(name).CopyTo(replacement, 0); resource.ReplaceVariable(element, replacement);
                    }
                AtomicFile.WriteAllBytes(resource.Path, resource.ToArray());
                if (ulong.TryParse((string?)generics[i].Attribute("FileHash"), CultureInfo.InvariantCulture, out ulong oldHash))
                    generics[i].SetAttributeValue("FileHash", ids.GetValueOrDefault(oldHash, oldHash).ToString(CultureInfo.InvariantCulture));
            }
            manifest.Save(manifestPath);
            // Validate the independent graph before publishing registrations or archives.
            var check = new CompiledSceneDocument(target);
            if (check.Roots.Count == 0 || !check.Roots.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The cloned vehicle root cannot be resolved under its new name.");
            journal.Remember(vehicles.Path); journal.Remember(paint.Path);
            foreach (var language in localized) journal.Remember(language.Path);
            AtomicFile.WriteAllBytes(vehicles.Path, vehicles.Table.ToBytes());
            AtomicFile.WriteAllBytes(paint.Path, paint.Table.ToBytes());
            foreach (var language in localized) AtomicFile.WriteAllBytes(language.Path, language.Table.ToBytes());
            journal.WillCreateFile(target.FullName);
            var packedCar = SdsWriter.PackSds(target, false);
            var packedTables = SdsWriter.PackSds(tables, true);
            journal.Replaced(packedTables.Archive, packedTables.Backup);
            if (packedCar.Dropped.Count != 0 || packedTables.Dropped.Count != 0) throw new InvalidDataException("Archive build dropped manifest resources.");
            var packed = new List<(string Archive, string? Backup)> { (packedCar.Archive, packedCar.Backup), (packedTables.Archive, packedTables.Backup) };
            foreach (var language in localized)
            {
                var made = SdsWriter.PackSds(language.Archive, true); journal.Replaced(made.Archive, made.Backup);
                if (made.Dropped.Count != 0) throw new InvalidDataException("Language archive build dropped manifest resources.");
                packed.Add((made.Archive, made.Backup));
            }
            journal.Commit();
            return new CarCloneOutcome(name, checked((int)id), packed, 0, null,
                ["Mafia III vehicle/paint registration added; geometry, rig, components and buffer-cache identities are independent.",
                 "Textures, materials, audio and tuning presets remain shared with the source until explicitly edited.",
                 traffic ? "Native traffic common flags are copied from the source; Mafia III's vehicle traffic pre-cache reads the registration table directly, with no CARM rows."
                     : "Native traffic common flags are zero, so the vehicle traffic pre-cache excludes this clone.",
                 titleKey is null ? "The source vehicle's localized title is shared."
                     : $"Custom title registered in {localized.Count} installed languages under native key {titleKey} (0x{Fnv64.Hash(titleKey, Encoding.UTF8):X16}); Mafia III uses64-bit localization keys, so the legacy TextId field is null."]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { refusal = TakenBack("the Mafia III clone", ex, journal.Undo()); return null; }
    }
}
