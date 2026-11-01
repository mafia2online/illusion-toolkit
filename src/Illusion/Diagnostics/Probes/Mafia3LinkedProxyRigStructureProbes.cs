using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestPreparedProxyRigStructure(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_french_east_block_b_lod0.sds")); if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive); if (document.PreparedLinkedProxyGroups.Count != 0) return;
        static IEnumerable<ISceneHierarchyNode> Walk(IEnumerable<ISceneHierarchyNode> rows)
        { foreach (var row in rows) { yield return row; foreach (var child in Walk(row.Children)) yield return child; } }
        var frames = Walk(document.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct().ToArray();
        var owner = frames.Single(frame => frame.SceneIdentity == "guid:0F947BBBC7BC8574"); var dock = frames.Single(frame => frame.SceneIdentity == "guid:0DCE12ED54CE1786");
        var folders = document.Graph.Resources.Select(resource => Path.GetDirectoryName(resource.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var files = folders.SelectMany(Directory.GetFiles).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)), StringComparer.OrdinalIgnoreCase);
        var plan = document.InspectLinkedProxyPreparation(owner, out var refusal) ?? throw new InvalidDataException(refusal);
        var preparation = document.PrepareLinkedProxyTransforms(plan, out refusal) ?? throw new InvalidDataException(refusal);
        IEditAction? add = null, reparent = null;
        try
        {
            var rig = document.VehicleRig(dock)!; var oldModel = Enumerable.Range(0, rig.Count).Select(rig.ModelRest).ToArray();
            var oldInverse = rig.Skeleton.InverseBind.ToArray();
            var bytes = document.Graph.Resources.ToDictionary(resource => resource, resource => SHA256.HashData(resource.ToArray()));
            bool Same() => bytes.All(pair => SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value));
            check("prepared bone insertion rejects duplicate names before native mutations", document.AddPreparedLinkedProxyBone(dock, "bone:0", 0, Matrix4x4.Identity, 1, out _) is null && Same());
            check("prepared bone insertion rejects invalid parents and nonrigid transforms atomically", document.AddPreparedLinkedProxyBone(dock, "bad_parent", 255, Matrix4x4.Identity, 1, out _) is null
                && document.AddPreparedLinkedProxyBone(dock, "bad_scale", 0, Matrix4x4.CreateScale(2), 1, out _) is null && Same());
            add = document.AddPreparedLinkedProxyBone(dock, "toolkit_probe_attachment", 1, Matrix4x4.CreateTranslation(.3f, .1f, .2f), .25f, out refusal) ?? throw new InvalidDataException(refusal);
            var current = document.VehicleRig(dock)!; var far = (CompiledSceneDocument.CompiledFrameNode)document.GetLinkedProxyTransformTargets(dock).Single();
            check("prepared bone insertion appends all native near and far tables without renumbering existing binds", current.Count == rig.Count + 1
                && document.VehicleRig(far)!.Count == current.Count && current.Parent(rig.Count) == 1 && current.Find("toolkit_probe_attachment") == rig.Count
                && oldModel.SequenceEqual(Enumerable.Range(0, rig.Count).Select(current.ModelRest)) && oldInverse.SequenceEqual(current.Skeleton.InverseBind.Take(rig.Count)));
            bool directRefused = false;
            try { ((IFrameNode)current.Skeleton.Bones[rig.Count].Source!).LocalTransform = Matrix4x4.CreateTranslation(5, 0, 0); }
            catch (EditRefusedException) { directRefused = true; }
            check("new prepared bone adapters refuse direct property writes that would bypass native pairing", directRefused
                && current.ModelRest(rig.Count) == document.VehicleRig(far)!.ModelRest(rig.Count));
            document.SaveWorkingCopy(); var reopened = new CompiledSceneDocument(archive);
            var reopenedDock = Walk(reopened.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().First(frame => frame.SceneIdentity == dock.SceneIdentity);
            check("appended prepared skeleton tables save and independently reopen with valid mapping", reopened.LinkedProxyMappingWarnings.Count == 0
                && reopened.VehicleRig(reopenedDock)!.Count == current.Count && reopened.VehicleRig(reopenedDock)!.Parent(rig.Count) == 1);
            var beforeParentModel = current.ModelRest(rig.Count);
            reparent = document.ReparentPreparedLinkedProxyBone(dock, rig.Count, 0, out refusal) ?? throw new InvalidDataException(refusal);
            check("prepared bone reparent preserves model-space bind and all existing bone indices", document.VehicleRig(dock)!.Parent(rig.Count) == 0
                && Vector3.Distance(document.VehicleRig(dock)!.ModelRest(rig.Count).Translation, beforeParentModel.Translation) < 1e-5f
                && oldModel.SequenceEqual(Enumerable.Range(0, rig.Count).Select(document.VehicleRig(dock)!.ModelRest)));
            check("prepared bone reparent rejects forward parents and cycles", document.ReparentPreparedLinkedProxyBone(dock, 1, rig.Count, out _) is null);
            reparent.ValidateUndo(); reparent.Undo(); reparent.ValidateRedo(); reparent.Redo(); reparent.Undo(); reparent = null;
            add.ValidateUndo(); add.Undo(); add.ValidateRedo(); add.Redo(); document.SaveWorkingCopy(); add.Undo(); add = null;
            check("prepared bone insertion and reparent undo restore exact native tables and cached skeleton owner", Same() && ReferenceEquals(rig, document.VehicleRig(dock)));
            preparation.Undo(); document.SaveWorkingCopy();
            check("advanced prepared rig undo restores all original compiled files and native manifests", files.All(pair => File.Exists(pair.Key) && SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value))
                && folders.SelectMany(Directory.GetFiles).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(files.Keys));
        }
        finally { reparent?.Undo(); add?.Undo(); if (document.PreparedLinkedProxyGroups.Count > 0) preparation.Undo(); document.SaveWorkingCopy(); }
    }
}
