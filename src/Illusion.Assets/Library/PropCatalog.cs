using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Illusion.Assets.Actors;
using Illusion.Assets.Frames;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Assets.Library;

/// <summary>
/// The prop library: what the stock game has that can be carried into a district — doors, chairs, tables,
/// plants, lamps — found in the archives' working copies and filed on shelves.
/// <para>
/// Two kinds of thing qualify. An actor that places an object of its own archive (a door, a breakable prop, a
/// framed piece of furniture) is a prop by definition, whatever it is called. Plain scenery is not — an
/// interior's walls are scenery too — so a frame object qualifies only when its name says what it is (the
/// shipped names are Czech and English: <c>zidle</c>, <c>stul</c>, <c>kytka</c>, <c>lamp</c>) and it is the
/// size of a piece of furniture. Each archive offers a thing once: one entry per actor definition, one per
/// scenery name without its counter.
/// </para>
/// <para>
/// Scanning reads every scene, so it is slow the first time and remembered after: the result is kept beside
/// the toolkit's settings, per archive, and an archive whose working copy has not changed is not read again.
/// Only archives already extracted are read — the library is an index of the working copies, never a reason
/// to extract the whole game.
/// </para>
/// </summary>
public static class PropCatalog
{
    /// <summary>The shelves, in the order the library lists them, each with the name fragments that file a
    /// thing there. "Other" takes the actors nothing else claimed.</summary>
    public static readonly IReadOnlyList<(string Name, Regex Pattern)> Categories =
    [
        ("Doors", Rx(@"dver|door|vrata|brana|gate")),
        ("Seating", Rx(@"zidl|chair|kresl|sedac|sedad|lavic|bench|gauc|sofa|stolick|stool|taburet")),
        ("Tables", Rx(@"stul|stol(?!ick)|table|desk|pult")),
        ("Beds", Rx(@"postel|\bbed(?!n|ýn)|lehatk|matrac")),
        ("Storage", Rx(@"skrin|regal|polic|shelf|cabinet|bedn|crate|box|krabic|sud\b|barrel|truhl|komod")),
        ("Plants", Rx(@"kytk|kvet|kvit|palm|plant|flower|rostl")),
        ("Lights", Rx(@"lamp|svetl|lustr|light|svicn")),
        ("Decor", Rx(@"hodin|clock|obraz|picture|vesak|zrcad|mirror|telef|phone|radio|vaz[ay]|vase|kos\b|kosik|basket|odpad|trash|popeln")),
    ];

    /// <summary>The shelf for an entry's name: the first category whose fragments it carries, or "Other".</summary>
    public static string CategoryOf(params string[] names)
    {
        foreach ((string name, Regex pattern) in Categories)
        {
            if (names.Any(n => n.Length > 0 && pattern.IsMatch(n))) return name;
        }
        return "Other";
    }

    // Scenery larger than this is architecture, not a prop; actors larger than this are gates and lifts.
    private const float MaxSceneryExtent = 5f;
    private const float MaxActorExtent = 8f;
    private const int MaxTriangles = 20000;

    // The actor types that place a movable or usable object of their archive's scene.
    private static readonly HashSet<EntityType> PropActors = [EntityType.Door, EntityType.CrashObject, EntityType.FrameWrapper];

    // Bump to throw away every cached scan: the entry shape or the rules changed.
    private const int CacheVersion = 3;

    /// <summary>Where the scan is remembered.</summary>
    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Illusion", "props.json");

    /// <summary>The folders under <c>pc\sds</c> the library reads: the stock interiors and the city.</summary>
    private static readonly string[] Folders = ["shops", "city"];

    /// <summary>
    /// Every prop of every extracted archive, scanning what changed since the last call. Safe to run off the
    /// UI thread — it reads files and nothing the editor holds. <paramref name="progress"/> gets the archive
    /// being read, its number and the total.
    /// </summary>
    public static IReadOnlyList<PropEntry> Load(Action<string, int, int>? progress = null, CancellationToken cancel = default)
    {
        Dictionary<string, CachedArchive> cache = ReadCache();
        var archives = new List<FileInfo>();
        foreach (string folder in Folders)
        {
            string dir = Path.Combine(MafiaEnvironment.PcFolder, "sds", folder);
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.GetFiles(dir, "*.sds"))
            {
                // A winter archive is its summer twin with snow on it — the same props, twice.
                if (Path.GetFileNameWithoutExtension(file).EndsWith("_z", StringComparison.OrdinalIgnoreCase)) continue;
                archives.Add(new FileInfo(file));
            }
        }

        var entries = new List<PropEntry>();
        var fresh = new Dictionary<string, CachedArchive>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < archives.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            FileInfo sds = archives[i];
            string relative = Path.GetRelativePath(Path.Combine(MafiaEnvironment.PcFolder, "sds"), sds.FullName);
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            string manifest = Path.Combine(extracted, "SDSContent.xml");
            if (!File.Exists(manifest)) continue;

            long stamp = StampOf(extracted);
            if (cache.TryGetValue(relative, out CachedArchive? known) && known.Stamp == stamp)
            {
                fresh[relative] = known;
                entries.AddRange(known.Entries);
                continue;
            }

