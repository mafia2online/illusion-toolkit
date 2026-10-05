using System.Text.Json;

namespace Illusion.Assets.Sds;

/// <summary>
/// Which collision hulls a piece of scenery was given when it was carried into an archive — so the editor can
/// move and delete them WITH it.
/// <para>
/// Nothing in the archive ties a static object to its collision: the hull is a placement in the collision
/// resource that happens to stand where the object stands. For the objects this toolkit carried in, the tie is
/// written down here, beside the working copy and outside its manifest (the game never sees it): the object's
/// name and the hashes of the hulls it was given. The placements themselves are found again by hash, nearest
/// first — their positions change whenever the object is moved, their hulls do not — and each placement goes
/// to one object only: a copy stands on top of its original with the very same hull, and must not take it.
/// </para>
/// </summary>
public static class ImportLinks
{
    private const string FileName = "illusion_links.json";

    /// <summary>Records the hulls an object was given, replacing whatever an earlier object of that name had.</summary>
    public static void Set(string extractedDir, string frameName, IEnumerable<ulong> hulls)
    {
        ArgumentException.ThrowIfNullOrEmpty(extractedDir);
        ArgumentException.ThrowIfNullOrEmpty(frameName);
        ArgumentNullException.ThrowIfNull(hulls);
        Dictionary<string, List<ulong>> links = Read(extractedDir);
        List<ulong> list = [.. hulls.Distinct()];
        if (list.Count == 0) links.Remove(frameName);
        else links[frameName] = list;
        Write(extractedDir, links);
    }

    /// <summary>Moves an object's record to its new name. An object is found by its name here, so one that is
    /// renamed without this leaves its hulls behind the next time it is moved or deleted.</summary>
    public static void Rename(string extractedDir, string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrEmpty(extractedDir);
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName) return;
        Dictionary<string, List<ulong>> links = Read(extractedDir);
        if (!links.Remove(oldName, out List<ulong>? hulls)) return;
        links[newName] = hulls;
        Write(extractedDir, links);
    }

    private static void Write(string extractedDir, Dictionary<string, List<ulong>> links)
    {
        string path = Path.Combine(extractedDir, FileName);
        try
        {
            if (links.Count == 0) File.Delete(path);
            else AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(links));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unrecorded link costs moving the hull by hand, nothing more
        }
        lock (Cache) Cache.Remove(path);
    }

    // The file as last read, by path, with the time stamp it had: every drag start, delete and duplicate asks
    // about every node it touches, and parsing the file for each of them is what made a large selection stall.
    private static readonly Dictionary<string, (DateTime Stamp, Dictionary<string, List<ulong>> Links)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The hulls recorded for an object of this archive, or none.</summary>
    public static IReadOnlyList<ulong> HullsOf(string extractedDir, string frameName)
    {
        ArgumentException.ThrowIfNullOrEmpty(extractedDir);
        return frameName is { Length: > 0 } && Read(extractedDir).TryGetValue(frameName, out List<ulong>? hulls) ? hulls : [];
    }

    /// <summary>Every object of the archive that has hulls recorded, with them.</summary>
    public static IReadOnlyDictionary<string, List<ulong>> All(string extractedDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(extractedDir);
        return Read(extractedDir);
    }

    private static Dictionary<string, List<ulong>> Read(string extractedDir)
    {
        string path = Path.Combine(extractedDir, FileName);
        try
        {
            if (!File.Exists(path)) return new();
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var kept) && kept.Stamp == stamp) return Copy(kept.Links);
            }
            Dictionary<string, List<ulong>> links =
                JsonSerializer.Deserialize<Dictionary<string, List<ulong>>>(File.ReadAllText(path)) ?? new();
            lock (Cache) Cache[path] = (stamp, links);
            return Copy(links);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    // Callers change what they are handed (Set, Rename); the kept one stays as the file is.
    private static Dictionary<string, List<ulong>> Copy(Dictionary<string, List<ulong>> links) =>
        links.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
}
