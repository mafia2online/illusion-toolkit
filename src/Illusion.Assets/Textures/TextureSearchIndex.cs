namespace Illusion.Assets.Textures;

/// <summary>
/// Global .dds lookup across the WHOLE resources mirror (every extracted SDS, ~1600 folders / ~55k files
/// on a full install) — so the material editor resolves any texture the game ships, not only the folders
/// of currently loaded districts. Built once per session by one recursive scan; <see cref="WarmUp"/> runs
/// it in the background during catalog init so the first material click doesn't block. First path wins per
/// name — duplicates across archives are shipping copies of the same texture. Textures of an SDS that was
/// never extracted are not on disk and therefore not found — extraction happens through normal use.
/// </summary>
public static class TextureSearchIndex
{
    private static readonly object Sync = new();
    private static volatile Dictionary<string, string>? _byName;

    public static bool IsBuilt => _byName != null;

    public static int Count => _byName?.Count ?? 0;

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
        if (_byName != null) return;
        lock (Sync)
        {
            if (_byName != null) return;
            string? resources = MafiaEnvironment.IsInitialized ? MafiaEnvironment.ResourcesFolder : null;
            if (resources == null || !Directory.Exists(resources)) return;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(resources, "*.dds", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(path);
                if (!map.ContainsKey(name)) map[name] = path;
            }
            _byName = map; // published last — readers never see a half-filled index
        }
    }

    /// <summary>Announces a texture the toolkit just wrote into the mirror, so it resolves without a
    /// rescan. An existing name keeps its first path (the first-wins rule of the scan). No-op while the
    /// index is unbuilt — the eventual scan finds the file on disk by itself.</summary>
    public static void Register(string path)
    {
        lock (Sync)
        {
            Dictionary<string, string>? current = _byName;
            string name = Path.GetFileName(path);
            if (current == null || current.ContainsKey(name)) return;
            _byName = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase) { [name] = path };
        }
    }

    /// <summary>Full path of a texture name anywhere in the mirror, or null. Blocks on the first call
    /// if the background build has not finished yet (WarmUp makes that rare).</summary>
    public static string? FindPath(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        EnsureBuilt();
        return _byName != null && _byName.TryGetValue(name, out string? path) ? path : null;
    }
}