            progress?.Invoke(relative, i + 1, archives.Count);
            List<PropEntry> found;
            try
            {
                found = Scan(relative, extracted);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                           or Formats.SdsFormatException or ArgumentException or IndexOutOfRangeException)
            {
                found = []; // an archive the format layer cannot read offers nothing, and the rest still load
            }
            fresh[relative] = new CachedArchive(stamp, found);
            entries.AddRange(found);
        }

        WriteCache(fresh);

        // One card per object. A prop turns up in several archives — the same phone in every shop, and in a
        // district once it has been carried there — and those are one thing to choose, not many. Interiors are
        // read before the city, so the card that stays points at the stock archive the object came from.
        var seen = new HashSet<(string, string, int)>();
        entries.RemoveAll(e => !seen.Add((e.Kind, e.Label.ToLowerInvariant(), e.Triangles)));

        // Shelf by shelf, and on each shelf the things that DO something first — a door that opens before a
        // door that is only a picture of one.
        entries.Sort((a, b) =>
        {
            int c = string.Compare(a.Category, b.Category, StringComparison.Ordinal);
            if (c != 0) return c;
            c = (a.Kind == "Scenery").CompareTo(b.Kind == "Scenery");
            return c != 0 ? c : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        });
        return entries;
    }

    /// <summary>The props one extracted archive offers.</summary>
    public static List<PropEntry> Scan(string relative, string extracted)
    {
        ExtractedSds sds = ExtractedSds.Load(extracted);
        if (sds.FrameResource is not { } scene) return [];
        ActorPlacements placements = ActorPlacements.Load(sds.Manifest, scene);

        var entries = new List<PropEntry>();
        var definitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ActorEntry actor in placements.All)
        {
            if (!actor.IsTyped || !PropActors.Contains(actor.Type)) continue;
            if (placements.TargetOf(actor) is not { } target || !FrameTransplant.CanTransplant(target, out _)) continue;
            string definition = actor.LinkedDefinition.Length > 0 ? actor.LinkedDefinition : Stem(actor.EntityName);
            if (!definitions.Add(definition)) continue;
            if (Measure(target) is not { } measured || measured.Extent > MaxActorExtent) continue;
            string category = actor.Type == EntityType.Door ? "Doors" : CategoryOf(definition, actor.EntityName, target.Name.String);
            entries.Add(new PropEntry(relative, actor.EntityName, definition, actor.Type.ToString(), category,
                measured.Triangles, measured.Size));
        }

        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object value in scene.FrameObjects.Values)
        {
            if (value is not FrameObjectSingleMesh mesh || mesh.GetType() != typeof(FrameObjectSingleMesh)) continue;
            if (placements.ActorCovering(mesh) != null || !mesh.Refs.ContainsKey(FrameEntryRefTypes.Geometry)) continue;
            string name = mesh.Name.String;
            if (name.Length == 0 || name.StartsWith("proxy", StringComparison.OrdinalIgnoreCase)) continue;
            string category = CategoryOf(name);
            if (category == "Other") continue;
            string stem = Stem(name);
            if (!stems.Add(stem)) continue;
            if (!FrameTransplant.CanTransplant(mesh, out _)) continue;
            if (Measure(mesh) is not { } measured || measured.Extent > MaxSceneryExtent || measured.Extent < 0.05f) continue;
            entries.Add(new PropEntry(relative, name, stem, "Scenery", category, measured.Triangles, measured.Size));
        }
        return entries;
    }

    private static (float Extent, int Triangles, float[] Size)? Measure(FrameObjectBase root)
    {
        IReadOnlyList<(Vector3[] Positions, uint[] Indices)> meshes = FrameTransplant.TrianglesOf(root);
        int triangles = meshes.Sum(m => m.Indices.Length / 3);
        if (triangles == 0 || triangles > MaxTriangles) return null;
        if (FrameTransplant.BoundsOf(root) is not { } bounds) return null;
        Vector3 size = bounds.Max - bounds.Min;
        return (MathF.Max(size.X, MathF.Max(size.Y, size.Z)), triangles, [size.X, size.Y, size.Z]);
    }

    // "Deli_zidle07" → "Deli_zidle", "09_teren_kvetinac_inst04_G" → "09_teren_kvetinac": the shipped names count
    // copies off at the end, sometimes as "inst" + a number, sometimes followed by a group letter.
    private static string Stem(string name)
    {
        string stem = Counter.Replace(name, "");
        return stem.Length > 0 ? stem : name;
    }

    private static readonly Regex Counter = new(@"(?:[_ ]?inst)?[_ .]*\d*(?:_[A-Z])?[_ .]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // What changes when a working copy does: its manifest and its scene.
    private static long StampOf(string extracted)
    {
        long stamp = File.GetLastWriteTimeUtc(Path.Combine(extracted, "SDSContent.xml")).Ticks;
        foreach (string fr in Directory.GetFiles(extracted, "FrameResource_*.fr"))
        {
            stamp = Math.Max(stamp, File.GetLastWriteTimeUtc(fr).Ticks);
        }
        return stamp;
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private sealed record CachedArchive(long Stamp, List<PropEntry> Entries);

    private sealed record CacheFile(int Version, Dictionary<string, CachedArchive> Archives);

    private static Dictionary<string, CachedArchive> ReadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return new(StringComparer.OrdinalIgnoreCase);
            CacheFile? file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));
            return file is { Version: CacheVersion }
                ? new Dictionary<string, CachedArchive>(file.Archives, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(StringComparer.OrdinalIgnoreCase); // a cache that cannot be read is a cache that is not there
        }
    }

    private static void WriteCache(Dictionary<string, CachedArchive> archives)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            AtomicFile.WriteAllBytes(CachePath, JsonSerializer.SerializeToUtf8Bytes(new CacheFile(CacheVersion, archives)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not remembering the scan costs the next start a rescan, nothing more
        }
    }
}
