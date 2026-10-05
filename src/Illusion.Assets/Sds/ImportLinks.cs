using System.Numerics;
using System.Text.Json;

namespace Illusion.Assets.Sds;

/// <summary>
/// Which collision hulls a piece of scenery was given when it was carried into an archive — so the editor can
/// move and delete them WITH it.
/// <para>
/// Nothing in the archive ties a static object to its collision: the hull is a placement in the collision
/// resource that happens to stand where the object stands. For the objects this toolkit carried in, the tie is
/// written down here, beside the working copy and outside its manifest (the game never sees it): the object's
/// name and, for each placement it was given, the hull it places and where that placement stood IN THE
/// OBJECT'S OWN SPACE when the link was made.
/// </para>
/// <para>
/// The place is what identifies a placement. A hull hash names a shape, not a placement — one object can be
/// given the same hull twice — and it changes when a scale re-cooks the hull; a distance from the object's
/// pivot says little, since a placement's origin can be metres from the pivot of what it belongs to. The
/// place in the object's space stays what it was through every move, turn and resize the two make together,
/// so the placement is found again where the object's matrix now puts that point. A record written before
/// places were kept has only the hash, and is matched by nearness to the object as it used to be.
/// </para>
/// </summary>
public static class ImportLinks
{
    private const string FileName = "illusion_links.json";

    /// <summary>One placement an object was given: the hull it places, and where it stood in the object's own
    /// space when the link was made — null in a record older than that.</summary>
    public readonly record struct Link(ulong Hull, Vector3? At);

    /// <summary>Records the placements an object was given, replacing whatever an earlier object of that name
    /// had. One entry per placement: the same hull given twice is two entries.</summary>
    public static void Set(string extractedDir, string frameName, IEnumerable<Link> hulls)
    {
        ArgumentException.ThrowIfNullOrEmpty(extractedDir);
        ArgumentException.ThrowIfNullOrEmpty(frameName);
        ArgumentNullException.ThrowIfNull(hulls);
        Dictionary<string, List<Link>> links = Read(extractedDir);
        List<Link> list = [.. hulls];
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
        Dictionary<string, List<Link>> links = Read(extractedDir);
        // Not over another object's record. Renamed onto a name that has one, this object gives up its own
        // tie (two objects of one name cannot both be found by it) — but the other's stays, and so does this
        // one's entry under the old name, which is what the undo of the rename finds and leaves alone. Moved
        // over it, the other object's record was gone, and the undo then took what was left away from it too.
        if (links.ContainsKey(newName) || !links.Remove(oldName, out List<Link>? hulls)) return;
        links[newName] = hulls;
        Write(extractedDir, links);
    }

    /// <summary>The placements recorded for an object of this archive, or none.</summary>
    public static IReadOnlyList<Link> HullsOf(string extractedDir, string frameName)
    {
        ArgumentException.ThrowIfNullOrEmpty(extractedDir);
        return frameName is { Length: > 0 } && Read(extractedDir).TryGetValue(frameName, out List<Link>? hulls) ? hulls : [];
    }

    /// <summary>Every object of the archive that has placements recorded, with them.</summary>
    public static IReadOnlyDictionary<string, List<Link>> All(string extractedDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(extractedDir);
        return Read(extractedDir);
    }

    // On disk: name → a list whose items are either a bare number (a hull hash — the form every record had
    // before places were kept, still read and still written for a link that has no place) or
    // { "h": hash, "at": [x, y, z] }.
    private static void Write(string extractedDir, Dictionary<string, List<Link>> links)
    {
        string path = Path.Combine(extractedDir, FileName);
        try
        {
            if (links.Count == 0)
            {
                File.Delete(path);
            }
            else
            {
                using var stream = new MemoryStream();
                using (var json = new Utf8JsonWriter(stream))
                {
                    json.WriteStartObject();
                    foreach ((string name, List<Link> hulls) in links.OrderBy(l => l.Key, StringComparer.Ordinal))
                    {
                        json.WriteStartArray(name);
                        foreach (Link link in hulls)
                        {
                            if (link.At is not { } at)
                            {
                                json.WriteNumberValue(link.Hull);
                                continue;
                            }
                            json.WriteStartObject();
                            json.WriteNumber("h", link.Hull);
                            json.WriteStartArray("at");
                            json.WriteNumberValue(at.X);
                            json.WriteNumberValue(at.Y);
                            json.WriteNumberValue(at.Z);
                            json.WriteEndArray();
                            json.WriteEndObject();
                        }
                        json.WriteEndArray();
                    }
                    json.WriteEndObject();
                }
                AtomicFile.WriteAllBytes(path, stream.ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unrecorded link costs moving the hull by hand, nothing more
        }
        lock (Cache) Cache.Remove(path);
    }

    // The file as last read, by path, with the time stamp it had: every drag start, delete and duplicate asks
    // about every node it touches, and parsing the file for each of them is what made a large selection stall.
    private static readonly Dictionary<string, (DateTime Stamp, Dictionary<string, List<Link>> Links)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, List<Link>> Read(string extractedDir)
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

            var links = new Dictionary<string, List<Link>>();
            using (JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path)))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) return new();
                foreach (JsonProperty entry in document.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Array) continue;
                    var hulls = new List<Link>();
                    foreach (JsonElement item in entry.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt64(out ulong bare))
                        {
                            hulls.Add(new Link(bare, null));
                        }
                        else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("h", out JsonElement h)
                                 && h.TryGetUInt64(out ulong hash))
                        {
                            Vector3? at = null;
                            if (item.TryGetProperty("at", out JsonElement place) && place.ValueKind == JsonValueKind.Array
                                && place.GetArrayLength() == 3)
                            {
                                var point = new Vector3(place[0].GetSingle(), place[1].GetSingle(), place[2].GetSingle());
                                if (float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z)) at = point;
                            }
                            hulls.Add(new Link(hash, at));
                        }
                    }
                    if (hulls.Count > 0) links[entry.Name] = hulls;
                }
            }
            lock (Cache) Cache[path] = (stamp, links);
            return Copy(links);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or FormatException or InvalidOperationException)
        {
            return new();
        }
    }

    // Callers change what they are handed (Set, Rename); the kept one stays as the file is.
    private static Dictionary<string, List<Link>> Copy(Dictionary<string, List<Link>> links) =>
        links.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
}
