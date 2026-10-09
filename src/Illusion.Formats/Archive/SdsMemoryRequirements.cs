using System.Text.Json;
using System.Xml.XPath;

namespace Illusion.Formats.Archive;

/// <summary>
/// The memory each resource of an archive asks for, as the archive that shipped states it.
///
/// <para>
/// An entry carries four figures the engine budgets a slot by, and they are NOT the payload size: a shipped
/// car's PREFAB is 23 851 bytes and asks for 41 030, its FrameResource 73 711 and asks for 77 812 plus 853 of
/// "other" RAM. Extraction does not keep them and a packing handler can only state the payload size, so a
/// rebuilt archive under-declares itself — a car came out asking for 148 100 bytes of slot RAM where the
/// original asked for 170 232. These are read back from the archive that shipped and kept beside the working
/// copy (<see cref="FileName"/>), so a rebuild states what the original did.
/// </para>
/// <para>
/// A resource is found by its type and the file name its manifest entry gives it; a container entry, which
/// names no file, by its place among the entries of its type.
/// </para>
/// </summary>
public sealed class SdsMemoryRequirements
{
    /// <summary>The file beside SDSContent.xml the requirements are kept in.</summary>
    public const string FileName = "illusion_memory.json";

    private readonly Dictionary<string, SdsMemoryRequirement> _byKey;

    private SdsMemoryRequirements(Dictionary<string, SdsMemoryRequirement> byKey) => _byKey = byKey;

    public int Count => _byKey.Count;

    /// <summary>
    /// Whether these read like an archive the game shipped rather than one a packer made: a packer states
    /// payload sizes and no "other" RAM at all, so figures like that are not worth keeping.
    /// </summary>
    public bool LooksShipped => _byKey.Values.Any(r => r.OtherRam > 0 || r.SlotRam > (uint)r.DataSize + 256);

    public bool TryGet(string key, out SdsMemoryRequirement requirement) => _byKey.TryGetValue(key, out requirement!);

