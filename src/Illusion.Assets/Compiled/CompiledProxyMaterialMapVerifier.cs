using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Checks persisted material coverage against the actual self-contained far GENR, not a claimed JSON list.</summary>
internal static class CompiledProxyMaterialMapVerifier
{
    internal static IReadOnlyDictionary<ulong, string> Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path)); var root = document.RootElement;
        if (!root.TryGetProperty("Version", out var version) || version.GetInt32() != 2
            || !root.TryGetProperty("Parent", out var parentValue) || !parentValue.TryGetUInt64(out ulong parent) || parent == 0
            || !root.TryGetProperty("Pairs", out var pairs) || pairs.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("MaterialTextures", out var materials) || materials.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("DestinationResource", out var destination) || destination.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Proxy mapping has an incomplete material contract: " + Path.GetFileName(path));
        if (!root.TryGetProperty("Bounds", out var bounds) || bounds.ValueKind != JsonValueKind.Object || !bounds.EnumerateObject().Any()
            || bounds.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() != 6
                || p.Value.EnumerateArray().Any(v => !v.TryGetSingle(out float f) || !float.IsFinite(f))))
            throw new InvalidDataException("Proxy mapping has an invalid bounds contract.");
        string relative = destination.GetString()!;
        string workspace = Path.GetFullPath(MafiaEnvironment.ResourcesFolder!);
        string full = Path.GetFullPath(Path.Combine(workspace, relative));
        if (!full.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !files.TryGetProperty(relative, out var expectedHash) || expectedHash.ValueKind != JsonValueKind.String
            || !File.Exists(full) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))) != expectedHash.GetString())
            throw new InvalidDataException("Proxy destination snapshot is missing or changed: " + Path.GetFileName(path));
        var targetIds = new HashSet<ulong>(); int pairCount = 0;
        foreach (var pair in pairs.EnumerateObject())
        {
            if (!ulong.TryParse(pair.Name, NumberStyles.None, CultureInfo.InvariantCulture, out ulong source) || source == 0
                || !pair.Value.TryGetUInt64(out ulong target) || target == 0 || !targetIds.Add(target))
                throw new InvalidDataException("Invalid proxy scene identity mapping.");
            pairCount++;
        }
        if (pairCount == 0) throw new InvalidDataException("Empty proxy scene identity mapping.");
        var declared = new Dictionary<ulong, string>();
        foreach (var material in materials.EnumerateObject())
        {
            if (!ulong.TryParse(material.Name, NumberStyles.None, CultureInfo.InvariantCulture, out ulong hash) || hash == 0
                || material.Value.ValueKind != JsonValueKind.String || material.Value.GetString() is not { Length: 64 } signature
                || signature.Any(ch => !Uri.IsHexDigit(ch)) || !declared.TryAdd(hash, signature))
                throw new InvalidDataException("Invalid proxy material signature.");
        }
        var resource = CompiledResource.Load(full); var graph = new CompiledGraph([resource]);
        if (graph.Objects.GetValueOrDefault(parent)?.TypeId != 0x23001d00) throw new InvalidDataException("Missing native proxy parent.");
        var scenes = new HashSet<CompiledElement>(); var seen = new HashSet<CompiledElement>();
        void Visit(CompiledElement? collection)
        {
            if (collection?.TypeId != 0x8fa874c4 || !seen.Add(collection)) return;
            foreach (var entry in graph.Items(graph.Field(collection, "m_Objects")))
            {
                var value = graph.Resolve(entry);
                if (value?.TypeId == 0x4403597e) scenes.Add(value); else Visit(value);
            }
        }
        foreach (var value in resource.Roots.Where(n => n.Event == 2)) Visit(graph.Resolve(graph.Field(value, "m_Object")));
        var actualScenes = new HashSet<ulong>(); var actualMaterials = new HashSet<ulong>();
        foreach (var scene in scenes)
        {
            var models = graph.Items(graph.Field(scene, "m_Components")).Select(graph.Resolve).OfType<CompiledElement>()
                .Where(n => n.TypeId == 0x2da22400).ToArray();
            if (!models.Any(model => graph.Resolve(graph.Field(model, "m_LODGroup"))?.ObjectId == parent)) continue;
            if (models.Length != 1) throw new InvalidDataException("Ambiguous native proxy model ownership.");
            actualScenes.Add(scene.ObjectId);
            var entries = graph.Items(graph.Field(models[0], "m_Visuals"));
            if (entries.Count == 0) throw new InvalidDataException("Empty native proxy visual list.");
            foreach (var entry in entries)
            {
                var visual = graph.Resolve(entry) ?? throw new InvalidDataException("Unresolved native proxy visual.");
                var face = graph.Resolve(graph.Field(visual, "m_FaceGroupSet"));
                var tables = graph.Items(graph.Field(graph.Field(face, "m_Data"), "m_FaceGroups"));
                if (tables.Count != 1) throw new InvalidDataException("Unknown native prepared material levels.");
                var bytes = graph.Data(tables[0]);
                if (bytes.Length < 4) throw new InvalidDataException("Invalid native proxy face groups.");
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                if (bytes.Length != 4L + count * 40L) throw new InvalidDataException("Unknown native proxy face-group layout.");
                for (int i = 0; i < count; i++)
                {
                    ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(12 + i * 40)..]); if (hash != 0) actualMaterials.Add(hash);
                }
            }
        }
        if (!actualScenes.SetEquals(targetIds) || actualMaterials.Count == 0 || !actualMaterials.SetEquals(declared.Keys))
            throw new InvalidDataException("Proxy material or scene coverage does not match its native destination.");
        return declared;
    }
}
