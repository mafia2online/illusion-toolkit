using System.Text.Json;

namespace Illusion.Assets.Sds;

/// <summary>
/// Where the geometry already carried into an archive went — so the same object imported again draws from the
/// same buffers instead of bringing another copy of them.
/// <para>
/// The shipped scenes share geometry freely (ten wanted posters on one block, a hundred chairs on one), and an
/// archive that held ten copies of one chair's vertices would be ten times the size for nothing. Per source
/// archive, each source buffer is mapped to the name of the buffer it was copied into here. The map is a
/// memory, not a promise: a mapped buffer that is no longer in the scene (its import was undone, its object
/// deleted and saved away) is simply copied again.
/// </para>
/// </summary>
public sealed class ImportGeometry
{
    private const string FileName = "illusion_geometry.json";

    private readonly string _dir;
    private readonly string _source;
    private readonly Dictionary<string, Dictionary<string, string>> _all;

    private ImportGeometry(string dir, string source, Dictionary<string, Dictionary<string, string>> all)
    {
        _dir = dir;
        _source = source;
        _all = all;
    }

    /// <summary>The map for objects from <paramref name="sourceArchive"/> carried into <paramref name="extractedDir"/>.</summary>
    public static ImportGeometry Load(string extractedDir, string sourceArchive)
    {
        ArgumentException.ThrowIfNullOrEmpty(extractedDir);
        ArgumentException.ThrowIfNullOrEmpty(sourceArchive);
        string path = Path.Combine(extractedDir, FileName);
        Dictionary<string, Dictionary<string, string>>? all = null;
        try
        {
            if (File.Exists(path)) all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            all = null;
        }
        return new ImportGeometry(extractedDir, sourceArchive.ToLowerInvariant(), all ?? new());
    }

    /// <summary>The name of the buffer a source buffer was copied into before, or null.</summary>
    public string? Copied(char kind, ulong sourceHash) =>
        _all.TryGetValue(_source, out Dictionary<string, string>? map) && map.TryGetValue(Key(kind, sourceHash), out string? name)
            ? name
            : null;

    /// <summary>Remembers where a source buffer was copied to.</summary>
    public void Remember(char kind, ulong sourceHash, string destinationName)
    {
        if (!_all.TryGetValue(_source, out Dictionary<string, string>? map)) _all[_source] = map = new();
        map[Key(kind, sourceHash)] = destinationName;
    }

    /// <summary>Writes the map back beside the working copy (outside its manifest — the game never sees it).</summary>
    public void Save()
    {
        try
        {
            AtomicFile.WriteAllBytes(Path.Combine(_dir, FileName), JsonSerializer.SerializeToUtf8Bytes(_all));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // forgetting costs the next import of this object a copy of its geometry, nothing more
        }
    }

    private static string Key(char kind, ulong hash) => $"{kind}{hash:x16}";
}