    /// <summary>States an entry's figures - how a resource brought in from another working copy keeps what it
    /// stated there instead of falling back to the packer's own.</summary>
    public void Set(string key, SdsMemoryRequirement requirement)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(requirement);
        _byKey[key] = requirement;
    }

    /// <summary>The key of a manifest entry: its type and file, or its type and ordinal when it names no file.</summary>
    public static string Key(string type, string? file, int ordinal) =>
        file != null ? $"{type}|{file}" : $"{type}#{ordinal}";

    /// <summary>The keys of a folder's manifest entries, in manifest order.</summary>
    public static List<string> ManifestKeys(string folder)
    {
        var keys = new List<string>();
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        using FileStream stream = File.OpenRead(Path.Combine(folder, "SDSContent.xml"));
        XPathNodeIterator entries = new XPathDocument(stream).CreateNavigator().Select("/SDSResource/ResourceEntry");
        while (entries.MoveNext())
        {
            keys.Add(KeyOf(entries.Current!, ordinals));
        }
        return keys;
    }

    /// <summary>The key of one manifest entry; <paramref name="ordinals"/> counts the entries of each type seen so far.</summary>
    public static string KeyOf(XPathNavigator entry, Dictionary<string, int> ordinals)
    {
        string type = "";
        string? file = null;
        XPathNodeIterator children = entry.SelectChildren(XPathNodeType.Element);
        while (children.MoveNext())
        {
            if (children.Current!.Name == "Type") type = children.Current.Value;
            else if (children.Current.Name == "File" && file == null) file = children.Current.Value;
        }
        int ordinal = ordinals.GetValueOrDefault(type);
        ordinals[type] = ordinal + 1;
        return Key(type, file, ordinal);
    }

    /// <summary>
    /// Reads what an archive states for each of its resources. The archive is extracted to a scratch folder to
    /// learn the file names its entries get — the names a working copy's manifest uses.
    /// </summary>
    public static SdsMemoryRequirements FromArchive(string archivePath)
    {
        SdsArchive archive = SdsArchive.Open(archivePath);
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_memory_" + Guid.NewGuid().ToString("N"));
        try
        {
            return Extract(archive, scratch);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* scratch left behind */ }
        }
    }

    /// <summary>
    /// Extracts <paramref name="archive"/> into <paramref name="targetFolder"/> and returns what it stated for
    /// each resource, keyed by the manifest the extraction wrote. The payload sizes are taken BEFORE the
    /// extraction: unwrapping a texture leaves the entry holding the bare image, ten bytes short of the payload
    /// the figures were stated for.
    /// </summary>
    public static SdsMemoryRequirements Extract(SdsArchive archive, string targetFolder)
    {
        ArgumentNullException.ThrowIfNull(archive);
        // An entry of a type the reader does not know is not extracted, and so has no manifest entry.
        var stated = archive.Entries.Where(e => e.TypeId != -1)
            .Select(e => new SdsMemoryRequirement(e.SlotRamRequired, e.SlotVramRequired, e.OtherRamRequired, e.OtherVramRequired, e.Data?.Length ?? 0))
            .ToList();
        archive.Extract(targetFolder);
        List<string> keys = ManifestKeys(targetFolder);
        if (keys.Count != stated.Count)
        {
            throw new SdsFormatException(
                $"the archive extracts to {keys.Count} manifest entries for its {stated.Count} resources");
        }
        var byKey = new Dictionary<string, SdsMemoryRequirement>(StringComparer.Ordinal);
        for (int i = 0; i < keys.Count; i++) byKey[keys[i]] = stated[i];
        return new SdsMemoryRequirements(byKey);
    }

    /// <summary>The requirements kept beside a working copy, or null when it has none.</summary>
    public static SdsMemoryRequirements? Load(string folder)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, uint[]>>(File.ReadAllText(path));
            if (raw == null) return null;
            var byKey = new Dictionary<string, SdsMemoryRequirement>(StringComparer.Ordinal);
            foreach ((string key, uint[] v) in raw)
            {
                if (v.Length == 5) byKey[key] = new SdsMemoryRequirement(v[0], v[1], v[2], v[3], (int)v[4]);
            }
            return new SdsMemoryRequirements(byKey);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string folder)
    {
        var raw = _byKey.ToDictionary(
            p => p.Key,
            p => new[] { p.Value.SlotRam, p.Value.SlotVram, p.Value.OtherRam, p.Value.OtherVram, (uint)p.Value.DataSize });
        string path = Path.Combine(folder, FileName);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// States for a freshly packed entry what the shipped one stated. With the payload the size it was, the
    /// figures are taken as they are; with a payload that changed size they scale with it, and never fall
    /// under what the packing handler measured from the new payload itself.
    /// </summary>
    public void Apply(string key, ResourceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!_byKey.TryGetValue(key, out SdsMemoryRequirement? stated)) return;
        int size = entry.Data?.Length ?? 0;
        if (size == stated.DataSize)
        {
            entry.SlotRamRequired = stated.SlotRam;
            entry.SlotVramRequired = stated.SlotVram;
            entry.OtherRamRequired = stated.OtherRam;
            entry.OtherVramRequired = stated.OtherVram;
            return;
        }
        double scale = stated.DataSize > 0 ? (double)size / stated.DataSize : 1.0;
        entry.SlotRamRequired = Math.Max(entry.SlotRamRequired, Scaled(stated.SlotRam, scale));
        entry.OtherRamRequired = Math.Max(entry.OtherRamRequired, Scaled(stated.OtherRam, scale));
        entry.SlotVramRequired = Math.Max(entry.SlotVramRequired, Scaled(stated.SlotVram, scale));
        entry.OtherVramRequired = Math.Max(entry.OtherVramRequired, Scaled(stated.OtherVram, scale));
    }

    private static uint Scaled(uint value, double scale) => (uint)Math.Ceiling(value * scale);
}
