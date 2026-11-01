using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Domain.Materials;
using Illusion.Formats.Compiled;
using Illusion.Formats.Hashing;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestLinkedProxyStructureHost(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_french_east_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive); if (document.PreparedLinkedProxyGroups.Count != 0) return;
        using var gpu = new GpuContext(); using var host = new D3DImageHost(); var renderer = new SceneRenderer(gpu);
        typeof(ViewportControl).GetProperty("Renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, renderer);
        var owner = new SceneNode("FrameResource", "FrameResource", true) { Source = document }; host.Roots.Add(owner);
        var leaves = new List<SceneNode>(); foreach (var native in document.Roots) owner.AddChild(SceneTree.BuildSceneTree(native, leaves));
        foreach (var leaf in leaves) if (leaf.Pending is { } pending) { var mesh = renderer.AddMesh(pending); mesh.Owner = leaf; leaf.Mesh = mesh; leaf.Pending = null; }
        host.Tree.MeshCount = renderer.Meshes.Count;
        static IEnumerable<SceneNode> Walk(SceneNode row) { yield return row; foreach (var child in row.Children) foreach (var descendant in Walk(child)) yield return descendant; }
        SceneNode Frame(string id) => Walk(owner).First(row => row.Source is CompiledSceneDocument.CompiledFrameNode frame && frame.SceneIdentity == "guid:" + id);
        var fine = Frame("0F947BBBC7BC8574"); var house = Frame("0BFF878103228FD6"); var dock = Frame("0DCE12ED54CE1786");
        var originalMeshes = renderer.Meshes.ToArray(); var originalRows = owner.Children.ToArray();
        var files = document.Graph.Resources.Select(resource => Path.GetDirectoryName(resource.Path)!).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(Directory.GetFiles).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)), StringComparer.OrdinalIgnoreCase);
        Dictionary<CompiledResource, byte[]> Bytes() => document.Graph.Resources.ToDictionary(resource => resource, resource => SHA256.HashData(resource.ToArray()));
        bool Same(Dictionary<CompiledResource, byte[]> before) => before.All(pair => SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value));
        var refusal = host.InspectCompiledLinkedProxy(fine, out var plan);
        if (refusal is not null || plan is null) throw new InvalidDataException(refusal);
        if (host.PrepareCompiledLinkedProxy(fine, plan) is { } rejected) throw new InvalidDataException(rejected);
        try
        {
            var fineFrame = (IFrameNode)fine.Source!; var farFrame = document.GetLinkedProxyTransformTargets(fineFrame).Single();
            SceneNode VisualRow(IFrameNode frame, int ordinal, int? lod = null) => Walk(owner).Where(row => row.Mesh is not null
                && (row.GeometrySource ?? row.Source) is ICompiledMeshSource source && ReferenceEquals(source.CanonicalFrame, frame)
                && (source.SceneIdentity.EndsWith($":visual:{ordinal}", StringComparison.Ordinal) || ordinal == 0 && ReferenceEquals(source, frame)))
                .OrderByDescending(row => row.Lod).First(row => lod is null || row.Lod == lod);
            var nearRow = VisualRow(fineFrame, 1); var farRow = VisualRow(farFrame, 1, 0);
            var materialEditor = (IMaterialSlotEditor)(nearRow.GeometrySource ?? nearRow.Source)!;
            var slotsBefore = materialEditor.GetSlotTable().Select(row => row.ToArray()).ToArray();
            check("selected native LOWEST material inspection uses its actual LOD table", nearRow.Lod > 0
                && host.GetMaterialsForNode(nearRow).Select(material => material.Hash).SequenceEqual(slotsBefore[nearRow.Lod]));
            var beforeMaterial = Bytes(); var beforeMeshes = renderer.Meshes.ToArray(); int beforeHistory = host.History.UndoCount;
            const ulong replacement = 0x2ac45a4f3c7d277b;
            check("public prepared material assignment pairs native and actual fine/far GPU slots", host.AssignSlotMaterial(nearRow, 0, replacement)
                && nearRow.Mesh!.Parts[0].MaterialHash == replacement && farRow.Mesh!.Parts[0].MaterialHash == replacement);
            var changed = Bytes(); var field = document.Graph.Items(document.Graph.Field(document.Graph.Field(document.Graph.Resolve(document.Graph.Field(((ICompiledMeshSource)(nearRow.GeometrySource ?? nearRow.Source)!).GeometryBinding!.Visual, "m_FaceGroupSet")), "m_Data"), "m_FaceGroups")).Last();
            byte[] fieldBytes = document.Graph.Data(field).ToArray(), tampered = fieldBytes.ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(tampered.AsSpan(12), slotsBefore[^1][0]);
            document.Graph.Owner(field).Replace(field, tampered); var injected = Bytes(); int undoCount = host.History.UndoCount; var selection = host.Selection.Selected.ToArray(); bool staleRefused = false;
            try { host.History.Undo(); } catch (EditRefusedException) { staleRefused = true; } catch (InvalidDataException) { staleRefused = true; }
            check("stale material table refuses composite undo before ownership GPU selection or history changes", staleRefused && Same(injected)
                && beforeMeshes.SequenceEqual(renderer.Meshes) && selection.SequenceEqual(host.Selection.Selected) && undoCount == host.History.UndoCount);
            document.Graph.Owner(field).Replace(field, fieldBytes);
            var collection = (MaterialCollection)typeof(MafiaMaterials).GetProperty("Collection", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var material = (Material_v63)collection.FindByHash(replacement)!; var texture = material.Textures.First(); material.Textures[texture.Key] = new HashName("missing_native_slot_dependency_probe");
            bool dependencyRefused = false;
            try { host.History.Undo(); } catch (EditRefusedException) { dependencyRefused = true; } catch (InvalidDataException) { dependencyRefused = true; }
            finally { material.Textures[texture.Key] = texture.Value; }
            check("changed material texture refuses native slot ownership composite atomically", dependencyRefused && Same(changed) && undoCount == host.History.UndoCount && beforeMeshes.SequenceEqual(renderer.Meshes));
            host.History.Undo();
            check("material ownership composite undo restores native identities and both GPU material hashes", Same(beforeMaterial)
                && nearRow.Mesh!.Parts[0].MaterialHash == slotsBefore[nearRow.Lod][0] && farRow.Mesh!.Parts[0].MaterialHash == slotsBefore[^1][0] && host.History.UndoCount == beforeHistory);
            var carryObjects = (IDictionary)typeof(CompiledSceneDocument).GetField("_proxySlotCarries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(document)!;
            string payload = carryObjects.Values.Cast<object>().SelectMany(carry => (string[])carry.GetType().GetProperty("SourceFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(carry)!)
                .First(path => path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && File.Exists(path));
            if (!Path.GetFullPath(payload).StartsWith(Path.GetFullPath(MafiaEnvironment.ResourcesFolder!) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The missing-dependency probe must use a copied texture.");
            string hidden = payload + ".probe-hidden-" + Guid.NewGuid().ToString("N"); var redoBytes = Bytes(); var redoMeshes = renderer.Meshes.ToArray(); int redoHistory = host.History.UndoCount;
            bool missingRefused = false; File.Move(payload, hidden);
            try { host.History.Redo(); } catch (EditRefusedException) { missingRefused = true; } catch (InvalidDataException) { missingRefused = true; }
            finally { File.Move(hidden, payload); }
            check("missing carried texture preflights redo before any cache ownership or GPU history changes", missingRefused && Same(redoBytes)
                && redoMeshes.SequenceEqual(renderer.Meshes) && redoHistory == host.History.UndoCount && host.History.CanRedo);
            host.History.Redo(); document.SaveWorkingCopy();
            check("material ownership composite redo saves with a validated native mapping", new CompiledSceneDocument(archive).PreparedLinkedProxyGroups.Count == 1
                && nearRow.Mesh!.Parts[0].MaterialHash == replacement && farRow.Mesh!.Parts[0].MaterialHash == replacement);
            host.History.Undo();

            var dockFrame = (CompiledSceneDocument.CompiledFrameNode)dock.Source!; var dockFar = document.GetLinkedProxyTransformTargets(dockFrame).Single();
            var dockNearRow = VisualRow(dockFrame, 0); var dockFarRow = VisualRow(dockFar, 0);
            dockNearRow.Mesh!.Visible = false; dockFarRow.Mesh!.Visible = true;
            var bones = document.InspectPreparedLinkedProxyBones(dockFrame, out refusal); if (bones.Count < 2) throw new InvalidDataException(refusal);
            var rig = document.VehicleRig(dockFrame)!; var pose = rig.LiveRest.ToArray(); pose[1] *= Matrix4x4.CreateTranslation(0, .125f, 0); rig.SetAnimationPose(pose);
            var beforeRig = Bytes(); var oldGpu = renderer.Meshes.ToArray(); var epochs = document.Graph.Resources.Select(resource => (resource.Revision, resource.IsDirty)).ToArray();
            long boundsEpoch = document.NativeLods!.BoundsRevision; int rigHistory = host.History.UndoCount; int allocations = 0;
            host.BeforeLinkedProxyRigGpuAllocation = () => { if (++allocations == 2) throw new InvalidOperationException("Injected paired rig GPU allocation failure."); };
            string? failed;
            try { failed = host.SetLinkedProxyBone(dock, 1, bones[1].Local * Matrix4x4.CreateTranslation(.25f, 0, 0)); }
            finally { host.BeforeLinkedProxyRigGpuAllocation = null; }
            check("second rig GPU allocation failure restores bytes dirty epochs bounds playback GPU and history", failed is not null && allocations == 2 && Same(beforeRig)
                && epochs.SequenceEqual(document.Graph.Resources.Select(resource => (resource.Revision, resource.IsDirty))) && boundsEpoch == document.NativeLods.BoundsRevision
                && ReferenceEquals(rig, document.VehicleRig(dockFrame)) && pose.SequenceEqual(document.VehicleRig(dockFrame)!.LiveRest)
                && oldGpu.SequenceEqual(renderer.Meshes) && rigHistory == host.History.UndoCount);
            rig.SetAnimationPose(null);
            var nearGpu = dockNearRow.Mesh; var farGpu = dockFarRow.Mesh; var positions = nearGpu!.PickPositions!.ToArray();
            if (host.SetLinkedProxyBone(dock, 1, bones[1].Local * Matrix4x4.CreateTranslation(.25f, 0, 0)) is { } boneRefusal) throw new InvalidDataException(boneRefusal);
            check("real rig edit replaces both GPU geometries and preserves independent manual eyes", !ReferenceEquals(nearGpu, dockNearRow.Mesh) && !ReferenceEquals(farGpu, dockFarRow.Mesh)
                && !dockNearRow.Mesh!.Visible && dockFarRow.Mesh!.Visible && !positions.SequenceEqual(dockNearRow.Mesh.PickPositions!));
            host.History.Undo(); host.History.Redo(); document.SaveWorkingCopy(); host.History.Undo();
            check("paired rig GPU undo redo save restores original resources and exact GPU draw order", Same(beforeRig) && oldGpu.SequenceEqual(renderer.Meshes));
            dockNearRow.Mesh!.Visible = true;

            var dockGpu = renderer.Meshes.ToArray(); host.Selection.SetSelection([dock], dock);
            check("public prepared rigid dock delete ignores skeleton helper rows and removes both native owners", host.DeleteSelected() && !host.Tree.IsInScene(dock));
            host.History.Undo();
            check("public prepared rigid dock delete undo restores exact GPU order and canonical rig owner", host.Tree.IsInScene(dock) && dockGpu.SequenceEqual(renderer.Meshes));

            var second = Frame("0B0612D7907F073E"); var selectedForFault = new[] { house, second }; host.Selection.SetSelection(selectedForFault, second);
            var faultBytes = Bytes(); var faultMeshes = renderer.Meshes.ToArray(); var faultRows = owner.Children.ToArray();
            var faultEpochs = document.Graph.Resources.Select(resource => (resource.Revision, resource.IsDirty)).ToArray(); long faultBounds = document.NativeLods.BoundsRevision;
            int faultHistory = host.History.UndoCount; bool duplicateFault = false;
            host.Editing.BeforeStructuredDuplicateGpuAllocation = frame => { if (ReferenceEquals(frame, second.Source)) throw new InvalidOperationException("Injected second selected duplicate GPU failure."); };
            try { host.DuplicateSelected(); } catch (InvalidOperationException) { duplicateFault = true; }
            finally { host.Editing.BeforeStructuredDuplicateGpuAllocation = null; }
            check("second selected duplicate GPU failure restores all native epochs rows selection renderer and history", duplicateFault && Same(faultBytes)
                && faultEpochs.SequenceEqual(document.Graph.Resources.Select(resource => (resource.Revision, resource.IsDirty))) && faultBounds == document.NativeLods.BoundsRevision
                && faultRows.SequenceEqual(owner.Children) && faultMeshes.SequenceEqual(renderer.Meshes) && selectedForFault.SequenceEqual(host.Selection.Selected) && faultHistory == host.History.UndoCount);

            var groupRefusal = host.CreateLinkedProxyGroup(house, "Public prepared Kit probe", out var kit);
            if (groupRefusal is not null || kit is null) throw new InvalidDataException(groupRefusal);
            check("public prepared Kit authoring reparents a real fine object", host.Reparent(house, kit));
            host.Selection.SetSelection([kit], kit); host.DuplicateSelected(); var copied = host.Editing.LastDuplicates.Single().Copy;
            var copiedFine = Walk(copied).Where(row => row.Mesh is not null).Select(row => (row.GeometrySource ?? row.Source) as ICompiledMeshSource)
                .OfType<ICompiledMeshSource>().Select(source => source.CanonicalFrame).OfType<CompiledSceneDocument.CompiledFrameNode>().First();
            var copiedFar = document.GetLinkedProxyTransformTargets(copiedFine).Single();
            var nearMeshes = renderer.Meshes.Where(mesh => mesh.Owner is SceneNode row && (row.GeometrySource ?? row.Source) is ICompiledMeshSource source && ReferenceEquals(source.CanonicalFrame, copiedFine)).ToArray();
            var farMeshes = renderer.Meshes.Where(mesh => mesh.Owner is SceneNode row && (row.GeometrySource ?? row.Source) is ICompiledMeshSource source && ReferenceEquals(source.CanonicalFrame, copiedFar)).ToArray();
            var membership = nearMeshes.First().NativeLod!; var nativeGroup = membership.Graph.Groups[membership.Group];
            var nearPoint = (nativeGroup.WorldMin + nativeGroup.WorldMax) * .5f; var farPoint = nearPoint + new Vector3(100000, 0, 0);
            foreach (var mesh in nearMeshes.Concat(farMeshes)) mesh.RefreshNativeLod(nearPoint);
            bool nearMask = nearMeshes.Any(mesh => mesh.RenderVisible) && farMeshes.All(mesh => !mesh.RenderVisible);
            foreach (var mesh in nearMeshes.Concat(farMeshes)) mesh.RefreshNativeLod(farPoint);
            check("public nested Kit duplicate applies exclusive near and far native GPU visibility", nearMeshes.Length > 0 && farMeshes.Length > 0
                && nearMask && nearMeshes.All(mesh => !mesh.RenderVisible) && farMeshes.Any(mesh => mesh.RenderVisible));
            var beforeDelete = renderer.Meshes.ToArray(); host.Selection.SetSelection([copied], copied);
            check("public DeleteSelected removes the whole Kit and paired far GPU roots", host.DeleteSelected() && !host.Tree.IsInScene(copied)
                && !renderer.Meshes.Any(mesh => nearMeshes.Contains(mesh) || farMeshes.Contains(mesh)));
            document.SaveWorkingCopy(); host.History.Undo();
            check("public paired Kit delete undo restores every GPU in its exact original order", host.Tree.IsInScene(copied) && beforeDelete.SequenceEqual(renderer.Meshes));
            host.History.Undo(); host.History.Undo(); host.History.Undo();

            // Fault injection narrows only the runtime budget to two real canonical pairs. This
            // exercises aggregate last-pair refusal without inventing any serialized game object.
            var states = (IDictionary)typeof(CompiledSceneDocument).GetField("_linkedProxyStates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(document)!;
            var state = states.Values.Cast<object>().Single(); var pairs = (IDictionary)state.GetType().GetProperty("Pairs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
            var savedEntries = new List<DictionaryEntry>(); var enumerator = pairs.GetEnumerator(); while (enumerator.MoveNext()) savedEntries.Add(enumerator.Entry);
            var entries = savedEntries.ToArray(); var two = entries.Where(entry => document.CanDuplicatePreparedLinkedProxy((IFrameNode)entry.Key)).Take(2).ToArray();
            pairs.Clear(); foreach (var entry in two) pairs.Add(entry.Key, entry.Value);
            try
            {
                var selected = two.Select(entry => Walk(owner).First(row => ReferenceEquals(row.Source, entry.Key))).ToArray(); host.Selection.SetSelection(selected, selected[^1]);
                var unchanged = Bytes(); var meshes = renderer.Meshes.ToArray(); int history = host.History.UndoCount;
                check("public batch deletion preflights the last-pair guard before any selected root changes", !host.DeleteSelected() && Same(unchanged)
                    && meshes.SequenceEqual(renderer.Meshes) && selected.SequenceEqual(host.Selection.Selected) && history == host.History.UndoCount);
            }
            finally { pairs.Clear(); foreach (var entry in entries) pairs.Add(entry.Key, entry.Value); }
            host.History.Undo(); document.SaveWorkingCopy();
            check("all public prepared structural material and rig edits undo to exact original files rows and GPU objects", originalRows.SequenceEqual(owner.Children)
                && originalMeshes.SequenceEqual(renderer.Meshes) && files.All(pair => File.Exists(pair.Key) && SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)));
            host.History.Clear();
        }
        finally
        {
            host.BeforeLinkedProxyRigGpuAllocation = null; host.Editing.BeforeStructuredDuplicateGpuAllocation = null;
            while (host.History.CanUndo) host.History.Undo(); document.SaveWorkingCopy(); host.History.Clear();
        }
    }
}
