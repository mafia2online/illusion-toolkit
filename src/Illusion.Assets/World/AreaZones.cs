using System.Numerics;
using Illusion.Formats.Archive;
using Illusion.Formats.CityAreas;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Assets.World;

/// <summary>
/// Builds the list of <see cref="AreaZone"/> for camera streaming: AREA volume names from FrameResource
/// <c>city_univers</c> are joined with <c>cityareas.bin</c> (AREA name → 1-2 districts), district names
/// are resolved to real <c>/sds/city/*.sds</c>. A box without a cityareas entry (e.g. shop zones) or without
/// resolvable districts is skipped.
/// </summary>
public static class AreaZones
{
    public static List<AreaZone> Load(Func<FileInfo, string> ensureExtracted,
        IReadOnlyCollection<string> validBases)
    {
        var zones = new List<AreaZone>();

        var cuSds = new FileInfo(MafiaEnvironment.CityUniversSds);
        if (!cuSds.Exists) return zones;

        string extracted = ensureExtracted(cuSds);

        // cityareas: AREA name → set of resolved districts.
        var areaDistricts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string bin = Path.Combine(extracted, "missions", "CITY", "cityareas.bin");
        if (File.Exists(bin))
        {
            var bases = new HashSet<string>(validBases, StringComparer.OrdinalIgnoreCase);
            foreach (CityAreaEntry e in CityAreasFile.Load(bin).Areas)
            {
                var list = new List<string>();
                foreach (string? b in new[] { DistrictNames.Resolve(e.Target1, bases), DistrictNames.Resolve(e.Target2, bases) })
                    if (b != null && !list.Contains(b)) list.Add(b);
                if (list.Count > 0) areaDistricts[e.Name] = list;
            }
        }
        if (areaDistricts.Count == 0) return zones;

        // FrameResource city_univers → AREA boxes (name + world AABB).
        // The scene alone: a volume is a matrix and a box, and the archive's vertex and index pools - which
        // ExtractedSds reads and decodes in full - say nothing about it. This runs again after every zone edit.
        IReadOnlyList<string> scenes = SdsManifest.Load(extracted).GetFiles("FrameResource");
        if (scenes.Count == 0) return zones;
        var frame = new FrameResource(scenes[0]);
        if (frame.FrameObjects == null) return zones;

        foreach (var pair in frame.FrameObjects)
        {
            if (pair.Value is not FrameObjectArea area) continue;
            string? name = area.Name?.ToString();
            if (name == null || !areaDistricts.TryGetValue(name, out List<string>? districts)) continue;

            (Vector3 min, Vector3 max) = WorldAabb(area);
            zones.Add(new AreaZone { Name = name, Min = min, Max = max, Districts = districts });
        }
        return zones;
    }

    // Local AABB (Bounds) → world: transform the 8 corners by the WorldTransform matrix.
    private static (Vector3, Vector3) WorldAabb(FrameObjectArea area)
    {
        var lo = area.Bounds.Min;
        var hi = area.Bounds.Max;
        Matrix4x4 m = area.WorldTransform;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? lo.X : hi.X,
                (i & 2) == 0 ? lo.Y : hi.Y,
                (i & 4) == 0 ? lo.Z : hi.Z);
            Vector3 w = Vector3.Transform(corner, m);
            min = Vector3.Min(min, w);
            max = Vector3.Max(max, w);
        }
        return (min, max);
    }
}
