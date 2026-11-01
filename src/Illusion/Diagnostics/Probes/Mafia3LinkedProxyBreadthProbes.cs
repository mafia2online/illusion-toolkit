using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledLinkedProxyBreadth(Action<string, bool> check)
    {
        foreach (var scenario in new[] { ("bc_bridge_a", 7, 7), ("bc_downtown_unique_bridge_b", 1, 6) })
        {
            var originals = Enumerable.Range(0, 3).Select(lod => new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, scenario.Item1 + $"_lod{lod}.sds"))).ToArray();
            if (originals.Any(file => !file.Exists)) continue;
            string unique = "proxy-breadth-probe-" + Guid.NewGuid().ToString("N");
            string fixture = Path.Combine(MafiaEnvironment.GameRoot, unique), mirror = Path.Combine(MafiaEnvironment.ResourcesFolder!, unique);
            Directory.CreateDirectory(Path.Combine(fixture, "city")); foreach (var file in originals) File.Copy(file.FullName, Path.Combine(fixture, "city", file.Name));
            var archiveHashes = originals.Select(file => file.FullName).Concat(Directory.GetFiles(Path.Combine(fixture, "city"), "*.sds"))
                .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
            string? mapping = null; byte[]? oldMapping = null;
            try
            {
                var archive = new FileInfo(Path.Combine(fixture, "city", originals[0].Name)); var document = new CompiledSceneDocument(archive);
                static IEnumerable<SdsFrameNode> Walk(IEnumerable<SdsFrameNode> rows)
                { foreach (var row in rows) { yield return row; foreach (var child in Walk(row.Children)) yield return child; } }
                var native = document.NativeLods!;
                var frame = Walk(document.Roots).Where(row => row.Mesh?.NativeLod is { } a && native.Groups[a.Group].Parent >= 0
                        || row.LodMeshes.Any(mesh => mesh.NativeLod is { } b && native.Groups[b.Group].Parent >= 0))
                    .Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct().First();
                var plan = document.InspectLinkedProxyPreparation(frame, out var refusal) ?? throw new InvalidDataException(scenario.Item1 + ": " + refusal);
                check(scenario.Item1 + " nonstock typed family passes invariant-based eligibility", plan.FineFrames == scenario.Item2 && plan.AfterVisuals == scenario.Item3);
                mapping = Path.Combine(MafiaEnvironment.ResourcesFolder!, ".illusion", "linked-proxies", plan.ParentGroupId.ToString("X16") + ".json");
                if (File.Exists(mapping)) oldMapping = File.ReadAllBytes(mapping);
                var before = Directory.GetFiles(mirror, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
                var edit = document.PrepareLinkedProxyTransforms(plan, out refusal) ?? throw new InvalidDataException(refusal);
                try
                {
                    var target = document.GetLinkedProxyTransformTargets(frame).Single(); var original = frame.LocalTransform;
                    var changed = original * Matrix4x4.CreateTranslation(.25f, .5f, 1);
                    if (document.ValidateLinkedProxyTransform(frame, changed) is { } reason) throw new InvalidDataException(reason);
                    frame.LocalTransform = changed;
                    check(scenario.Item1 + " prepared transform pairs real fine and generated coarse worlds", frame.WorldTransform == target.WorldTransform);
                    document.SaveWorkingCopy();
                    var reload = new CompiledSceneDocument(archive);
                    var reopenedFine = Walk(reload.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct().Single(row => row.SceneIdentity == frame.SceneIdentity);
                    check(scenario.Item1 + " saved mapping reopens with generalized member count and paired pose", reload.PreparedLinkedProxyGroups.Count == 1 && reload.LinkedProxyMappingWarnings.Count == 0
                        && reopenedFine.WorldTransform == reload.GetLinkedProxyTransformTargets(reopenedFine).Single().WorldTransform && reopenedFine.WorldTransform == frame.WorldTransform);
                    var decoder = new CompiledMeshDecoder(document.Graph);
                    var decode = typeof(CompiledSceneDocument).GetMethod("DecodeVisual", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var expected = new List<MeshData>();
                    foreach (var fine in plan.Fine)
                        foreach (var component in document.Graph.Items(document.Graph.Field(fine.Element, "m_Components")).Select(document.Graph.Resolve).Where(component => component?.TypeId == 0x2DA22400))
                            foreach (var visual in document.Graph.Items(document.Graph.Field(component, "m_Visuals")).Select(document.Graph.Resolve).OfType<Illusion.Formats.Compiled.CompiledElement>())
                            {
                                int last = document.Graph.Items(document.Graph.Field(document.Graph.Resolve(document.Graph.Field(visual, "m_MeshSet")), "m_LODs")).Count - 1;
                                expected.Add((MeshData)decode.Invoke(document, [fine, visual, decoder, last, null, null])!);
                            }
                    var targets = plan.Fine.SelectMany(fine => document.GetLinkedProxyTransformTargets(fine)).Cast<CompiledSceneDocument.CompiledFrameNode>().Select(far => far.SceneIdentity).ToHashSet();
                    string farCity = Path.Combine(fixture, "far", "city"); Directory.CreateDirectory(farCity);
                    string farPath = Path.Combine(farCity, Path.GetFileName(plan.DestinationArchive));
                    using (var output = File.Create(farPath)) SdsArchive.Pack(MafiaEnvironment.ExtractedDir(new FileInfo(plan.DestinationArchive)), MafiaEnvironment.Profile).Save(output, new SdsWriteOptions { Compress = true });
                    var farDocument = new CompiledSceneDocument(new FileInfo(farPath));
                    var farRows = Walk(farDocument.Roots).Where(row => row.Source is CompiledSceneDocument.CompiledFrameNode far && targets.Contains(far.SceneIdentity)).ToArray();
                    var actual = farRows.SelectMany(row => row.Mesh is { } mesh ? new[] { mesh } : row.LodMeshes.AsEnumerable()).ToArray();
                    check(scenario.Item1 + " compressed far-only archive is independent of fine archive geometry", expected.Count == scenario.Item3
                        && actual.Length == expected.Count && actual.Select(ProxyBreadthSignature).Order().SequenceEqual(expected.Select(ProxyBreadthSignature).Order()));
                    var generatedFrames = farRows.Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct().ToArray();
                    check(scenario.Item1 + " every native lowest vertex and index buffer survives far-only compression byte exact", ProxyBreadthBuffers(document, plan.Fine, true)
                        .Order().SequenceEqual(ProxyBreadthBuffers(farDocument, generatedFrames, false).Order()));
                    check(scenario.Item1 + " far-only native group keeps coarse fallback without fine completer", farDocument.NativeLods is { } farGraph
                        && actual.All(mesh => mesh.NativeLod is { } member && !farGraph.Groups[member.Group].ChildrenReady && farGraph.IsVisible(member.Group, mesh.World.Translation)));
                    frame.LocalTransform = original; edit.Undo(); document.SaveWorkingCopy();
                    check(scenario.Item1 + " preparation move save and undo restore all original GENR textures and manifests", before.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
                        && !File.Exists(mapping));
                }
                finally
                {
                    // Own temporary mirror only: source archives remain read-only throughout the test.
                    foreach (var pair in before) File.WriteAllBytes(pair.Key, pair.Value);
                }
                check(scenario.Item1 + " original and copied input archive bytes remain unchanged", archiveHashes.All(pair => SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)));
            }
            finally
            {
                if (mapping is not null) { if (oldMapping is not null) File.WriteAllBytes(mapping, oldMapping); else if (File.Exists(mapping)) File.Delete(mapping); }
                string root = Path.GetFullPath(MafiaEnvironment.GameRoot) + Path.DirectorySeparatorChar;
                foreach (string path in new[] { fixture, mirror })
                    if (Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path) == unique && Directory.Exists(path)) Directory.Delete(path, true);
            }
        }
    }

    private static string ProxyBreadthSignature(MeshData mesh)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes(mesh.Positions.AsSpan())); hash.AppendData(MemoryMarshal.AsBytes(mesh.Normals.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(mesh.Indices.AsSpan())); hash.AppendData(MemoryMarshal.AsBytes(mesh.UVs.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(mesh.Tangents.AsSpan())); hash.AppendData(MemoryMarshal.AsBytes(mesh.Binormals.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(mesh.Colors.AsSpan())); hash.AppendData(MemoryMarshal.AsBytes(mesh.DecalUVs.AsSpan()));
        hash.AppendData(mesh.BoneIndices.AsSpan()); hash.AppendData(MemoryMarshal.AsBytes(mesh.BoneWeights.AsSpan()));
        var world = new[] { mesh.World }; hash.AppendData(MemoryMarshal.AsBytes(world.AsSpan()));
        foreach (var part in mesh.Parts) { hash.AppendData(BitConverter.GetBytes(part.MaterialHash)); hash.AppendData(BitConverter.GetBytes(part.StartIndex)); hash.AppendData(BitConverter.GetBytes(part.IndexCount)); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<string> ProxyBreadthBuffers(CompiledSceneDocument document, IEnumerable<CompiledSceneDocument.CompiledFrameNode> frames, bool lowest)
    {
        var graph = document.Graph; var buffers = new HashSet<Illusion.Formats.Compiled.CompiledElement>();
        foreach (var frame in frames)
            foreach (var component in graph.Items(graph.Field(frame.Element, "m_Components")).Select(graph.Resolve).Where(component => component?.TypeId == 0x2DA22400))
                foreach (var visual in graph.Items(graph.Field(component, "m_Visuals")).Select(graph.Resolve))
                {
                    var levels = graph.Items(graph.Field(graph.Resolve(graph.Field(visual, "m_MeshSet")), "m_LODs"));
                    var mesh = graph.Resolve(graph.Field(levels[lowest ? levels.Count - 1 : 0], "m_Mesh"));
                    var index = graph.Resolve(graph.Field(graph.Field(mesh, "m_IndexWrapper"), "m_Buffer"));
                    if (index is not null) buffers.Add(index);
                    foreach (var wrapper in graph.Items(graph.Field(mesh, "m_VertexWrapper")))
                        if (graph.Resolve(graph.Field(wrapper, "m_Buffer")) is { } buffer) buffers.Add(buffer);
                }
        return buffers.Select(buffer => Convert.ToHexString(SHA256.HashData(graph.Data(graph.Field(buffer, "m_Data")!))));
    }
}
