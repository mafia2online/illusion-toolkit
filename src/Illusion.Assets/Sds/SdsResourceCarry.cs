using System.Globalization;
using System.Xml.Linq;
using Illusion.Domain;

namespace Illusion.Assets.Sds;

/// <summary>Reference-counted, undoable native Texture/MemFile inclusion for a compiled destination.
/// Files and opaque version-20 entry wrappers are persisted together by Flush, with the manifest last.</summary>
public sealed class SdsResourceCarry
{
    private sealed record Entry(string Type, ulong Hash, XElement Definition, XElement[] SourceTypes, byte[] Payload, string File);
    private readonly string _folder, _manifest;
    private readonly byte[] _original;
    private byte[] _saved;
    private readonly Dictionary<(string Type, ulong Hash), Entry> _entries = [];
    private readonly Dictionary<(string Type, ulong Hash), int> _references = [];
    private readonly HashSet<string> _writtenFiles = new(StringComparer.OrdinalIgnoreCase);
    private sealed record GenericBudget(string File, uint[] Values);
    private readonly List<GenericBudget> _genericBudgets = [];
    private static readonly string[] BudgetFields = ["SlotRam", "SlotVram", "OtherRam", "OtherVram"];
    public bool IsDirty { get; private set; }

    /// <summary>Restores flush bookkeeping when a containing multi-resource save restores its files.</summary>
    internal Action CaptureFlushCheckpoint()
    {
        byte[] saved = _saved;
        string[] written = _writtenFiles.ToArray(); bool dirty = IsDirty;
        return () => { _saved = saved; _writtenFiles.Clear(); _writtenFiles.UnionWith(written); IsDirty = dirty; };
    }

    public SdsResourceCarry(string destinationFolder)
    {
        _folder = Path.GetFullPath(destinationFolder); _manifest = Path.Combine(_folder, "SDSContent.xml");
        _original = _saved = File.ReadAllBytes(_manifest);
        if (Parse(_original).Attribute("ArchiveVersion")?.Value != "20") throw new NotSupportedException("Native resource inclusion requires SDS20.");
    }

    /// <summary>Stages source textures and memory files without writing either folder. Already applied on success.</summary>
    public IEditAction? AddFrom(string sourceFolder, out string? refusal)
    {
        refusal = null;
        try
        {
            var source = XElement.Load(Path.Combine(sourceFolder, "SDSContent.xml"));
            if (source.Attribute("ArchiveVersion")?.Value != "20") throw new NotSupportedException("Source resources must be SDS20.");
            var baseline = Parse(_original);
            var types = source.Element("ResourceTypes")?.Elements("ResourceType").Select(t => new XElement(t)).ToArray()
                ?? throw new InvalidDataException("Source native resource types are missing.");
            var keys = new HashSet<(string Type, ulong Hash)>();
            var additions = new Dictionary<(string Type, ulong Hash), Entry>();
            foreach (var node in source.Elements("ResourceEntry"))
            {
                string type = node.Element("Type")?.Value ?? throw new InvalidDataException("Missing native resource type.");
                if (type is not ("Texture" or "MemFile")) continue;
                ulong hash = ulong.Parse(node.Attribute("FileHash")!.Value, CultureInfo.InvariantCulture);
                var key = (type, hash);
                byte[] bytes = File.ReadAllBytes(Contained(sourceFolder, node.Element("File")!.Value));
                var existing = baseline.Elements("ResourceEntry").FirstOrDefault(e => e.Element("Type")?.Value == type && e.Attribute("FileHash")?.Value == node.Attribute("FileHash")!.Value);
                if (existing is not null)
                {
                    if (!Equal(existing, File.ReadAllBytes(Contained(_folder, existing.Element("File")!.Value)), node, bytes))
                        throw new NotSupportedException($"Destination {type} resource {hash:X16} has different native bytes or metadata.");
                    continue;
                }
                string file = $"Carried_{type}_{hash:X16}{Path.GetExtension(node.Element("File")!.Value)}";
                if (File.Exists(Contained(_folder, file)) && !_entries.ContainsKey(key))
                    throw new NotSupportedException("An unrelated file already uses the carried resource name: " + file);
                var definition = new XElement(node); definition.Element("File")!.Value = file;
                var entry = new Entry(type, hash, definition, types, bytes, file);
                if ((_entries.TryGetValue(key, out var held) || additions.TryGetValue(key, out held)) &&
                    !Equal(held.Definition, held.Payload, definition, bytes))
                    throw new NotSupportedException($"Carried {type} resource {hash:X16} conflicts with an earlier source.");
                if (!_entries.ContainsKey(key)) additions.TryAdd(key, entry);
                keys.Add(key);
            }
            // Validate the complete type merge and budgets before registering an applied action.
            var proposed = _entries.Concat(additions).Where(p => _references.GetValueOrDefault(p.Key) > 0 || keys.Contains(p.Key)).Select(p => p.Value).ToArray();
            _ = BuildManifest(proposed);
            foreach (var (key, value) in additions) _entries.Add(key, value);
            var edit = new CarryEdit(this, keys.ToArray()); edit.Redo(); return edit;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or NotSupportedException or OverflowException or FormatException)
        { refusal = ex.Message; return null; }
    }

