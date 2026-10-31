using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledNavigation(Action<string, bool> check)
    {
        foreach (string name in new[] { "bc_easthighland_general.sds", "bc_magnolia_unique_church_a_general.sds" })
        {
            var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, name));
            if (!archive.Exists) continue;
            var document = new CompiledSceneDocument(archive);
            string folder = MafiaEnvironment.ExtractedDir(archive);
            var snapshot = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes);
            byte[] archiveBytes = File.ReadAllBytes(archive.FullName);
            try
            {
                var section = document.NavigationMeshes.Single();
                check(name + " isolated translation capability", section.CanTranslateIsolatedSection(out _));
                var before = section.Meshes.Single().Vertices.ToArray();
                var triangles = section.Meshes.Single().TriangleIndices.ToArray();
                var offset = new Vector3(3, -7, 1);
                var expected = before.Select(v => v + offset).ToArray();
                var edit = document.TranslateNavigationSection(0, offset, out var refusal)
                    ?? throw new InvalidOperationException(refusal);
                check(name + " canonical translated vertices", section.Meshes.Single().Vertices.SequenceEqual(expected));
                check(name + " topology retained", section.Meshes.Single().TriangleIndices.SequenceEqual(triangles));
                document.SaveWorkingCopy();
                check(name + " translation reload", new CompiledSceneDocument(archive).NavigationMeshes.Single().Meshes.Single().Vertices.SequenceEqual(expected));
                var packed = SdsArchive.Pack(folder, MafiaEnvironment.Profile);
                using var stream = new MemoryStream();
                packed.Save(stream, new SdsWriteOptions { Compress = true });
                stream.Position = 0;
                SdsArchive.Load(stream).Extract(folder);
                check(name + " compressed build/fresh extraction", new CompiledSceneDocument(archive).NavigationMeshes.Single().Meshes.Single().Vertices.SequenceEqual(expected));
                edit.Undo(); document.SaveWorkingCopy();
                check(name + " byte-exact undo", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
                edit.Redo(); document.SaveWorkingCopy();
                check(name + " redo reload", new CompiledSceneDocument(archive).NavigationMeshes.Single().Meshes.Single().Vertices.SequenceEqual(expected));
                edit.Undo(); document.SaveWorkingCopy();
                check(name + " redo/undo exact", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
                check(name + " input archive untouched", File.ReadAllBytes(archive.FullName).SequenceEqual(archiveBytes));
            }
            finally { foreach (var (path, bytes) in snapshot) File.WriteAllBytes(path, bytes); }
        }
        var streamed = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_loc_traindepot_general.sds"));
        if (!streamed.Exists) return;
        var streamedDocument = new CompiledSceneDocument(streamed);
        var original = streamedDocument.NavigationMeshes.Select(n => n.ToBytes()).ToArray();
        check("streamed navigation translation capability refused", !streamedDocument.NavigationMeshes[0].CanTranslateIsolatedSection(out _));
        var refused = streamedDocument.TranslateNavigationSection(0, Vector3.UnitX, out var reason);
        check("streamed navigation translation reports boundary refusal", refused is null && reason?.Contains("streaming boundaries", StringComparison.Ordinal) == true);
        check("streamed navigation refusal preserves bytes", streamedDocument.NavigationMeshes.Select((n, i) => n.ToBytes().SequenceEqual(original[i])).All(v => v));
    }
}
