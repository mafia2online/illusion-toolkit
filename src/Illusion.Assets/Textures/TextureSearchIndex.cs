namespace Illusion.Assets.Textures;

/// <summary>
/// Global .dds lookup across the WHOLE resources mirror (every extracted SDS, ~1600 folders / ~55k files
/// on a full install) — so the material editor resolves any texture the game ships, not only the folders
/// of currently loaded districts. Built by one recursive scan; <see cref="WarmUp"/> runs it in the background
/// during catalog init so the first material click doesn't block. The first folder found wins per name —
/// duplicates across archives are shipping copies of the same texture.
/// <para>
/// The scan is a picture of one moment, and the mirror changes under it: an archive extracted later in the
/// session brings textures the scan never saw (<see cref="RegisterFolder"/> — the extractor calls it), and a
/// texture the toolkit itself carried into a working copy can be taken out of it again. So every folder that
/// holds a name is remembered, not only the first, and an answer is checked against the disk before it is
/// given: a copy that has gone falls through to the next one instead of answering with a path to nothing.
/// </para>
/// </summary>
public static class TextureSearchIndex
{
    private static readonly object Sync = new();

    // name → the folders that hold a file of that name, first found first. Folders are kept once and named by
    // their place in the list: sixty thousand paths would otherwise repeat a few hundred folder names.
    private static Dictionary<string, List<int>>? _byName;
    private static readonly List<string> Folders = new();
    private static readonly Dictionary<string, int> FolderIds = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsBuilt
    {
        get { lock (Sync) return _byName != null; }
    }

    public static int Count
    {
        get { lock (Sync) return _byName?.Count ?? 0; }
    }

    /// <summary>Builds the index in the background (no-op when already built / environment not ready).</summary>
    public static void WarmUp() => Task.Run(() =>
    {
        try { EnsureBuilt(); }
        catch { /* an unreadable mirror must never take the app down — FindPath just misses */ }
    });

    /// <summary>Scans the resources mirror once. Stays unbuilt (and retries later) while the environment
    /// has no initialized game path.</summary>
    public static void EnsureBuilt()
    {
        lock (Sync)
        {
            if (_byName != null) return;
            string? resources = MafiaEnvironment.IsInitialized ? MafiaEnvironment.ResourcesFolder : null;
            if (resources == null || !Directory.Exists(resources)) return;

            var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(resources, "*.dds", SearchOption.AllDirectories))
            {
                Add(map, path);
            }
            _byName = map; // set last — a failed scan leaves the index unbuilt, to be tried again
        }
    }

    // Caller holds Sync.
    private static void Add(Dictionary<string, List<int>> map, string path)
    {
        string? folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder)) return;
        if (!FolderIds.TryGetValue(folder, out int id))
        {
            FolderIds[folder] = id = Folders.Count;
            Folders.Add(folder);
        }
        string name = Path.GetFileName(path);
        if (!map.TryGetValue(name, out List<int>? holders)) map[name] = holders = new List<int>(1);
        if (!holders.Contains(id)) holders.Add(id);
    }

    /// <summary>Announces a texture the toolkit just wrote into the mirror, so it resolves without a
    /// rescan. A name already known keeps the folder it was first found in; this one is remembered behind
    /// it. No-op while the index is unbuilt — the eventual scan finds the file on disk by itself.</summary>
    public static void Register(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (Sync)
        {
            if (_byName != null) Add(_byName, path);
        }
    }

    /// <summary>
    /// Announces every texture of a folder that has just appeared in the mirror — an archive extracted after
    /// the scan. Without it, a texture that lives in an archive opened later in the session stayed "in no
    /// extracted archive" until the program was restarted. No-op while the index is unbuilt.
    /// </summary>
    public static void RegisterFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        lock (Sync)
        {
            if (_byName == null || !Directory.Exists(folder)) return;
            foreach (string path in Directory.EnumerateFiles(folder, "*.dds", SearchOption.AllDirectories))
            {
                Add(_byName, path);
            }
        }
    }

    /// <summary>Full path of a texture name anywhere in the mirror, or null — a file that is there now: a
    /// copy that has been removed since it was indexed is forgotten and the next one answers. Blocks on the
    /// first call if the background build has not finished yet (WarmUp makes that rare).</summary>
    public static string? FindPath(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        EnsureBuilt();
        lock (Sync)
        {
            if (_byName == null || !_byName.TryGetValue(name, out List<int>? holders)) return null;
            while (holders.Count > 0)
            {
                string path = Path.Combine(Folders[holders[0]], name);
                if (File.Exists(path)) return path;
                holders.RemoveAt(0);
            }
            _byName.Remove(name);
            return null;
        }
    }
}
