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
        try
        {
            AtomicFile.WriteAllBytes(Path.Combine(extractedDir, FileName), JsonSerializer.SerializeToUtf8Bytes(links));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unrecorded link costs moving the hull by hand, nothing more
        }
    }

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
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, List<ulong>>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }
}
