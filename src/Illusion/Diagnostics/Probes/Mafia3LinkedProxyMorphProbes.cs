using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestPreparedProxyMorph(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_french_east_block_b_lod0.sds"));
        var sourceArchive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city_crash", "bc_city_crash.sds"));
        if (!archive.Exists || !sourceArchive.Exists) return;
        string folder = SdsMeshLoader.EnsureExtracted(sourceArchive), source = Path.Combine(folder, "Generic_3379_00689871E06C2C3D.ires.compiled");
        if (!File.Exists(source)) return;
        var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
        var document = new CompiledSceneDocument(archive); if (document.PreparedLinkedProxyGroups.Count != 0) return;
        static IEnumerable<ISceneHierarchyNode> Walk(IEnumerable<ISceneHierarchyNode> rows)
        { foreach (var row in rows) { yield return row; foreach (var child in Walk(row.Children)) yield return child; } }
        var owner = Walk(document.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().First(frame => frame.SceneIdentity == "guid:0F947BBBC7BC8574");
        var files = document.Graph.Resources.Select(resource => Path.GetDirectoryName(resource.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(Directory.GetFiles)
            .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)), StringComparer.OrdinalIgnoreCase);
        var plan = document.InspectLinkedProxyPreparation(owner, out var refusal) ?? throw new InvalidDataException(refusal);
        var preparation = document.PrepareLinkedProxyTransforms(plan, out refusal) ?? throw new InvalidDataException(refusal);
        IFrameDuplication? group = null, imported = null;
        try
        {
            group = document.CreatePreparedLinkedProxyGroup(owner, "native_morph_probe", out refusal) ?? throw new InvalidDataException(refusal);
            var dynamicPath = Directory.GetFiles(folder, "Generic_3380_*.ires.compiled").Single();
            var dynamicGraph = new CompiledGraph([CompiledResource.Load(dynamicPath)]);
            var dynamicObject = dynamicGraph.Objects[0x155D0F9770A0814C];
            var beforeRefusal = document.Graph.Resources.ToDictionary(resource => resource, resource => (SHA: SHA256.HashData(resource.ToArray()), resource.Revision));
            int objects = document.ObjectCount, meshes = document.Meshes.Count;
            check("prepared morph import refuses actual gameplay physics dependencies without mutating prior native state",
                dynamicGraph.Items(dynamicGraph.Field(dynamicObject, "m_Components")).Select(dynamicGraph.Resolve).Any(component => component?.TypeId is not (0x0fa73185 or 0x2da22400))
                && document.ImportPreparedMorphProp(dynamicPath, dynamicObject.ObjectId, owner, (IFrameNode)group.Root.Source!, out _) is null
                && document.ObjectCount == objects && document.Meshes.Count == meshes
                && beforeRefusal.All(pair => pair.Key.Revision == pair.Value.Revision && SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value.SHA)));
            imported = document.ImportPreparedMorphProp(source, 0x155D0F95DB818728, owner, (IFrameNode)group.Root.Source!, out refusal) ?? throw new InvalidDataException(refusal);
            var fine = (CompiledSceneDocument.CompiledFrameNode)imported.Root.Source!; var far = (CompiledSceneDocument.CompiledFrameNode)imported.AdditionalRoots.Single().Source!;
            var graph = document.Graph;
            CompiledMeshBinding MorphBinding(ISceneHierarchyNode root) => Walk(new[] { root }).Select(row => row.GeometrySource ?? row.Source).OfType<ICompiledMeshSource>()
                .Select(source => source.GeometryBinding).OfType<CompiledMeshBinding>().First(binding => graph.Resolve(graph.Field(graph.Resolve(graph.Field(binding.Visual, "m_MeshSet")), "m_MorphTextureData")) is not null);
            var nearBinding = MorphBinding(imported.Root); var farBinding = MorphBinding(imported.AdditionalRoots.Single());
            var nearChannels = document.MorphChannels(nearBinding, 0); var farChannels = document.MorphChannels(farBinding, 0);
            CompiledElement Set(CompiledMeshBinding binding) => graph.Resolve(graph.Field(binding.Visual, "m_MeshSet"))!;
            CompiledElement Texture(CompiledMeshBinding binding) => graph.Resolve(graph.Field(Set(binding), "m_MorphTextureData"))!;
            var nearTexture = Texture(nearBinding); var farTexture = Texture(farBinding);
            check("prepared morph import owns both complete two-visual rigid seven-bone props", document.VehicleRig(fine)!.Count == 7 && document.VehicleRig(far)!.Count == 7
                && graph.Items(graph.Field(graph.Items(graph.Field(fine.Element, "m_Components")).Select(graph.Resolve).Single(component => component?.TypeId == 0x2da22400), "m_Visuals")).Count == 2
                && nearChannels.TargetIds.SequenceEqual(farChannels.TargetIds));
            check("prepared morph copies independent native texture cache identities and exact sparse payloads", nearTexture.ObjectId != farTexture.ObjectId
                && graph.Integer(graph.Field(Set(nearBinding), "m_MorphMapHash")!) != 0x0083C7E98D871A16
                && graph.Integer(graph.Field(Set(nearBinding), "m_MorphMapHash")!) != graph.Integer(graph.Field(Set(farBinding), "m_MorphMapHash")!)
                && graph.Data(graph.Field(nearTexture, "m_Data")!).SequenceEqual(graph.Data(graph.Field(farTexture, "m_Data")!)));
            bool contained = true; var bind = nearBinding.GeometryBind!.Value;
            var data = graph.Data(graph.Field(nearBinding.Visual, "m_Data")!); var min = CompiledMeshDecoder.Vector(data[16..]); var max = CompiledMeshDecoder.Vector(data[28..]);
            Matrix4x4.Invert(bind, out var inverse); var zero = nearBinding.DecodeLod(0)!;
            for (int mask = 0; mask < 16; mask++)
            {
                var weights = Enumerable.Range(0, 4).ToDictionary(target => target, target => (mask & (1 << target)) == 0 ? 0f : 1f);
                var near = document.PreviewMorph(nearBinding, 0, weights); var coarse = document.PreviewMorph(farBinding, 0, weights);
                contained &= near.Positions.SequenceEqual(coarse.Positions) && near.Normals.SequenceEqual(coarse.Normals);
                foreach (uint vertex in near.Indices.Distinct())
                {
                    var point = Vector3.Transform(near.Positions[vertex], inverse);
                    contained &= point.X >= min.X - 1e-5f && point.Y >= min.Y - 1e-5f && point.Z >= min.Z - 1e-5f
                        && point.X <= max.X + 1e-5f && point.Y <= max.Y + 1e-5f && point.Z <= max.Z + 1e-5f;
                }
            }
            check("copied native morph envelopes contain every extreme of the four-target manual domain", contained && nearChannels.Vertices.Length == zero.VertexCount);
            bool overshoot = false; try { _ = document.PreviewAnimatedMorph(nearBinding, 0, new Dictionary<int, float> { [0] = 1.5f }); } catch (NotSupportedException) { overshoot = true; }
            check("prepared morph playback refuses weights outside its proven envelope domain", overshoot);
            check("prepared morph target inspection resolves both canonical visual bindings", document.GetPreparedMorphBindings(nearBinding, out refusal).SequenceEqual(new[] { nearBinding, farBinding }));
            var cache = (System.Collections.IDictionary)typeof(CompiledSceneDocument).GetField("_morphChannelsCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(document)!;
            int cached = cache.Count;
            for (int index = 0; index < 10; index++) _ = document.GetPreparedMorphBindings(nearBinding, out _);
            check("prepared morph contract inspection does not retain temporary preview bindings", cache.Count == cached);
            document.SaveWorkingCopy(); var reopened = new CompiledSceneDocument(archive);
            check("prepared morph native resources and mapping independently reopen", reopened.LinkedProxyMappingWarnings.Count == 0
                && Walk(reopened.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().Any(frame => frame.SceneIdentity == fine.SceneIdentity));
            string packed = Path.Combine(MafiaEnvironment.GameRoot, "morph-probe-" + Guid.NewGuid().ToString("N"), "city", Path.GetFileName(plan.DestinationArchive));
            Directory.CreateDirectory(Path.GetDirectoryName(packed)!);
            using (var stream = File.Create(packed)) SdsArchive.Pack(MafiaEnvironment.ExtractedDir(new FileInfo(plan.DestinationArchive)), MafiaEnvironment.Profile).Save(stream, new SdsWriteOptions { Compress = true });
            var farOnly = new CompiledSceneDocument(new FileInfo(packed));
            var independentRow = Walk(farOnly.Roots).First(row => row.Source is CompiledSceneDocument.CompiledFrameNode frame && frame.SceneIdentity == far.SceneIdentity);
            var independentBinding = Walk(new[] { independentRow }).Select(row => row.GeometrySource ?? row.Source).OfType<ICompiledMeshSource>().Select(source => source.GeometryBinding).OfType<CompiledMeshBinding>()
                .First(binding => farOnly.Graph.Resolve(farOnly.Graph.Field(farOnly.Graph.Resolve(farOnly.Graph.Field(binding.Visual, "m_MeshSet")), "m_MorphTextureData")) is not null);
            var weightsForFresh = new Dictionary<int, float> { [0] = 1, [1] = .5f, [3] = .7f };
            var expected = document.PreviewMorph(farBinding, 0, weightsForFresh); var actual = farOnly.PreviewMorph(independentBinding, 0, weightsForFresh);
            check("compressed far-only morph prop resolves its owned atlas correction table and native geometry", actual.Positions.SequenceEqual(expected.Positions)
                && actual.Normals.SequenceEqual(expected.Normals) && farOnly.MorphChannels(independentBinding, 0).TargetIds.SequenceEqual(nearChannels.TargetIds));
            imported.Detach(); imported.Reattach(); imported.Detach(); imported = null; group.Detach(); group = null; preparation.Undo(); document.SaveWorkingCopy();
            check("prepared morph import redo undo restores source templates and every prior working file", SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(sourceHash)
                && files.All(pair => File.Exists(pair.Key) && SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)));
        }
        finally { imported?.Detach(); group?.Detach(); if (document.PreparedLinkedProxyGroups.Count > 0) preparation.Undo(); document.SaveWorkingCopy(); }
    }
}
