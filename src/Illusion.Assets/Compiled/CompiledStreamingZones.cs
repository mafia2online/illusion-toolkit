using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using Illusion.Assets.World;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Editor streaming footprints from compiled district bounds, independent of Mafia II AREA data.</summary>
public static class CompiledStreamingZones
{
    private sealed record BoundsCache(string Archive, long Size, long Modified, string WorkingSignature, float[] Min, float[] Max);

    public static List<AreaZone> Load(Func<FileInfo, string> extract, IReadOnlyCollection<string> districts)
    {
        string cachePath = Path.Combine(MafiaEnvironment.ResourcesFolder!, ".compiled-district-bounds.json");
        var old = new Dictionary<string, BoundsCache>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(cachePath))
                foreach (var value in JsonSerializer.Deserialize<BoundsCache?[]>(File.ReadAllBytes(cachePath)) ?? [])
                    if (value is not null && !string.IsNullOrWhiteSpace(value.Archive) && Valid(value.Min, value.Max))
                        old[value.Archive] = value;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        var updated = new List<BoundsCache>();
        var zones = new List<AreaZone>();
        foreach (string district in districts)
        {
            var archives = new[] { "_lod2.sds", "_lod1.sds", "_lod0.sds", ".sds" }
                .Select(suffix => new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + suffix)))
                .Where(file => file.Exists).ToArray();
            if (archives.Length == 0) continue;
            // Extract before checking the cache: saved working copies can change without rebuilding SDS.
            var folders = archives.Select(extract).ToArray();
            string signature = string.Join("|", archives.Select(file => $"{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}")
                .Concat(folders.SelectMany(folder => Directory.EnumerateFiles(folder, "Generic*", SearchOption.TopDirectoryOnly))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => { var file = new FileInfo(path); return $"{path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"; })));
            var archive = archives[0];
            BoundsCache? bounds = null;
            if (old.TryGetValue(archive.FullName, out var cached) && cached.WorkingSignature == signature
                && Valid(cached.Min, cached.Max)) bounds = cached;
            else
            {
                Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
                foreach (string folder in folders)
                    if (Measure(folder) is { } box) { min = Vector3.Min(min, box.Min); max = Vector3.Max(max, box.Max); }
                if (Finite(min) && Finite(max))
                    bounds = new BoundsCache(archive.FullName, archive.Length, archive.LastWriteTimeUtc.Ticks, signature,
                        [min.X, min.Y, min.Z], [max.X, max.Y, max.Z]);
            }
            if (bounds is null) continue;
            updated.Add(bounds);
            zones.Add(new AreaZone { Name = district, Min = new Vector3(bounds.Min[0], bounds.Min[1], bounds.Min[2]),
                Max = new Vector3(bounds.Max[0], bounds.Max[1], bounds.Max[2]), Districts = [district] });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        string temporary = cachePath + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(updated));
            File.Move(temporary, cachePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return zones;
    }

    public static (Vector3 Min, Vector3 Max)? Measure(string extractedFolder)
    {
        var resources = new List<CompiledResource>();
        var manifest = SdsManifest.Load(extractedFolder);
        var entries = System.Xml.Linq.XElement.Load(Path.Combine(extractedFolder, "SDSContent.xml"))
            .Elements("ResourceEntry").Where(e => e.Element("Type")?.Value == "Generic")
            .ToDictionary(e => e.Element("File")!.Value);
        foreach (string path in manifest.GetFiles("Generic"))
        {
            // Older extracted manifests expose the wrapper only as an attribute; read it without changing it.
            var element = entries[Path.GetFileName(path)];
            byte[] prefix = Convert.FromBase64String(element.Attribute("Prefix")!.Value);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
            if (type is 0xb2f3e413 or 0xb4347d18) resources.Add(CompiledResource.Load(path));
        }
        var graph = new CompiledGraph(resources);
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var scene in graph.Nodes)
        {
            if (graph.Field(scene, "m_Name") is null || graph.Field(scene, "m_Transform") is not { DataSize: 48 } transform) continue;
            Matrix4x4 world = CompiledSceneDocument.ReadMatrix(graph.Data(transform));
            foreach (var reference in graph.Items(graph.Field(scene, "m_Components")))
            {
                var component = graph.Resolve(reference);
                foreach (var item in graph.Items(graph.Field(component, "m_Visuals")))
                {
                    var visual = graph.Resolve(item);
                    if (graph.Field(visual, "m_Data") is not { DataSize: >= 40 } data) continue;
                    var bytes = graph.Data(data);
                    Vector3 lo = CompiledMeshDecoder.Vector(bytes[16..]);
                    Vector3 hi = CompiledMeshDecoder.Vector(bytes[28..]);
                    if (!Finite(lo) || !Finite(hi) || lo.X > hi.X || lo.Y > hi.Y || lo.Z > hi.Z) continue;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 point = Vector3.Transform(new Vector3((corner & 1) == 0 ? lo.X : hi.X,
                            (corner & 2) == 0 ? lo.Y : hi.Y, (corner & 4) == 0 ? lo.Z : hi.Z), world);
                        min = Vector3.Min(min, point); max = Vector3.Max(max, point);
                    }
                }
            }
        }
        return Finite(min) && Finite(max) && max.X > min.X && max.Y > min.Y ? (min, max) : null;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
        && Math.Abs(value.X) < 10_000_000 && Math.Abs(value.Y) < 10_000_000 && Math.Abs(value.Z) < 10_000_000;
    private static bool Valid(float[]? min, float[]? max) => min is { Length: 3 } && max is { Length: 3 }
        && Finite(new Vector3(min[0], min[1], min[2])) && Finite(new Vector3(max[0], max[1], max[2]))
        && min[0] < max[0] && min[1] < max[1] && min[2] <= max[2];
}
