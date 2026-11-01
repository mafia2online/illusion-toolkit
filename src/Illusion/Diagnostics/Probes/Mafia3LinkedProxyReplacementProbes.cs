using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Bridge;
using Illusion.Assets.Compiled;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledLinkedProxyReplacement(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_french_east_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive); if (document.PreparedLinkedProxyGroups.Count != 0) return;
        var frame = EntityCreationRows(document.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .First(owner => owner.SceneIdentity == "guid:0F947BBBC7BC8574");
        var files = document.Graph.Resources.Select(resource => Path.GetDirectoryName(resource.Path)!).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(Directory.GetFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        bool FilesExact() => files.All(pair => File.Exists(pair.Key) && SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value));
        var plan = document.InspectLinkedProxyPreparation(frame, out var refusal) ?? throw new InvalidDataException(refusal);
        var preparation = document.PrepareLinkedProxyTransforms(plan, out refusal) ?? throw new InvalidDataException(refusal);
        var farFrame = document.GetLinkedProxyTransformTargets(frame).Single();
        CompiledMeshBinding Binding(IFrameNode owner, int ordinal, CompiledSceneDocument? source = null) => EntityCreationRows((source ?? document).Roots)
            .Select(row => (row.GeometrySource ?? row.Source) as ICompiledMeshSource)
            .First(item => item?.GeometryBinding is not null && item.CanonicalFrame is CompiledSceneDocument.CompiledFrameNode native
                && native.SceneIdentity == ((ICompiledMeshSource)owner).SceneIdentity && item.SceneIdentity.EndsWith($":visual:{ordinal}", StringComparison.Ordinal))!.GeometryBinding!;
        MeshObjectPayload Export(CompiledMeshBinding binding, int lod) => binding.Export(document, lod, out var error) ?? throw new InvalidDataException(error);
        bool SameCorners(CompiledMeshBinding a, int level, CompiledMeshBinding b) => SameProxyReplacementCorners(a.DecodeLod(level)!, b.DecodeLod(0)!);
        BridgeMeshApplier.ApplyResult? applied = null; IEditAction? unique = null;
        try
        {
            var source = Binding(frame, 1); var far = Binding(farFrame, 1); int lowest = source.LodCount - 1;
            var position = Export(source, 0); for (int index = 0; index < position.Positions.Length; index++) position.Positions[index] += new Vector3(12, 0, 0);
            var before = document.Graph.Resources.ToDictionary(resource => resource, resource => (resource.Revision, Bytes: SHA256.HashData(resource.ToArray())));
            long boundsRevision = document.NativeLods!.BoundsRevision;
            applied = source.TryApply(position, 0, out refusal) ?? throw new InvalidDataException(refusal);
            check("expanded geometry preflight allocates no active native writes or files", before.All(pair => pair.Key.Revision == pair.Value.Revision
                && SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value.Bytes)) && document.NativeLods.BoundsRevision == boundsRevision && FilesExact());
            applied.ApplyNew();
            check("higher LOD position expansion requantizes all fine levels and regenerates far LOWEST", applied.Requantized
                && applied.AdditionalCompiledLodMeshes.Any(mesh => mesh.Lod == lowest && ReferenceEquals(mesh.Binding.Visual, source.Visual))
                && SameCorners(source, lowest, far));
            document.SaveWorkingCopy(); var reloaded = new CompiledSceneDocument(archive);
            check("expanded compression spatial metadata and rigid envelopes save with valid reopened proxy mapping", reloaded.PreparedLinkedProxyGroups.Count == 1
                && reloaded.LinkedProxyMappingWarnings.Count == 0 && SameCorners(Binding(frame, 1, reloaded), lowest, Binding(farFrame, 1, reloaded))
                && document.ValidateLinkedProxyTransform(frame, frame.LocalTransform) is null);
            applied.RestoreOriginal(); applied = null;

            var topology = Export(source, lowest); int originalVertices = topology.Positions.Length, originalFaces = topology.FaceMaterials.Length;
            PokeProxyTriangle(topology); applied = source.TryApply(topology, lowest, out refusal) ?? throw new InvalidDataException(refusal); applied.ApplyNew();
            check("real paired topology adds a vertex and two native triangles with complete far regeneration", source.DecodeLod(lowest)!.VertexCount > originalVertices
                && source.DecodeLod(lowest)!.Indices.Length / 3 == originalFaces + 2 && SameCorners(source, lowest, far));
            document.SaveWorkingCopy(); reloaded = new CompiledSceneDocument(archive);
            check("topology counts face groups CD BPA and native buffers parse after fresh reload", reloaded.PreparedLinkedProxyGroups.Count == 1
                && SameCorners(Binding(frame, 1, reloaded), lowest, Binding(farFrame, 1, reloaded)));
            string folder = Path.Combine(".scratch", "proxy-replacement-far-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            var farArchive = new FileInfo(Path.Combine(folder, Path.GetFileName(plan.DestinationArchive)));
            using (var output = farArchive.Create()) SdsArchive.Pack(MafiaEnvironment.ExtractedDir(new FileInfo(plan.DestinationArchive)), MafiaEnvironment.Profile)
                .Save(output, new SdsWriteOptions { Compress = true });
            var independent = new CompiledSceneDocument(farArchive);
            check("compressed far-only archive owns regenerated triangle topology and every native corner channel", SameCorners(source, lowest, Binding(farFrame, 1, independent)));
            var packedSources = document.CompanionArchives.Append(archive).DistinctBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(file => file, file => File.ReadAllBytes(file.FullName));
            try
            {
                foreach (var input in packedSources.Keys)
                {
                    using var output = input.Create();
                    SdsArchive.Pack(MafiaEnvironment.ExtractedDir(input), MafiaEnvironment.Profile).Save(output, new SdsWriteOptions { Compress = true });
                }
                var built = new CompiledSceneDocument(archive); var builtSource = Binding(frame, 1, built); var builtFar = Binding(farFrame, 1, built);
                ulong nearId = built.Graph.Resolve(built.Graph.Field(builtSource.Visual, "m_MeshSet"))!.ObjectId;
                ulong farId = built.Graph.Resolve(built.Graph.Field(builtFar.Visual, "m_MeshSet"))!.ObjectId;
                var again = builtSource.Export(built, lowest, out refusal) ?? throw new InvalidDataException(refusal);
                for (int index = 0; index < again.Positions.Length; index++) again.Positions[index] += new Vector3(.25f, 0, 0);
                var edit = builtSource.TryApply(again, lowest, out refusal) ?? throw new InvalidDataException(refusal); edit.ApplyNew();
                try
                {
                    check("compressed Build reopen re-edit retains independently owned native identities", built.PreparedLinkedProxyGroups.Count == 1
                        && built.Graph.Resolve(built.Graph.Field(builtSource.Visual, "m_MeshSet"))!.ObjectId == nearId
                        && built.Graph.Resolve(built.Graph.Field(builtFar.Visual, "m_MeshSet"))!.ObjectId == farId
                        && SameProxyReplacementCorners(builtSource.DecodeLod(lowest)!, builtFar.DecodeLod(0)!));
                    built.SaveWorkingCopy();
                    check("compressed Build ownership evidence survives another save and fresh mapping load", new CompiledSceneDocument(archive).LinkedProxyMappingWarnings.Count == 0);
                }
                finally { edit.RestoreOriginal(); built.SaveWorkingCopy(); }
                string originDirectory = Path.Combine(MafiaEnvironment.ResourcesFolder!, ".illusion", "linked-proxies", "geometry-origins");
                var origins = Directory.GetFiles(originDirectory, "*.json").ToDictionary(path => path, File.ReadAllBytes);
                try
                {
                    foreach (string path in origins.Keys) File.WriteAllText(path, "{invalid original identity evidence");
                    var conservative = new CompiledSceneDocument(archive); var guarded = Binding(frame, 1, conservative);
                    var guardedPayload = guarded.Export(conservative, lowest, out refusal) ?? throw new InvalidDataException(refusal);
                    for (int index = 0; index < guardedPayload.Positions.Length; index++) guardedPayload.Positions[index] += new Vector3(.25f, 0, 0);
                    var guardedEdit = guarded.TryApply(guardedPayload, lowest, out refusal) ?? throw new InvalidDataException(refusal);
                    guardedEdit.ApplyNew();
                    try
                    {
                        check("corrupt original identity evidence falls back to fresh ownership instead of trusting the ledger", conservative.Graph.Resolve(conservative.Graph.Field(guarded.Visual, "m_MeshSet"))!.ObjectId != nearId);
                    }
                    finally { guardedEdit.RestoreOriginal(); }
                }
                finally { foreach (var origin in origins) File.WriteAllBytes(origin.Key, origin.Value); }
            }
            finally { foreach (var input in packedSources) File.WriteAllBytes(input.Key.FullName, input.Value); }
            applied.RestoreOriginal(); applied = null;

            var shared = Binding(frame, 9); var farShared = Binding(farFrame, 9); var sibling = Binding(frame, 11); var farSibling = Binding(farFrame, 11);
            var siblingPositions = sibling.DecodeLod(0)!.Positions.ToArray(); var farSiblingPositions = farSibling.DecodeLod(0)!.Positions.ToArray();
            check("stock shared buffer cannot export before explicit paired ownership", shared.Export(document, 0, out _) is null);
            unique = document.IsolateVisualGeometry(shared.Visual, out refusal) ?? throw new InvalidDataException(refusal);
            var sharedPayload = Export(shared, 0); PokeProxyTriangle(sharedPayload);
            for (int vertex = 0; vertex < sharedPayload.Positions.Length; vertex++) sharedPayload.Positions[vertex] += new Vector3(12, 0, 0);
            applied = shared.TryApply(sharedPayload, 0, out refusal) ?? throw new InvalidDataException(refusal); applied.ApplyNew();
            check("paired shared ownership supports count growth while preserving unedited near and far aliases", SameCorners(shared, 0, farShared)
                && sibling.DecodeLod(0)!.Positions.SequenceEqual(siblingPositions) && farSibling.DecodeLod(0)!.Positions.SequenceEqual(farSiblingPositions));
            document.SaveWorkingCopy(); reloaded = new CompiledSceneDocument(archive);
            check("shared ownership relocation and fresh cloned identities persist through reload", reloaded.PreparedLinkedProxyGroups.Count == 1
                && SameCorners(Binding(frame, 9, reloaded), 0, Binding(farFrame, 9, reloaded)));
            applied.RestoreOriginal(); unique.Undo(); preparation.Undo();
            preparation.Redo(); unique.Redo(); applied.ApplyNew();
            document.SaveWorkingCopy();
            check("ownership preparation topology and geometry replay reacquire current proxy state", document.ValidateLinkedProxyTransform(frame, frame.LocalTransform) is null
                && new CompiledSceneDocument(archive).PreparedLinkedProxyGroups.Count == 1 && SameCorners(shared, 0, farShared));
            applied.RestoreOriginal(); applied = null; unique.Undo(); unique = null; preparation.Undo(); document.SaveWorkingCopy();
            check("all position topology shared ownership RAM budgets native resources and manifests undo byte exact", FilesExact());
        }
        finally
        {
            applied?.RestoreOriginal(); unique?.Undo();
            if (document.PreparedLinkedProxyGroups.Count != 0) preparation.Undo(); document.SaveWorkingCopy();
        }
    }

    private static bool SameProxyReplacementCorners(MeshData fine, MeshData far)
    {
        if (fine.Indices.Length != far.Indices.Length || fine.Parts.Length != far.Parts.Length) return false;
        for (int part = 0; part < fine.Parts.Length; part++) if (fine.Parts[part].MaterialHash != far.Parts[part].MaterialHash || fine.Parts[part].IndexCount != far.Parts[part].IndexCount) return false;
        for (int corner = 0; corner < fine.Indices.Length; corner++)
        {
            int a = (int)fine.Indices[corner], b = (int)far.Indices[corner];
            if (fine.Positions[a] != far.Positions[b] || fine.Normals[a] != far.Normals[b] || fine.UVs?[a] != far.UVs?[b]
                || fine.Tangents?[a] != far.Tangents?[b] || fine.Binormals?[a] != far.Binormals?[b] || fine.Colors?[a] != far.Colors?[b] || fine.DecalUVs?[a] != far.DecalUVs?[b]) return false;
        }
        return true;
    }

    private static void PokeProxyTriangle(MeshObjectPayload payload)
    {
        uint[] triangle = payload.LoopVertexIndices.Take(3).ToArray(); uint center = (uint)payload.Positions.Length;
        payload.Positions = payload.Positions.Append((payload.Positions[triangle[0]] + payload.Positions[triangle[1]] + payload.Positions[triangle[2]]) / 3 + new Vector3(0, 0, .2f)).ToArray();
        int[] donors = [0, 1, 0, 1, 2, 0, 2, 0, 0];
        payload.LoopVertexIndices = new[] { triangle[0], triangle[1], center, triangle[1], triangle[2], center, triangle[2], triangle[0], center }.Concat(payload.LoopVertexIndices.Skip(3)).ToArray();
        payload.LoopOrigIndex = donors.Select(index => payload.LoopOrigIndex[index]).Concat(payload.LoopOrigIndex.Skip(3)).ToArray();
        payload.LoopNormals = donors.Select(index => payload.LoopNormals[index]).Concat(payload.LoopNormals.Skip(3)).ToArray();
        payload.LoopUvs = donors.Select(index => payload.LoopUvs[index]).Concat(payload.LoopUvs.Skip(3)).ToArray();
        if (payload.LoopColors.Length != 0) payload.LoopColors = donors.Select(index => payload.LoopColors[index]).Concat(payload.LoopColors.Skip(3)).ToArray();
        payload.ExtraLoopUvs = payload.ExtraLoopUvs.ToDictionary(pair => pair.Key, pair => donors.Select(index => pair.Value[index]).Concat(pair.Value.Skip(3)).ToArray());
        payload.FaceMaterials = Enumerable.Repeat(payload.FaceMaterials[0], 3).Concat(payload.FaceMaterials.Skip(1)).ToArray();
    }
}
