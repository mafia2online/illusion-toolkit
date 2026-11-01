using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledNavigationCook(Action<string, bool> check)
    {
        string? copiedExecutable = Environment.GetEnvironmentVariable("ILLUSION_MAFIA3_COOK_EXE");
        if (string.IsNullOrWhiteSpace(copiedExecutable) || !File.Exists(copiedExecutable)) return;
        Task.Run(async () =>
        {
            foreach (var (name, vertex, offset) in new[] {
                ("bc_loc_quarry_dummy_general.sds", 0, new Vector3(.05f, 0, 0)),
                ("bc_loc_sammys_club_dummy_general.sds", 19, new Vector3(.05f, 0, 0)),
                ("bc_loc_quarry_dummy_general.sds", 462, new Vector3(0, 0, .02f)),
                ("bc_loc_sammys_club_dummy_general.sds", 1330, new Vector3(0, 0, .02f)) })
            {
                var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, name));
                if (!archive.Exists) continue;
                var document = new CompiledSceneDocument(archive);
                string folder = MafiaEnvironment.ExtractedDir(archive);
                var snapshot = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
                byte[] archiveBefore = File.ReadAllBytes(archive.FullName);
                try
                {
                    var nav = document.NavigationMeshes.Single();
                    byte[] before = nav.ToBytes(); var original = nav.Meshes.Single().Vertices[vertex];
                    var vertices = new Dictionary<int, Vector3> { [vertex] = original + offset };
                    var noop = await document.DeformNavigationInteriorAsync(0, new Dictionary<int, Vector3>(), copiedExecutable);
                    check(name + " native exact stock graph/no-op", noop.Edit is not null && nav.ToBytes().SequenceEqual(before));
                    var result = await document.DeformNavigationInteriorAsync(0, vertices, copiedExecutable);
                    var edit = result.Edit ?? throw new InvalidOperationException(result.Refusal);
                    check(name + " native canonical interior edit", nav.Meshes.Single().Vertices[vertex] == vertices[vertex]);
                    if (offset.Z != 0) check(name + " native interior elevation changes face planes", nav.Meshes.Single().Vertices[vertex].Z != original.Z);
                    document.SaveWorkingCopy();
                    check(name + " native interior fresh document", new CompiledSceneDocument(archive).NavigationMeshes.Single().Meshes.Single().Vertices[vertex] == vertices[vertex]);
                    var packed = SdsArchive.Pack(folder, MafiaEnvironment.Profile);
                    using var stream = new MemoryStream(); packed.Save(stream, new SdsWriteOptions { Compress = true });
                    stream.Position = 0; SdsArchive.Load(stream).Extract(folder);
                    check(name + " native interior compressed build", new CompiledSceneDocument(archive).NavigationMeshes.Single().Meshes.Single().Vertices[vertex] == vertices[vertex]);
                    edit.Undo(); document.SaveWorkingCopy();
                    check(name + " native interior byte-exact undo", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
                    edit.Redo(); document.SaveWorkingCopy();
                    check(name + " native interior redo reload", new CompiledSceneDocument(archive).NavigationMeshes.Single().Meshes.Single().Vertices[vertex] == vertices[vertex]);
                    edit.Undo(); document.SaveWorkingCopy();
                    int boundary = nav.PrepareInteriorDeformation(new Dictionary<int, Vector3>()).BoundaryVertices[0];
                    var refused = await document.DeformNavigationInteriorAsync(0,
                        new Dictionary<int, Vector3> { [boundary] = nav.Meshes.Single().Vertices[boundary] + Vector3.UnitX }, copiedExecutable);
                    check(name + " native boundary refusal unchanged", refused.Edit is null && nav.ToBytes().SequenceEqual(before));
                    byte[] tampered = before.ToArray();
                    tampered[nav.PrepareInteriorDeformation(new Dictionary<int, Vector3>()).MeshObject + 8] ^= 1;
                    bool metadataRefused = false;
                    try { nav.ApplyInteriorCook(tampered); }
                    catch (InvalidDataException) { metadataRefused = true; }
                    check(name + " native response metadata whitelist", metadataRefused && nav.ToBytes().SequenceEqual(before));
                    check(name + " native source archive untouched", File.ReadAllBytes(archive.FullName).SequenceEqual(archiveBefore));
                }
                finally { foreach (var (path, bytes) in snapshot) File.WriteAllBytes(path, bytes); }
            }
            var mismatch = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_loc_traindepot_general.sds"));
            if (!mismatch.Exists) return;
            var refusedDocument = new CompiledSceneDocument(mismatch);
            byte[] source = refusedDocument.NavigationMeshes.Single().ToBytes();
            var refusedResult = await refusedDocument.DeformNavigationInteriorAsync(0, new Dictionary<int, Vector3>(), copiedExecutable);
            check("native graph baseline mismatch refuses unchanged", refusedResult.Edit is null &&
                refusedResult.Refusal?.Contains("baseline", StringComparison.Ordinal) == true &&
                refusedDocument.NavigationMeshes.Single().ToBytes().SequenceEqual(source));
        }).GetAwaiter().GetResult();
    }
}
