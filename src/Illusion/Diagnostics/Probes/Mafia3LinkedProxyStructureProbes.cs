using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Domain.Materials;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledLinkedProxyStructure(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_french_east_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var doc = new CompiledSceneDocument(archive);
        if (doc.PreparedLinkedProxyGroups.Count != 0) return;
        static IEnumerable<ISceneHierarchyNode> Rows(IEnumerable<ISceneHierarchyNode> roots)
        { foreach (var row in roots) { yield return row; foreach (var child in Rows(row.Children)) yield return child; } }
        var frames = Rows(doc.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct().ToArray();
        var owner = frames.First(frame => frame.SceneIdentity == "guid:0F947BBBC7BC8574");
        var house = frames.First(frame => frame.SceneIdentity == "guid:0BFF878103228FD6");
        var dock = frames.First(frame => frame.SceneIdentity == "guid:0DCE12ED54CE1786");
        var folders = doc.Graph.Resources.Select(resource => Path.GetDirectoryName(resource.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var files = folders.SelectMany(Directory.GetFiles).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)), StringComparer.OrdinalIgnoreCase);
        bool FilesExact() => folders.SelectMany(Directory.GetFiles).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(files.Keys)
            && files.All(pair => File.Exists(pair.Key) && SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value));
        var plan = doc.InspectLinkedProxyPreparation(owner, out var refusal) ?? throw new InvalidDataException(refusal);
        var preparation = doc.PrepareLinkedProxyTransforms(plan, out refusal) ?? throw new InvalidDataException(refusal);
        IFrameDuplication? copy = null, group = null; IFrameDetachment? removed = null; IEditAction? rigEdit = null, ownership = null;
        IMaterialSlotEditor? slotEditor = null; IReadOnlyList<ulong[]>? originalSlots = null;
        try
        {
            var farOwner = doc.GetLinkedProxyTransformTargets(owner).Single();
            ICompiledMeshSource Visual(IFrameNode frame, int ordinal = 1) => Rows(doc.Roots).Select(row => row.GeometrySource ?? row.Source)
                .OfType<ICompiledMeshSource>().First(source => ReferenceEquals(source.CanonicalFrame, frame) && source.GeometryBinding is not null
                    && (source.SceneIdentity.EndsWith($":visual:{ordinal}", StringComparison.Ordinal) || ordinal == 0 && ReferenceEquals(source, frame)));
            var source = Visual(owner); var target = Visual(farOwner);
            slotEditor = (IMaterialSlotEditor)source; originalSlots = slotEditor.GetSlotTable().Select(row => row.ToArray()).ToArray();
            var native = doc.Graph.Resources.ToDictionary(resource => resource, resource => SHA256.HashData(resource.ToArray()));
            bool NativeExact() => native.All(pair => SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value));
            check("prepared unknown-component duplication refuses without native changes", doc.DuplicatePreparedLinkedProxy(owner, out _) is null && NativeExact());
            ownership = doc.EnsurePreparedLinkedProxyMaterialOwnership(owner, source.GeometryBinding!.Visual, out refusal) ?? throw new InvalidDataException(refusal ?? "Missing native ownership action.");
            var revisions = doc.Graph.Resources.Select(resource => resource.Revision).ToArray();
            check("unknown prepared material refuses without resource revision changes", !slotEditor.SetSlotMaterial(0, ulong.MaxValue)
                && revisions.SequenceEqual(doc.Graph.Resources.Select(resource => resource.Revision)));
            const ulong replacement = 0x2ac45a4f3c7d277b;
            var requestedSlots = originalSlots.Select(row => row.ToArray()).ToArray(); ulong replacedHash = requestedSlots[^1][0];
            foreach (var row in requestedSlots) for (int slot = 0; slot < row.Length; slot++) if (row[slot] == replacedHash) row[slot] = replacement;
            if (!slotEditor.SetSlotTable(requestedSlots)) throw new InvalidDataException(doc.LinkedProxyMaterialRefusal);
            check("prepared slot replacement pairs fine and far native lowest material tables", slotEditor.GetSlotTable()[^1].SequenceEqual(((IMaterialSlotEditor)target).GetSlotTable()[0]));
            ownership.ValidateUndo();
            check("slot composition preflight permits only committed material GUID differences", doc.ValidatePreparedLinkedProxyMaterialTable(owner, originalSlots) is null);
            doc.SaveWorkingCopy();
            var reopened = new CompiledSceneDocument(archive);
            check("new material dependency carry saves and reopens a valid native proxy mapping", reopened.PreparedLinkedProxyGroups.Count == 1 && reopened.LinkedProxyMappingWarnings.Count == 0);
            if (!slotEditor.SetSlotTable(originalSlots)) throw new InvalidDataException(doc.LinkedProxyMaterialRefusal);
            ownership.Undo(); ownership = null; originalSlots = null;
            check("material and cache ownership undo restore original native identities", NativeExact());

            copy = doc.Duplicate(house, out refusal) ?? throw new InvalidDataException(refusal);
            var cloned = (CompiledSceneDocument.CompiledFrameNode)copy.Root.Source!;
            var far = (IFrameNode)copy.AdditionalRoots.Single().Source!;
            check("prepared duplicate has separate canonical fine and generated far registrations", cloned.SceneIdentity != house.SceneIdentity
                && doc.GetLinkedProxyTransformTargets(cloned).Single() == far && copy.AdditionalRoots.Count == 1);
            var originalLocal = cloned.LocalTransform; cloned.LocalTransform *= Matrix4x4.CreateTranslation(1, 0, 0);
            check("prepared duplicate transforms keep both native worlds equal", cloned.WorldTransform == far.WorldTransform);
            doc.SaveWorkingCopy(); reopened = new CompiledSceneDocument(archive);
            check("paired duplicate save reopens with complete native family coverage", reopened.PreparedLinkedProxyGroups.Count == 1 && reopened.LinkedProxyMappingWarnings.Count == 0);
            removed = doc.CaptureDetachment([cloned]) ?? throw new InvalidDataException("Paired removal refused."); removed.ValidateDetach(); removed.Detach();
            check("paired removal exposes exactly its generated far canonical frame", removed.AdditionalFrames.Count == 1 && ReferenceEquals(removed.AdditionalFrames[0], far));
            doc.SaveWorkingCopy(); reopened = new CompiledSceneDocument(archive);
            check("paired removal save removes both GUID definitions and preserves mapping", !reopened.Graph.Objects.ContainsKey(Convert.ToUInt64(cloned.SceneIdentity[5..], 16))
                && reopened.PreparedLinkedProxyGroups.Count == 1 && reopened.LinkedProxyMappingWarnings.Count == 0);
            removed.Reattach(); removed = null; cloned.LocalTransform = originalLocal; copy.Detach(); copy.Reattach(); copy.Detach(); copy = null;

            group = doc.CreatePreparedLinkedProxyGroup(house, "Prepared native probe Kit", out refusal) ?? throw new InvalidDataException(refusal);
            var kit = (IFrameNode)group.Root.Source!;
            check("prepared identity Kit belongs to the fine resource and has no render geometry", kit.LocalTransform == Matrix4x4.Identity && group.Root.Mesh is null && group.Root.LodMeshes.Count == 0);
            if (!doc.Reparent(house, kit)) throw new InvalidDataException("Same-resource prepared reparent refused.");
            check("prepared reparent preserves paired fine and far worlds", house.WorldTransform == doc.GetLinkedProxyTransformTargets(house).Single().WorldTransform);
            copy = doc.Duplicate(kit, out refusal) ?? throw new InvalidDataException(refusal);
            var nestedFrame = Rows(copy.Root.Children).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().First(frame => doc.GetLinkedProxyTransformTargets(frame).Count != 0);
            var nestedMeshes = Rows([copy.Root]).SelectMany(row => row.Mesh is { } mesh ? new[] { mesh } : row.LodMeshes).ToArray();
            check("native Kit subtree duplication assigns renderer LOD membership to nested fine meshes", copy.AdditionalRoots.Count == 1
                && doc.GetLinkedProxyTransformTargets(nestedFrame).Count == 1 && nestedMeshes.Length > 0 && nestedMeshes.All(mesh => mesh.NativeLod is not null));
            var subtree = Rows([copy.Root]).Select(row => row.Source).OfType<IFrameNode>().Distinct().ToArray();
            removed = doc.CaptureDetachment(subtree) ?? throw new InvalidDataException("Whole Kit subtree removal refused."); removed.Detach(); doc.SaveWorkingCopy(); reopened = new CompiledSceneDocument(archive);
            check("whole prepared Kit removal saves a complete remaining native family", reopened.PreparedLinkedProxyGroups.Count == 1 && reopened.LinkedProxyMappingWarnings.Count == 0);
            removed.Reattach(); removed = null; copy.Detach(); copy = null;
            if (!doc.Reparent(house, doc)) throw new InvalidDataException("Prepared unparent refused."); group.Detach(); group = null;

            var bones = doc.InspectPreparedLinkedProxyBones(dock, out refusal); if (bones.Count != 2) throw new InvalidDataException(refusal);
            var originalRig = doc.VehicleRig(dock)!; var originalImmutable = originalRig.Immutable;
            var originalRest = doc.Graph.Data(doc.Graph.Field(originalImmutable, "m_OrigBoneMatrices")!).ToArray();
            var dockBinding = Visual(dock, 0).GeometryBinding!; var oldPositions = dockBinding.DecodeLod(0)!.Positions.ToArray();
            check("prepared rig rejects nonrigid scale without native writes", doc.SetPreparedLinkedProxyBoneTransform(dock, 1, Matrix4x4.CreateScale(2), out _) is null);
            rigEdit = doc.SetPreparedLinkedProxyBoneTransform(dock, 1, bones[1].Local * Matrix4x4.CreateTranslation(.25f, 0, 0), out refusal) ?? throw new InvalidDataException(refusal);
            bool staleBoneRefused = false;
            try { ((IFrameNode)originalRig.Skeleton.Bones[1].Source!).LocalTransform = bones[1].Local; }
            catch (EditRefusedException) { staleBoneRefused = true; }
            check("paired rest authoring isolates immutable native GUIDs and refuses stale original bone adapters", !ReferenceEquals(originalImmutable, doc.VehicleRig(dock)!.Immutable)
                && originalRest.SequenceEqual(doc.Graph.Data(doc.Graph.Field(originalImmutable, "m_OrigBoneMatrices")!).ToArray()) && staleBoneRefused);
            check("paired native existing-bone edit changes actual rigid geometry", !oldPositions.SequenceEqual(dockBinding.DecodeLod(0)!.Positions));
            doc.SaveWorkingCopy(); reopened = new CompiledSceneDocument(archive);
            check("paired rest-bone edit saves and reopens its native mapping", reopened.PreparedLinkedProxyGroups.Count == 1 && reopened.LinkedProxyMappingWarnings.Count == 0);
            rigEdit.ValidateUndo(); rigEdit.Undo(); rigEdit.ValidateRedo(); rigEdit.Redo(); rigEdit.Undo(); rigEdit = null;
            check("paired rigid rest-bone undo restores exact decoded positions", oldPositions.SequenceEqual(dockBinding.DecodeLod(0)!.Positions));
            check("rig ownership undo restores the exact canonical skeleton adapter", ReferenceEquals(originalRig, doc.VehicleRig(dock)));
            preparation.ValidateUndo(); preparation.Undo(); doc.SaveWorkingCopy();
            check("prepared structural material and rig lifecycle restores all original GENR manifests and payloads", FilesExact());
        }
        finally
        {
            rigEdit?.Undo();
            if (originalSlots is not null && slotEditor is not null) _ = slotEditor.SetSlotTable(originalSlots);
            ownership?.Undo(); removed?.Reattach(); copy?.Detach();
            if (group is not null) { _ = doc.Reparent(house, doc); group.Detach(); }
            if (doc.PreparedLinkedProxyGroups.Count != 0) preparation.Undo();
            doc.SaveWorkingCopy();
        }
    }
}
