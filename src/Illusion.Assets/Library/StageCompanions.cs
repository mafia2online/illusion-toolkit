namespace Illusion.Assets.Library;

/// <summary>
/// Archives that another archive cannot be LOOKED at without. Mafia II does not make every resource
/// self-contained: a car keeps its own body, its own paint and its own sounds, and takes everything generic
/// — the chrome and glass shaders, the headlight glows, the wheels and hubcaps — from a shared library the
/// game always has loaded beside it. Open the car on its own and it comes out white and wheel-less, because
/// two thirds of the textures its materials name are simply not in the file.
/// <para>
/// Nothing INSIDE a car names that library: no frame, material or buffer reference in the archive points
/// outward at all (the frame format has no cross-archive addressing). The pairing is knowledge about the
/// game, which is why it is written down here rather than discovered.
/// </para>
/// </summary>
public static class StageCompanions
{
    // The shared car library, in the order the game would have it: cars_universal carries the wheels,
    // hubcaps, lights and the generic Univ* / wheel_univers_* textures; cars_universal2 a second set.
    private static readonly string[] CarLibrary = { "cars_universal.sds", "cars_universal2.sds" };

    /// <summary>
    /// The archives to load alongside <paramref name="sds"/> so it can be shown as the game shows it.
    /// Empty for anything with no known companion — which is most of the game.
    /// </summary>
    public static IReadOnlyList<FileInfo> For(FileInfo sds)
    {
        DirectoryInfo? folder = sds.Directory;
        if (folder?.Parent?.Name.Equals("sds_retail", StringComparison.OrdinalIgnoreCase) == true
            && (folder.Name.Equals("city", StringComparison.OrdinalIgnoreCase)
                || folder.Name.Equals("city_crash", StringComparison.OrdinalIgnoreCase)))
            return CompiledCity(sds, folder);
        if (folder == null || !folder.Name.Equals("cars", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<FileInfo>();

        // The libraries are not their own companions, and neither are the archives in cars\ that are no
        // vehicle at all (a texture pack has no materials to satisfy).
        if (CarLibrary.Contains(sds.Name, StringComparer.OrdinalIgnoreCase)) return Array.Empty<FileInfo>();

        var found = new List<FileInfo>();
        foreach (string name in CarLibrary)
        {
            var companion = new FileInfo(Path.Combine(folder.FullName, name));
            if (companion.Exists) found.Add(companion);
        }
        return found;
    }

    private static IReadOnlyList<FileInfo> CompiledCity(FileInfo archive, DirectoryInfo folder)
    {
        var found = new List<FileInfo>();
        string stem = Path.GetFileNameWithoutExtension(archive.Name);
        var global = new FileInfo(Path.Combine(folder.Parent!.FullName, "city", "bourbon_tex_global.sds"));
        if (global.Exists && !global.FullName.Equals(archive.FullName, StringComparison.OrdinalIgnoreCase)) found.Add(global);
        string city = stem.StartsWith("si_", StringComparison.OrdinalIgnoreCase)
            || stem.Contains("_si_", StringComparison.OrdinalIgnoreCase) ? "si" : "bc";
        var shared = new FileInfo(Path.Combine(folder.Parent!.FullName, "city_univers", city + "_city_univers.sds"));
        if (shared.Exists) found.Add(shared);
        if (!folder.Name.Equals("city", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_textures", StringComparison.OrdinalIgnoreCase)) return found;

        // Fusion shares low-detail textures at district scope: e.g. french_east_block_b
        // takes french_east_lod2_textures, while bayou_block_a takes bayou_lod2_textures.
        const string suffix = "_lod2_textures";
        var texturePack = folder.EnumerateFiles("*_lod2_textures.sds")
            .Select(file => (File: file, Prefix: Path.GetFileNameWithoutExtension(file.Name)[..^suffix.Length]))
            .Where(item => stem.Equals(item.Prefix, StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith(item.Prefix + "_", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Prefix.Length).FirstOrDefault().File;
        if (texturePack is not null) found.Add(texturePack);
        const string streamingPrefix = "bourbon_tex_";
        var streamingPack = folder.EnumerateFiles(streamingPrefix + "*.sds")
            .Select(file => (File: file, Prefix: Path.GetFileNameWithoutExtension(file.Name)[streamingPrefix.Length..]))
            .Where(item => stem.Equals(item.Prefix, StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith(item.Prefix + "_", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Prefix.Length).FirstOrDefault().File;
        if (streamingPack is not null) found.Add(streamingPack);
        return found;
    }
}
