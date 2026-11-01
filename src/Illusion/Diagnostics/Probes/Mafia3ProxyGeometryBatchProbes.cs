using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledProxyGeometryBatch(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_french_east_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive); if (document.PreparedLinkedProxyGroups.Count != 0) return;
        var owner = EntityCreationRows(document.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .First(frame => frame.SceneIdentity == "guid:0F947BBBC7BC8574");
        var files = document.Graph.Resources.Select(resource => Path.GetDirectoryName(resource.Path)!).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(Directory.GetFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        var plan = document.InspectLinkedProxyPreparation(owner, out var reason) ?? throw new InvalidDataException(reason);
        var preparation = document.PrepareLinkedProxyTransforms(plan, out reason) ?? throw new InvalidDataException(reason);
        var bindings = EntityCreationRows(document.Roots).Select(row => (row.GeometrySource ?? row.Source) as ICompiledMeshSource)
            .Where(source => source?.GeometryBinding is not null && ReferenceEquals(source.CanonicalFrame, owner))
            .OrderBy(source => source!.SceneIdentity, StringComparer.Ordinal).Select(source => source!.GeometryBinding!).Take(2).ToArray();
        var requests = bindings.Select((binding, index) =>
        {
            int lod = binding.LodCount - 1;
            var payload = binding.Export(document, lod, out var error) ?? throw new InvalidDataException(error);
            for (int vertex = 0; vertex < payload.Positions.Length; vertex++) payload.Positions[vertex] += new Vector3(12 + index, 0, 0);
            PokeProxyTriangle(payload); return new CompiledLinkedGeometryRequest(binding, payload, lod);
        }).ToArray();
        var baseline = document.Graph.Resources.ToDictionary(resource => resource, resource => (resource.Revision, Bytes: SHA256.HashData(resource.ToArray())));
        bool Exact() => baseline.All(pair => pair.Key.Revision == pair.Value.Revision && SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value.Bytes));
        CompiledLinkedGeometryBatch? batch = null; bool applied = false;
        try
        {
            var malformed = requests[1].Payload.LoopVertexIndices; requests[1].Payload.LoopVertexIndices = [uint.MaxValue, 0, 1];
            var refused = document.PlanLinkedProxyGeometryBatch(requests, out reason);
            requests[1].Payload.LoopVertexIndices = malformed;
            check("paired geometry batch malformed second replacement restores first staging bytes/revisions", refused is null && Exact());
            batch = document.PlanLinkedProxyGeometryBatch(requests, out reason) ?? throw new InvalidDataException(reason);
            check("paired geometry batch stages two same-family XYZ/topology outputs without native mutations", batch.Results.Count == 2 && Exact());
            batch.Edit.Redo(); applied = true;
            check("paired geometry batch applies both native meshes and regenerated LOWEST counterparts", batch.Results.All(result => result.Requantized)
                && batch.Results[0].AdditionalCompiledLodMeshes.Count > 0 && document.ValidateLinkedProxyTransform(owner, owner.LocalTransform) is null);
            document.SaveWorkingCopy();
            var reload = new CompiledSceneDocument(archive);
            check("paired geometry batch save/reopen preserves native bounds and validated paired mapping", reload.PreparedLinkedProxyGroups.Count == 1 && reload.LinkedProxyMappingWarnings.Count == 0);
            batch.Edit.Undo(); applied = false;
            check("paired geometry batch undo restores all baseline native resource bytes", baseline.All(pair => SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value.Bytes)));
            batch.Edit.Redo(); applied = true; batch.Edit.Undo(); applied = false;
            check("paired geometry batch redo/undo remains replayable", baseline.All(pair => SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value.Bytes)));
        }
        finally
        {
            if (applied) batch!.Edit.Undo(); preparation.Undo(); document.SaveWorkingCopy();
        }
        check("paired geometry batch restores every copied resource/manifest after save and undo", files.All(pair => SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)));
    }
}