    /// <summary>Reserves the shipped native budgets of copied GENR definitions in their destination entry.
    /// Each independent import reserves its own copy; shared texture/memory-file inclusion remains deduplicated.</summary>
    public IEditAction? AddGenericBudget(string destinationFile, IEnumerable<string> sourceFolders, out string? refusal)
    {
        refusal = null;
        try
        {
            _ = Contained(_folder, destinationFile);
            var target = Parse(_original).Elements("ResourceEntry").SingleOrDefault(e => e.Element("File")?.Value == destinationFile);
            if (target?.Element("Type")?.Value != "Generic") throw new ArgumentException("Select a destination native Generic resource file.");
            uint[] values = new uint[4];
            foreach (string folder in sourceFolders.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var root = XElement.Load(Path.Combine(folder, "SDSContent.xml"));
                if (root.Attribute("ArchiveVersion")?.Value != "20") throw new NotSupportedException("GENR budgets require SDS20 sources.");
                foreach (var entry in root.Elements("ResourceEntry").Where(e => e.Element("Type")?.Value == "Generic"))
                    for (int i = 0; i < values.Length; i++) values[i] = checked(values[i] + uint.Parse(entry.Attribute(BudgetFields[i])!.Value, CultureInfo.InvariantCulture));
            }
            var budget = new GenericBudget(destinationFile, values);
            _ = BuildManifest(_entries.Where(p => _references.GetValueOrDefault(p.Key) > 0).Select(p => p.Value), _genericBudgets.Append(budget));
            var action = new BudgetEdit(this, budget); action.Redo(); return action;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or NotSupportedException or OverflowException or FormatException)
        { refusal = ex.Message; return null; }
    }

    public void Flush()
    {
        if (!IsDirty) return;
        if (!File.ReadAllBytes(_manifest).AsSpan().SequenceEqual(_saved)) throw new InvalidOperationException("The destination resource manifest changed outside this import; reload before saving.");
        var active = _entries.Where(p => _references.GetValueOrDefault(p.Key) > 0).Select(p => p.Value).ToArray();
        byte[] manifest = active.Length == 0 && _genericBudgets.Count == 0 ? _original : BuildManifest(active);
        foreach (var entry in active)
        {
            string target = Contained(_folder, entry.File);
            if (File.Exists(target) && !File.ReadAllBytes(target).AsSpan().SequenceEqual(entry.Payload))
                throw new InvalidOperationException("A carried resource was modified independently: " + entry.File);
        }
        foreach (string file in _writtenFiles)
            if (File.Exists(Contained(_folder, file)) && !File.ReadAllBytes(Contained(_folder, file)).AsSpan().SequenceEqual(_entries.Values.Single(e => e.File == file).Payload))
                throw new InvalidOperationException("A carried resource was modified independently: " + file);
        foreach (var entry in active)
        {
            AtomicFile.WriteAllBytes(Contained(_folder, entry.File), entry.Payload);
            _writtenFiles.Add(entry.File);
        }
        AtomicFile.WriteAllBytes(_manifest, manifest); _saved = manifest;
        var keep = active.Select(e => e.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in _writtenFiles.Where(f => !keep.Contains(f)).ToArray())
        { File.Delete(Contained(_folder, file)); _writtenFiles.Remove(file); }
        IsDirty = false;
    }

