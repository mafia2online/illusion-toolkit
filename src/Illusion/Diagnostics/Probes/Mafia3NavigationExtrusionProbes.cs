using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Navigation;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledNavigationExtrusion(Action<string, bool> check)
    {
        string? executable = Environment.GetEnvironmentVariable("ILLUSION_MAFIA3_COOK_EXE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;
        Task.Run(async () =>
        {
            foreach (var (name, edge, position) in new[] {
                ("bc_loc_quarry_dummy_general.sds", 41, (Vector3?)null),
                ("bc_loc_sammys_club_dummy_general.sds", 8, (Vector3?)null),
                ("bc_easthighland_general.sds", 41, (Vector3?)new Vector3(2700.2498f,681.6772f,-324.06537f)),
                ("bc_magnolia_unique_church_a_general.sds", 70, (Vector3?)new Vector3(1171.2325f,471.82602f,34.130882f)) })
            {
                var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, name)); if (!archive.Exists) continue;
                var document = new CompiledSceneDocument(archive); var nav = document.NavigationMeshes.Single(); var mesh = nav.Meshes.Single();
                string folder = MafiaEnvironment.ExtractedDir(archive);
                var snapshot = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
                byte[] source = nav.ToBytes(), archiveSource = File.ReadAllBytes(archive.FullName);
                int vertices = mesh.Vertices.Count, faces = mesh.FaceCount, edges = mesh.EdgeCount;
                var indices = mesh.TriangleIndices.ToArray(); double originalArea = NavigationArea(mesh);
                try
                {
                    var candidate = nav.BoundaryExtrusionCandidates(128).FirstOrDefault(c => c.Edge == edge);
                    check(name + " native extrusion candidates are read-only", candidate is not null && source.SequenceEqual(nav.ToBytes()));
                    if (candidate is null) throw new InvalidDataException("Expected copied extrusion candidate is unavailable.");
                    var plan = nav.PrepareInteriorDeformation(new Dictionary<int, Vector3>());
                    int a = BitConverter.ToInt32(source, plan.Edges + edge * 20), b = BitConverter.ToInt32(source, plan.Edges + edge * 20 + 4);
                    var midpoint = (mesh.Vertices[a] + mesh.Vertices[b]) / 2;
                    var overlap = position is null ? midpoint + Vector3.Normalize(candidate.Position - midpoint) * .5f : mesh.Vertices[a];
                    var refused = await document.ExtrudeNavigationBoundaryAsync(0, edge, overlap, executable);
                    check(name + " overlapping or collapsed extrusion refuses unchanged", refused.Edit is null &&
                        (position is not null || refused.Refusal?.Contains("overlap", StringComparison.OrdinalIgnoreCase) == true) && nav.ToBytes().SequenceEqual(source));
                    var target = position ?? candidate.Position;
                    var result = await document.ExtrudeNavigationBoundaryAsync(0, edge, target, executable);
                    var edit = result.Edit ?? throw new InvalidOperationException(result.Refusal); byte[] cooked = nav.ToBytes();
                    check(name + " native extrusion adds real connected geometry", mesh.Vertices.Count == vertices + 1 && mesh.FaceCount == faces + 1 && mesh.EdgeCount == edges + 3);
                    check(name + " native extrusion grows walkable area and preserves original triangles", NavigationArea(mesh) > originalArea && mesh.TriangleIndices.Take(indices.Length).SequenceEqual(indices));
                    if(position is not null)
                    {
                        Vector3 Read(byte[] bytes,int at)=>new(BitConverter.ToSingle(bytes,at),BitConverter.ToSingle(bytes,at+4),BitConverter.ToSingle(bytes,at+8));
                        var oldMin=Read(source,plan.MeshObject+128);var oldMax=Read(source,plan.MeshObject+144);
                        check(name+" isolated extrusion expands original section bounds exactly",(target.X<oldMin.X||target.Y<oldMin.Y||target.Z<oldMin.Z||target.X>oldMax.X||target.Y>oldMax.Y||target.Z>oldMax.Z)&&
                            Read(cooked,plan.MeshObject+128)==Vector3.Min(oldMin,target)&&Read(cooked,plan.MeshObject+144)==Vector3.Max(oldMax,target));
                    }
                    document.SaveWorkingCopy();
                    check(name + " native extrusion fresh document", new CompiledSceneDocument(archive).NavigationMeshes.Single().ToBytes().SequenceEqual(cooked));
                    using (var stream = new MemoryStream())
                    {
                        SdsArchive.Pack(folder, MafiaEnvironment.Profile).Save(stream, new SdsWriteOptions { Compress = true });
                        stream.Position = 0; SdsArchive.Load(stream).Extract(folder);
                    }
                    check(name + " native extrusion compressed fresh extraction", new CompiledSceneDocument(archive).NavigationMeshes.Single().ToBytes().SequenceEqual(cooked));
                    var noop = await NativeNavigationCooker.CookAsync(cooked, new Dictionary<int, Vector3>(), executable);
                    check(name + " expanded native graph baseline reproduces exactly", noop.SequenceEqual(cooked));
                    edit.Undo(); document.SaveWorkingCopy();
                    check(name + " native extrusion exact resources and manifest undo", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)) && mesh.Vertices.Count == vertices && mesh.FaceCount == faces);
                    byte[] tampered = cooked.ToArray(); tampered[nav.PrepareBoundaryExtrusion(edge, target).MeshObject + 8] ^= 1;
                    bool metadataRefused = false; try { nav.ApplyBoundaryExtrusionCook(edge, tampered, target); } catch (InvalidDataException) { metadataRefused = true; }
                    check(name + " native extrusion metadata whitelist", metadataRefused && nav.ToBytes().SequenceEqual(source));
                    edit.Redo(); document.SaveWorkingCopy();
                    check(name + " native extrusion redo reload", new CompiledSceneDocument(archive).NavigationMeshes.Single().ToBytes().SequenceEqual(cooked));
                    edit.Undo(); document.SaveWorkingCopy();
                    check(name + " native extrusion source archive untouched", File.ReadAllBytes(archive.FullName).SequenceEqual(archiveSource));
                }
                finally { foreach (var (path, bytes) in snapshot) File.WriteAllBytes(path, bytes); }
            }
        }).GetAwaiter().GetResult();
    }
    private static double NavigationArea(Mafia3NavigationMesh mesh)
    {
        double area = 0;
        for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
        {
            var a = mesh.Vertices[(int)mesh.TriangleIndices[i]]; var b = mesh.Vertices[(int)mesh.TriangleIndices[i + 1]]; var c = mesh.Vertices[(int)mesh.TriangleIndices[i + 2]];
            area += Math.Abs(((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X)) * .5;
        }
        return area;
    }
}
