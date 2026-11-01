namespace Illusion.Assets.Compiled;

/// <summary>Archive candidates for native placement activation. Resource GUIDs, not these names,
/// are subsequently used to resolve each model and its shared descriptor table.</summary>
public static class CompiledNativePlacementCatalog
{
    public static IReadOnlyList<FileInfo> ForNativePlacements(FileInfo scene)
    {
        string stem = Path.GetFileNameWithoutExtension(scene.Name);
        string? variant = stem.StartsWith("bc_", StringComparison.OrdinalIgnoreCase) ? "bc"
            : stem.StartsWith("si_", StringComparison.OrdinalIgnoreCase) ? "si" : null;
        if (variant is null || scene.Directory is not { } directory) return [];
        var result = new List<FileInfo>();
        if (directory.Name.Equals("city", StringComparison.OrdinalIgnoreCase))
        {
            var regional = directory.EnumerateFiles("*_general.sds")
                .Select(file => (File: file, Prefix: Path.GetFileNameWithoutExtension(file.Name)[..^8]))
                .Where(item => stem.Equals(item.Prefix + "_general", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith(item.Prefix + "_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Prefix.Length).ThenBy(item => item.File.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.File).FirstOrDefault();
            if (regional is not null) result.Add(regional);
            var global = new FileInfo(Path.Combine(directory.Parent!.FullName, "city_crash", variant + "_city_crash.sds"));
            if (global.Exists) result.Add(global);
        }
        else if (directory.Name.Equals("city_crash", StringComparison.OrdinalIgnoreCase) && scene.Name.Equals(variant + "_city_crash.sds", StringComparison.OrdinalIgnoreCase))
            result.Add(scene);
        return result;
    }
}