    private byte[] BuildManifest(IEnumerable<Entry> entries, IEnumerable<GenericBudget>? genericBudgets = null)
    {
        var root = Parse(_original);
        var types = root.Element("ResourceTypes") ?? throw new InvalidDataException("Destination native resource types are missing.");
        int EnsureType(Entry entry, int sourceIndex, HashSet<int>? visited = null)
        {
            visited ??= [];
            if (!visited.Add(sourceIndex)) throw new NotSupportedException("Cyclic native resource parent table.");
            if ((uint)sourceIndex >= entry.SourceTypes.Length) throw new InvalidDataException("Source type index is out of range.");
            var source = entry.SourceTypes[sourceIndex]; string name = source.Attribute("Name")!.Value;
            var current = types.Elements("ResourceType").ToArray();
            int found = Array.FindIndex(current, t => t.Attribute("Name")?.Value == name);
            int parent = int.Parse(source.Attribute("Parent")!.Value, CultureInfo.InvariantCulture);
            if (parent != 0 && parent == sourceIndex) throw new NotSupportedException("Self-referencing native resource parent.");
            int targetParent = parent == 0 ? 0 : EnsureType(entry, parent, visited);
            if (found >= 0)
            {
                if (current[found].Attribute("Parent")?.Value != targetParent.ToString(CultureInfo.InvariantCulture))
                    throw new NotSupportedException("Destination native resource parent differs for " + name);
                return found;
            }
            int index = current.Length;
            types.Add(new XElement("ResourceType", new XAttribute("Id", index), new XAttribute("Name", name), new XAttribute("Parent", targetParent)));
            return index;
        }
        foreach (var entry in entries.OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Hash))
        {
            var node = new XElement(entry.Definition);
            node.SetAttributeValue("TypeId", EnsureType(entry, int.Parse(node.Attribute("TypeId")!.Value, CultureInfo.InvariantCulture)));
            foreach (string budget in BudgetFields)
                root.SetAttributeValue(budget, checked(uint.Parse(root.Attribute(budget)!.Value, CultureInfo.InvariantCulture) + uint.Parse(node.Attribute(budget)!.Value, CultureInfo.InvariantCulture)));
            root.Add(node);
        }
        foreach (var budget in genericBudgets ?? _genericBudgets)
        {
            var target = root.Elements("ResourceEntry").Single(e => e.Element("File")?.Value == budget.File);
            for (int i = 0; i < BudgetFields.Length; i++)
            {
                string field = BudgetFields[i];
                target.SetAttributeValue(field, checked(uint.Parse(target.Attribute(field)!.Value, CultureInfo.InvariantCulture) + budget.Values[i]));
                root.SetAttributeValue(field, checked(uint.Parse(root.Attribute(field)!.Value, CultureInfo.InvariantCulture) + budget.Values[i]));
            }
        }
        using var stream = new MemoryStream(); new XDocument(root).Save(stream); return stream.ToArray();
    }
    private static XElement Parse(byte[] bytes)
    { using var stream = new MemoryStream(bytes, writable: false); return XElement.Load(stream); }
    private static bool Equal(XElement a, byte[] left, XElement b, byte[] right)
    {
        if (!left.AsSpan().SequenceEqual(right)) return false;
        return new[] { "Prefix", "SlotRam", "SlotVram", "OtherRam", "OtherVram" }.All(name => a.Attribute(name)?.Value == b.Attribute(name)?.Value)
            && a.Element("Version")?.Value == b.Element("Version")?.Value;
    }
    private static string Contained(string folder, string file)
    {
        string boundary = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(folder, file));
        if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Carried resource path escapes its folder.");
        return path;
    }
    private sealed class CarryEdit(SdsResourceCarry owner, (string Type, ulong Hash)[] keys) : IEditAction
    {
        private bool _applied;
        public void Redo() { if (_applied) return; foreach (var key in keys) owner._references[key] = checked(owner._references.GetValueOrDefault(key) + 1); owner.IsDirty = true; _applied = true; }
        public void Undo() { if (!_applied) return; foreach (var key in keys) owner._references[key]--; owner.IsDirty = true; _applied = false; }
    }
    private sealed class BudgetEdit(SdsResourceCarry owner, GenericBudget budget) : IEditAction
    {
        private bool _applied;
        public void Redo() { if (_applied) return; owner._genericBudgets.Add(budget); owner.IsDirty = true; _applied = true; }
        public void Undo() { if (!_applied) return; owner._genericBudgets.Remove(budget); owner.IsDirty = true; _applied = false; }
    }
}
