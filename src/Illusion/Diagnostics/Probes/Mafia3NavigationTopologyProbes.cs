using System.IO;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledNavigationTopology(Action<string, bool> check)
    {
        string? executable = Environment.GetEnvironmentVariable("ILLUSION_MAFIA3_COOK_EXE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;
        Task.Run(async () =>
        {
            foreach (var (name, edge) in new[] { ("bc_loc_quarry_dummy_general.sds", 1359), ("bc_loc_sammys_club_dummy_general.sds", 1250) })
            {
                var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, name));
                if (!archive.Exists) continue;
                var document = new CompiledSceneDocument(archive); var nav = document.NavigationMeshes.Single();
                string folder = MafiaEnvironment.ExtractedDir(archive);
                var snapshot = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
                byte[] source = nav.ToBytes(), archiveSource = File.ReadAllBytes(archive.FullName);
                var originalIndices = nav.Meshes.Single().TriangleIndices.ToArray(); var originalVertices = nav.Meshes.Single().Vertices.ToArray();
                try
                {
                    check(name + " topology candidate listing is bounded and read-only", nav.EdgeFlipCandidates(128).Contains(edge) && nav.ToBytes().SequenceEqual(source));
                    var result = await document.FlipNavigationEdgeAsync(0, edge, executable);
                    var action = result.Edit ?? throw new InvalidOperationException(result.Refusal);
                    byte[] cooked = nav.ToBytes(); var expected = nav.Meshes.Single().TriangleIndices.ToArray();
                    check(name + " native diagonal flip changes canonical topology without moving vertices", !expected.SequenceEqual(originalIndices) && originalVertices.SequenceEqual(nav.Meshes.Single().Vertices));
                    document.SaveWorkingCopy();
                    check(name + " topology fresh document reload", new CompiledSceneDocument(archive).NavigationMeshes.Single().Meshes.Single().TriangleIndices.SequenceEqual(expected));
                    using (var stream = new MemoryStream())
                    {
                        SdsArchive.Pack(folder, MafiaEnvironment.Profile).Save(stream, new SdsWriteOptions { Compress = true });
                        stream.Position = 0; SdsArchive.Load(stream).Extract(folder);
                    }
                    check(name + " topology compressed build and fresh extraction", new CompiledSceneDocument(archive).NavigationMeshes.Single().Meshes.Single().TriangleIndices.SequenceEqual(expected));
                    action.Undo(); document.SaveWorkingCopy();
                    check(name + " topology exact undo preserves canonical indices", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)) && nav.Meshes.Single().TriangleIndices.SequenceEqual(originalIndices));
                    byte[] tampered = cooked.ToArray(); tampered[nav.PrepareEdgeFlip(edge).MeshObject + 8] ^= 1;
                    bool refused = false;
                    try { nav.ApplyEdgeFlipCook(edge, tampered); } catch (InvalidDataException) { refused = true; }
                    check(name + " topology response metadata whitelist", refused && nav.ToBytes().SequenceEqual(source));
                    var invalid = await document.FlipNavigationEdgeAsync(0, -1, executable);
                    check(name + " invalid topology index refuses unchanged", invalid.Edit is null && nav.ToBytes().SequenceEqual(source));
                    action.Redo(); document.SaveWorkingCopy();
                    check(name + " topology redo reload", new CompiledSceneDocument(archive).NavigationMeshes.Single().Meshes.Single().TriangleIndices.SequenceEqual(expected));
                    action.Undo(); document.SaveWorkingCopy();
                    check(name + " topology source archive untouched", File.ReadAllBytes(archive.FullName).SequenceEqual(archiveSource));
                }
                finally { foreach (var (path, bytes) in snapshot) File.WriteAllBytes(path, bytes); }
            }
        }).GetAwaiter().GetResult();
    }
}
