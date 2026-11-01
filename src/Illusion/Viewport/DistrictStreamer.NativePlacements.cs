using System.IO;
using Illusion.Assets;
using Illusion.Assets.Compiled;

namespace Illusion.Viewport;

internal sealed partial class DistrictStreamer
{
    private const string NativeGlobalPlacementPrefix = "native-global:";
    private static string NativePlacementKey(FileInfo file) =>
        (file.Directory?.Name.Equals("city_crash", StringComparison.OrdinalIgnoreCase) == true
            ? NativeGlobalPlacementPrefix : "native-region:") + file.FullName;

    // Native instance archives are ordinary compiled documents. Each archive activates its OWN
    // root resources once; dependency loads resolve templates and never spawn another archive.
    private readonly Dictionary<string, IReadOnlyList<FileInfo>> _nativePlacementArchives = new(StringComparer.OrdinalIgnoreCase);
    private void AddNativePlacementItems(List<(FileInfo File, string Label, string? District)> items, FileInfo? scene)
    {
        if (MafiaEnvironment.Profile.ArchiveVersion != 20 || scene is null) return;
        if (!_nativePlacementArchives.TryGetValue(scene.FullName, out var archives))
            _nativePlacementArchives.Add(scene.FullName, archives = CompiledNativePlacementCatalog.ForNativePlacements(scene));
        foreach (var file in archives)
            if (!items.Any(item => item.File.FullName.Equals(file.FullName, StringComparison.OrdinalIgnoreCase)))
                items.Add((file, "Placements: " + Path.GetFileNameWithoutExtension(file.Name), NativePlacementKey(file)));
    }

    private List<(FileInfo File, string Label, string? District)> NativePlacementsForDistricts(IEnumerable<string> districts)
    {
        var items = new List<(FileInfo File, string Label, string? District)>();
        foreach (string name in districts)
            if (_host.Catalogs.Areas.FirstOrDefault(area => area.BaseName == name) is { } area)
                AddNativePlacementItems(items, area.FileFor(_winter));
        return items;
    }
}
