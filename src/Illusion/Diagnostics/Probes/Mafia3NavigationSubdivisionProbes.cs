using System.IO;
using System.Numerics;
using System.Xml.Linq;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledNavigationSubdivision(Action<string, bool> check)
    {
        string? executable = Environment.GetEnvironmentVariable("ILLUSION_MAFIA3_COOK_EXE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;
        Task.Run(async () =>
        {
            foreach (var (name, face) in new[] { ("bc_loc_quarry_dummy_general.sds", 2), ("bc_loc_sammys_club_dummy_general.sds", 1) })
            {
                var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, name)); if (!archive.Exists) continue;
                var document = new CompiledSceneDocument(archive); var nav = document.NavigationMeshes.Single(); var mesh = nav.Meshes.Single();
                string folder = MafiaEnvironment.ExtractedDir(archive), manifest = Path.Combine(folder, "SDSContent.xml");
                var snapshot = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
                byte[] source = nav.ToBytes(), archiveBefore = File.ReadAllBytes(archive.FullName);
                int vertices = mesh.Vertices.Count, faces = mesh.FaceCount, edges = mesh.EdgeCount;
                int[] protectedVertices = nav.PrepareInteriorDeformation(new Dictionary<int, Vector3>()).BoundaryVertices;
                var faceData = Enumerable.Range(0, mesh.FaceCount).SelectMany(f => Enumerable.Range(0, mesh.FaceDataStride).Select(c => mesh.GetFaceData(f, c))).ToArray();
                var edgeData = Enumerable.Range(0, mesh.EdgeCount).SelectMany(e => Enumerable.Range(0, mesh.EdgeDataStride).Select(c => mesh.GetEdgeData(e, c))).ToArray();
                uint ram = (uint)XElement.Load(manifest).Attribute("SlotRam")!;
                try
                {
                    check(name + " subdivision candidates are read-only", nav.FaceSubdivisionCandidates().Contains(face) && nav.ToBytes().SequenceEqual(source));
                    var result = await document.SubdivideNavigationFaceAsync(0, face, copiedExecutablePath: executable);
                    var edit = result.Edit ?? throw new InvalidOperationException(result.Refusal); byte[] cooked = nav.ToBytes();
                    check(name + " native subdivision grows real geometry and preserves canonical mesh", ReferenceEquals(mesh, nav.Meshes.Single()) &&
                        mesh.Vertices.Count == vertices + 1 && mesh.FaceCount == faces + 2 && mesh.EdgeCount == edges + 6);
                    check(name + " subdivision preserves boundary vertex identities", nav.PrepareInteriorDeformation(new Dictionary<int, Vector3>()).BoundaryVertices.SequenceEqual(protectedVertices));
                    check(name + " subdivision preserves existing face and edge user data", faceData.SequenceEqual(Enumerable.Range(0, faces).SelectMany(f => Enumerable.Range(0, mesh.FaceDataStride).Select(c => mesh.GetFaceData(f, c)))) &&
                        edgeData.SequenceEqual(Enumerable.Range(0, edges).SelectMany(e => Enumerable.Range(0, mesh.EdgeDataStride).Select(c => mesh.GetEdgeData(e, c)))));
                    document.SaveWorkingCopy();
                    var metadata = XElement.Load(manifest);
                    check(name + " subdivision updates exact native archive RAM delta", (uint)metadata.Attribute("SlotRam")! == ram + cooked.Length - source.Length);
                    var reload = new CompiledSceneDocument(archive).NavigationMeshes.Single();
                    check(name + " subdivision fresh document reproduces all bytes", reload.ToBytes().SequenceEqual(cooked));
                    using (var stream = new MemoryStream())
                    {
                        var packed = SdsArchive.Pack(folder, MafiaEnvironment.Profile); packed.Save(stream, new SdsWriteOptions { Compress = true });
                        stream.Position = 0; var fresh = SdsArchive.Load(stream);
                        check(name + " subdivision compressed SDS carries enlarged RAM budget", fresh.SlotRamRequired == ram + cooked.Length - source.Length);
                        fresh.Extract(folder);
                    }
                    check(name + " subdivision compressed fresh extraction reload", new CompiledSceneDocument(archive).NavigationMeshes.Single().ToBytes().SequenceEqual(cooked));
                    var noop = await NativeNavigationCooker.CookAsync(cooked, new Dictionary<int, Vector3>(), executable);
                    check(name + " newly subdivided native graph baseline reproduces exactly", noop.SequenceEqual(cooked));
                    edit.Undo(); document.SaveWorkingCopy();
                    check(name + " subdivision exact resources and manifest undo", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)) && mesh.Vertices.Count == vertices && mesh.FaceCount == faces && mesh.EdgeCount == edges);
                    byte[] tampered = cooked.ToArray(); tampered[nav.PrepareFaceSubdivision(face).MeshObject + 8] ^= 1;
                    bool refused = false; try { nav.ApplyFaceSubdivisionCook(face, tampered); } catch (InvalidDataException) { refused = true; }
                    check(name + " subdivision response metadata whitelist", refused && nav.ToBytes().SequenceEqual(source));
                    var outside = await document.SubdivideNavigationFaceAsync(0, face, new Vector3(float.MaxValue), executable);
                    check(name + " subdivision invalid point refuses unchanged", outside.Edit is null && nav.ToBytes().SequenceEqual(source));
                    edit.Redo(); document.SaveWorkingCopy();
                    check(name + " subdivision redo fresh reload", new CompiledSceneDocument(archive).NavigationMeshes.Single().ToBytes().SequenceEqual(cooked));
                    edit.Undo(); document.SaveWorkingCopy();
                    check(name + " subdivision original SDS remains untouched", File.ReadAllBytes(archive.FullName).SequenceEqual(archiveBefore));
                }
                finally { foreach (var (path, bytes) in snapshot) File.WriteAllBytes(path, bytes); }
            }
        }).GetAwaiter().GetResult();
    }
}
