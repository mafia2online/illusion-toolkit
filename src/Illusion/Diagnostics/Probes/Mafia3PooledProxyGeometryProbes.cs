using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledPooledProxyGeometry(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_french_east_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive); var originalDocument = document;
        if (document.PreparedLinkedProxyGroups.Count != 0) return;
        var graph = document.Graph;
        var frame = EntityCreationRows(document.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .First(owner => owner.SceneIdentity == "guid:0F947BBBC7BC8574");
        CompiledMeshBinding Binding(IFrameNode owner, CompiledSceneDocument? doc = null) => EntityCreationRows((doc ?? document).Roots)
            .Select(row => (row.GeometrySource ?? row.Source) as ICompiledMeshSource)
            .First(item => item?.GeometryBinding is not null && ReferenceEquals(item.CanonicalFrame, owner)
                && item.SceneIdentity.EndsWith(":visual:1", StringComparison.Ordinal))!.GeometryBinding!;
        var source = Binding(frame);
        var set = graph.Resolve(graph.Field(source.Visual, "m_MeshSet"));
        var mesh = graph.Resolve(graph.Field(graph.Items(graph.Field(set, "m_LODs"))[source.LodCount - 1], "m_Mesh"));
        var wrapper = graph.Items(graph.Field(mesh, "m_VertexWrapper"))[0];
        var buffer = graph.Resolve(graph.Field(wrapper, "m_Buffer"))!;
        var field = graph.Field(buffer, "m_Data")!; byte[] original = graph.Data(field).ToArray();
        var offsets = graph.Nodes.SelectMany(node => graph.Items(graph.Field(node, "m_VertexWrapper")))
            .Where(row => ReferenceEquals(graph.Resolve(graph.Field(row, "m_Buffer")), buffer))
            .Select(row => graph.Field(row, "m_Offset")!).Distinct().ToDictionary(value => value, value => graph.Data(value).ToArray());
        var files = graph.Resources.Select(resource => Path.GetDirectoryName(resource.Path)!).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(Directory.GetFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        IEditAction? preparation = null; Illusion.Assets.Bridge.BridgeMeshApplier.ApplyResult? edit = null;
        try
        {
            var pooled = Enumerable.Repeat((byte)0xA5, original.Length + 80).ToArray(); original.CopyTo(pooled, 32);
            graph.Owner(field).ReplaceVariable(field, pooled);
            foreach (var item in offsets)
            {
                var bytes = item.Value.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(bytes,
                    checked(BinaryPrimitives.ReadUInt32LittleEndian(bytes) + 32)); graph.Owner(item.Key).Replace(item.Key, bytes);
            }
            var poolBaseline = graph.Data(field).ToArray();
            document.SaveWorkingCopy(); document = new CompiledSceneDocument(archive);
            frame = EntityCreationRows(document.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
                .First(owner => owner.SceneIdentity == "guid:0F947BBBC7BC8574");
            source = Binding(frame);
            var activePool = document.Graph.Field(document.Graph.Objects[buffer.ObjectId], "m_Data")!;
            var plan = document.InspectLinkedProxyPreparation(frame, out var reason) ?? throw new InvalidDataException(reason);
            preparation = document.PrepareLinkedProxyTransforms(plan, out reason) ?? throw new InvalidDataException(reason);
            int lod = source.LodCount - 1;
            var payload = source.Export(document, lod, out reason) ?? throw new InvalidDataException(reason);
            int faces = payload.FaceMaterials.Length; PokeProxyTriangle(payload);
            edit = source.TryApply(payload, lod, out reason) ?? throw new InvalidDataException(reason); edit.ApplyNew();
            check("pooled proxy replacement isolates native byte ranges and preserves original pool/alias offsets",
                document.Graph.Data(activePool).SequenceEqual(poolBaseline) && offsets.All(item => BinaryPrimitives.ReadUInt32LittleEndian(graph.Data(item.Key))
                    == BinaryPrimitives.ReadUInt32LittleEndian(item.Value) + 32) && source.DecodeLod(lod)!.Indices.Length / 3 == faces + 2);
            document.SaveWorkingCopy();
            var reload = new CompiledSceneDocument(archive);
            check("pooled proxy topology saves and reopens with validated native mapping", reload.PreparedLinkedProxyGroups.Count == 1 && reload.LinkedProxyMappingWarnings.Count == 0);
            string folder = Path.Combine(".scratch", "pooled-proxy-far-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            var farArchive = new FileInfo(Path.Combine(folder, Path.GetFileName(plan.DestinationArchive)));
            using (var output = farArchive.Create()) SdsArchive.Pack(MafiaEnvironment.ExtractedDir(new FileInfo(plan.DestinationArchive)), MafiaEnvironment.Profile)
                .Save(output, new SdsWriteOptions { Compress = true });
            var farOnly = new CompiledSceneDocument(farArchive);
            check("pooled proxy replacement retains self-contained compressed far geometry", farOnly.Meshes.Any(value => value.Indices.Length / 3 == faces + 2));
        }
        finally
        {
            edit?.RestoreOriginal(); preparation?.Undo(); document.SaveWorkingCopy();
            graph.Owner(field).ReplaceVariable(field, original);
            foreach (var item in offsets) graph.Owner(item.Key).Replace(item.Key, item.Value);
            originalDocument.SaveWorkingCopy();
        }
        check("pooled proxy test restores every copied resource and manifest byte exactly", files.All(item => SHA256.HashData(File.ReadAllBytes(item.Key)).SequenceEqual(item.Value)));
    }
}
