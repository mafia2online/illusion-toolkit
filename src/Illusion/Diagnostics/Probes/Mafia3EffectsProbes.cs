using System.IO;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Domain.Properties;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledEffects(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "particles", "particles.sds"));
        if (!archive.Exists) return;
        string extracted = SdsMeshLoader.EnsureExtracted(archive);
        var paths = SdsManifest.Load(extracted).GetFiles("Generic");
        var before = paths.ToDictionary(p => p, File.ReadAllBytes);
        byte[] archiveBefore = File.ReadAllBytes(archive.FullName);
        try
        {
            var document = new CompiledSceneDocument(archive);
            var library = new CompiledEffectLibraryData(document.Graph);
            var generations = library.Effects.SelectMany(library.Generations).ToArray();
            check("M3 native effects library counts", library.Effects.Count == 1196 && generations.Length == 4535
                && generations.Sum(g => library.Operators(g).Count) == 43925);
            var root = document.Roots.Single(n => n.Name == "Native effects");
            check("M3 effect generation hierarchy is available", root.Children.Count == library.Effects.Count
                && root.Children.Sum(e => e.Children.Count) == generations.Length);
            var generation = root.Children[0].Children[0];
            var properties = ((IPropertySource)generation.Source!).GetPropertyGroups().SelectMany(g => g.Properties).ToArray();
            check("M3 effect properties have unique IDs", properties.Select(p => p.Id).Distinct().Count() == properties.Length);
            var editableLibrary = new CompiledEffectLibrary(document.Graph);
            bool metadataSafe = true;
            int metadataCount = 0, nestedMetadataCount = 0;
            foreach (var curve in document.Graph.Nodes.Where(n => n.ObjectId != 0
                && document.Graph.Fields(n).Keys.Any(k => k is "m_NumKeys" or "m_KeysDimension" or "m_TangentsValid")))
                foreach (var field in editableLibrary.Properties(curve, "Curve metadata").GetPropertyGroups().SelectMany(g => g.Properties))
                {
                    string leaf = field.Id[(field.Id.LastIndexOf('.') + 1)..];
                    if (leaf is not ("m_NumKeys" or "m_KeysDimension" or "m_TangentsValid")) continue;
                    metadataCount++;
                    if (field.Id.Contains("m_Keys[", StringComparison.Ordinal)) nestedMetadataCount++;
                    metadataSafe &= field.IsReadOnly && field.Set is null && field.ApplyEdit is null;
                }
            check("M3 all effect curve metadata including nested key counts is read-only",
                metadataCount > 0 && nestedMetadataCount > 0 && metadataSafe);
            var property = properties.First(p => !p.IsReadOnly && p.Kind == PropertyKind.Float
                && p.Id.Contains(".m_Rate.source:", StringComparison.Ordinal));
            float old = (float)property.Get()!, changed = old + .125f;
            property.Set!(changed);
            document.SaveWorkingCopy();
            var fresh = new CompiledSceneDocument(archive);
            var reloaded = fresh.Roots.Single(n => n.Name == "Native effects").Children[0].Children[0];
            var found = ((IPropertySource)reloaded.Source!).GetPropertyGroups().SelectMany(g => g.Properties)
                .Single(p => p.Id == property.Id);
            check("M3 effect constant edit survives save and fresh document", Equals(found.Get(), changed));
            var op = fresh.Graph.Nodes.First(n => n.TypeId == 0x3EBB96BE);
            var preview = new CompiledEffectLibraryData(fresh.Graph).Constant(op, "m_Rate");
            check("M3 effect constant preview follows native edited source", preview is { Dimensions: 1 }
                && preview.Value.X == changed && preview.SharedUsers > 1);
            property.Set(old);
            document.SaveWorkingCopy();
            check("M3 effect undo restores every compiled resource byte", before.All(p => File.ReadAllBytes(p.Key).AsSpan().SequenceEqual(p.Value)));
            check("M3 effect editing never builds into source archive", File.ReadAllBytes(archive.FullName).AsSpan().SequenceEqual(archiveBefore));
        }
        finally
        {
            foreach (var (path, bytes) in before)
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(path, bytes);
        }
    }
}
