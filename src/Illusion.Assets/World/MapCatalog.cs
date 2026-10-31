using System.Text.RegularExpressions;
using Illusion.Formats.CityAreas;

namespace Illusion.Assets.World;

/// <summary>
/// Catalog of map areas for the selector. Primary source — <c>cityareas.bin</c> (game data:
/// list of AREA targets + district adjacency graph). Target names are resolved into <c>/sds/city/*.sds</c>
/// (with aliases and dropping sub-numbers: <c>prazdna01</c>→<c>prazdna</c>). If cityareas is missing —
/// fall back to a folder scan. The District/Interior class is taken from the inner <c>missions/</c> folder.
/// </summary>
public sealed class MapCatalog
{
    public IReadOnlyList<MapArea> Areas { get; }
    public bool FromCityAreas { get; }

    private MapCatalog(IReadOnlyList<MapArea> areas, bool fromCityAreas)
    {
        Areas = areas;
        FromCityAreas = fromCityAreas;
    }

    public static MapCatalog Build(string cityFolder, Func<FileInfo, string> ensureExtracted)
    {
        var summer = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var winter = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(cityFolder))
        {
            foreach (string f in Directory.GetFiles(cityFolder, "*.sds"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (MafiaEnvironment.Profile.ArchiveVersion == 20 && (name.EndsWith("_general", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("_collisions", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("_textures", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("bourbon_tex_", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (MafiaEnvironment.Profile.ArchiveVersion == 20 && name.Length > 5 && name[^5..^1].Equals("_lod", StringComparison.OrdinalIgnoreCase))
                {
                    string district = name[..^5];
                    if (!summer.TryGetValue(district, out var existing) || string.CompareOrdinal(name, Path.GetFileNameWithoutExtension(existing.Name)) < 0)
                        summer[district] = new FileInfo(f);
                }
                else if (name.EndsWith("_z", StringComparison.OrdinalIgnoreCase))
                    winter[name[..^2]] = new FileInfo(f);
                else
                    summer[name] = new FileInfo(f);
            }
        }

        var neighbors = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool fromCityAreas = false;

        // Primary path: cityareas.bin from city_univers.
        string? areasBin = TryLocateCityAreas(ensureExtracted);
        if (areasBin != null)
        {
            try
            {
                CityAreasFile ca = CityAreasFile.Load(areasBin);
                fromCityAreas = true;
                foreach (CityAreaEntry e in ca.Areas)
                {
                    string? b1 = DistrictNames.Resolve(e.Target1, summer.Keys);
                    string? b2 = DistrictNames.Resolve(e.Target2, summer.Keys);
                    if (b1 != null) used.Add(b1);
                    if (b2 != null) used.Add(b2);
                    if (b1 != null && b2 != null && !b1.Equals(b2, StringComparison.OrdinalIgnoreCase))
                    {
                        Edge(neighbors, b1, b2);
                        Edge(neighbors, b2, b1);
                    }
                }
            }
            catch
            {
                fromCityAreas = false;
            }
        }

        // Fallback / supplement: if cityareas gave no list — take everything from the folder.
        if (used.Count == 0)
        {
            foreach (string b in summer.Keys) used.Add(b);
        }

        var areas = new List<MapArea>();
        foreach (string b in used)
        {
            if (!summer.TryGetValue(b, out FileInfo? s)) continue;
            var area = new MapArea
            {
                BaseName = b,
                Summer = s,
                Winter = winter.TryGetValue(b, out FileInfo? w) ? w : null,
                IsInterior = IsInterior(cityFolder, b),
            };
            if (neighbors.TryGetValue(b, out HashSet<string>? nb))
                area.Neighbors.AddRange(nb.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            areas.Add(area);
        }

        // Open-world districts first, interiors below; within a group — alphabetically.
        areas = areas
            .OrderBy(a => a.IsInterior)
            .ThenBy(a => a.BaseName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MapCatalog(areas, fromCityAreas);
    }

    private static void Edge(Dictionary<string, HashSet<string>> g, string a, string b)
    {
        if (!g.TryGetValue(a, out HashSet<string>? set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            g[a] = set;
        }
        set.Add(b);
    }

    // cityareas.bin lives inside city_univers (missions\CITY\); MafiaEnvironment owns that engine path.
    private static string? TryLocateCityAreas(Func<FileInfo, string> ensureExtracted)
    {
        try { return MafiaEnvironment.TryGetCityAreasBin(ensureExtracted); }
        catch { return null; }
    }

    // Open-world district (inner missions folder cityNN_*) vs mission interior.
    private static bool IsInterior(string cityFolder, string baseName)
    {
        try
        {
            string extractedRoot = MafiaEnvironment.ResourcesFolder != null
                ? MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(cityFolder, baseName + ".sds")))
                : Path.Combine(cityFolder, "extracted", baseName + ".sds");
            string missions = Path.Combine(extractedRoot, "missions");
            if (!Directory.Exists(missions)) return false;
            string? first = Directory.GetDirectories(missions).FirstOrDefault();
            string? folder = first != null ? Path.GetFileName(first) : null;
            return folder == null || !Regex.IsMatch(folder, @"^city\d", RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
