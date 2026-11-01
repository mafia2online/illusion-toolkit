using System.IO;
using System.Numerics;
using System.Reflection;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestPreparedProxyRigHost(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_french_east_block_b_lod0.sds")); if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive); if (document.PreparedLinkedProxyGroups.Count != 0) return;
        using var gpu = new GpuContext(); using var host = new D3DImageHost(); var renderer = new SceneRenderer(gpu) { ShowSky = false, ShowSkeleton = true };
        typeof(ViewportControl).GetProperty("Renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, renderer);
        using var target = new SharedRenderTarget(gpu, 192, 192);
        var owner = new SceneNode("FrameResource", "FrameResource", true) { Source = document }; host.Roots.Add(owner);
        var leaves = new List<SceneNode>(); foreach (var native in document.Roots) owner.AddChild(SceneTree.BuildSceneTree(native, leaves));
        foreach (var leaf in leaves) if (leaf.Pending is { } pending) { var mesh = renderer.AddMesh(pending); mesh.Owner = leaf; leaf.Mesh = mesh; leaf.Pending = null; }
        host.Tree.MeshCount = renderer.Meshes.Count;
        static IEnumerable<SceneNode> Walk(SceneNode row) { yield return row; foreach (var child in row.Children) foreach (var descendant in Walk(child)) yield return descendant; }
        SceneNode Frame(string id) => Walk(owner).First(row => row.Source is CompiledSceneDocument.CompiledFrameNode frame && frame.SceneIdentity == "guid:" + id);
        var fine = Frame("0F947BBBC7BC8574"); var dock = Frame("0DCE12ED54CE1786"); var dockFrame = (CompiledSceneDocument.CompiledFrameNode)dock.Source!;
        var refusal = host.InspectCompiledLinkedProxy(fine, out var plan);
        if (refusal is not null || plan is null) throw new InvalidDataException(refusal);
        if (host.PrepareCompiledLinkedProxy(fine, plan) is { } rejected) throw new InvalidDataException(rejected);
        var initialized = host.Streamer.PrepareCompiledRigAuthoringRefresh(owner, new HashSet<IFrameNode>()); initialized.Redo();
        var rig = document.VehicleRig(dockFrame)!; var originalTree = dock.Children.Single(row => row.Kind == "Skeleton");
        var originalBone = Walk(originalTree).Single(row => CompiledVehicleRig.BoneIndex(row.Source) == 1);
        var originalMeshes = renderer.Meshes.ToArray(); var originalNative = document.Graph.Resources.ToDictionary(resource => resource, resource => resource.ToArray());
        bool NativeExact() => originalNative.All(pair => pair.Key.ToArray().AsSpan().SequenceEqual(pair.Value));
        var position = Vector3.Transform(rig.ModelRest(1).Translation, dockFrame.WorldTransform);
        renderer.Camera.Orthographic = true; renderer.Camera.OrthoHeight = 3; renderer.Camera.LookAt(position + new Vector3(0, -5, 2), position);
        foreach (var mesh in renderer.Meshes) mesh.Visible = false;
        host.Selection.SetSelection([originalBone], originalBone);
        byte[] Draw() { renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
        var beforePixels = Draw(); var originalGlyphs = typeof(SceneRenderer).GetField("_rigRenderer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
        int Glyphs() => (int)originalGlyphs.GetType().GetProperty("GlyphCount")!.GetValue(originalGlyphs)!;
        int oldGlyphCount = Glyphs(); int originalHistory = host.History.UndoCount;
        try
        {
            int allocations = 0; var epochs = document.Graph.Resources.Select(resource => (resource.Revision, resource.IsDirty)).ToArray(); long bounds = document.NativeLods!.BoundsRevision;
            host.Selection.SetSelection([originalBone], originalBone);
            host.BeforeLinkedProxyRigGpuAllocation = () => { if (++allocations == 2) throw new InvalidOperationException("Injected appended-bone second GPU failure."); };
            string? failed; try { failed = host.AddLinkedProxyBone(dock, "probe_failed_attachment", 1, Matrix4x4.CreateTranslation(.4f, 0, .3f), .25f); }
            finally { host.BeforeLinkedProxyRigGpuAllocation = null; }
            check("prepared bone insertion GPU failure restores native epochs tree selection overlays and history", failed is not null && allocations == 2 && NativeExact()
                && epochs.SequenceEqual(document.Graph.Resources.Select(resource => (resource.Revision, resource.IsDirty))) && bounds == document.NativeLods.BoundsRevision
                && ReferenceEquals(originalTree, dock.Children.Single(row => row.Kind == "Skeleton")) && ReferenceEquals(originalBone, host.Selection.Active)
                && renderer.Meshes.SequenceEqual(originalMeshes) && host.History.UndoCount == originalHistory && Draw().SequenceEqual(beforePixels));
            if (host.AddLinkedProxyBone(dock, "probe_native_attachment", 1, Matrix4x4.CreateTranslation(.4f, 0, .3f), .25f) is { } addRefusal) throw new InvalidDataException(addRefusal);
            var insertedTree = dock.Children.Single(row => row.Kind == "Skeleton"); var insertedRig = document.VehicleRig(dockFrame)!;
            var insertedBone = Walk(insertedTree).Single(row => CompiledVehicleRig.BoneIndex(row.Source) == 2);
            check("prepared appended bone rebuilds canonical skeleton rows and preserves selected bone index", insertedRig.Count == 3 && Glyphs() == oldGlyphCount + 2
                && CompiledVehicleRig.BoneIndex(host.Selection.Active?.Source) == 1 && !ReferenceEquals(originalTree, insertedTree)
                && ReferenceEquals(insertedBone.Source, insertedRig.Skeleton.Bones[2].Source));
            var insertedPixels = Draw();
            check("prepared inserted bone changes actual offscreen skeleton overlay pixels", !insertedPixels.SequenceEqual(beforePixels));
            var insertedWorld = ((IFrameNode)insertedBone.Source!).WorldTransform.Translation;
            var picked = host.Streamer.PickGlyph(insertedWorld + new Vector3(0, -5, 0), Vector3.UnitY, out _, .03f);
            check("prepared inserted native bone participates in skeleton-only glyph picking", picked is not null && CompiledVehicleRig.BoneIndex(picked.Source) == 2);
            host.Selection.SetSelection([insertedBone], insertedBone);
            check("prepared native bone selection draws its actual accent glyph", !Draw().SequenceEqual(insertedPixels));
            if (host.ReparentLinkedProxyBone(dock, 2, 0) is { } parentRefusal) throw new InvalidDataException(parentRefusal);
            check("prepared bone reparent updates tree hierarchy and keeps model-space bone selection", document.VehicleRig(dockFrame)!.Parent(2) == 0
                && CompiledVehicleRig.BoneIndex(host.Selection.Active?.Source) == 2 && host.Selection.Active?.Parent?.Source is { } parentSource && CompiledVehicleRig.BoneIndex(parentSource) == 0);
            host.History.Undo(); host.History.Redo(); document.SaveWorkingCopy(); host.History.Undo(); host.History.Undo();
            bool removedBoneSelectionFallsBack = ReferenceEquals(host.Selection.Active?.Source, dockFrame);
            host.Selection.SetSelection([originalBone], originalBone);
            check("prepared advanced rig undo restores old adapters GPU and pixels with original selection", removedBoneSelectionFallsBack && NativeExact() && ReferenceEquals(rig, document.VehicleRig(dockFrame))
                && ReferenceEquals(originalTree, dock.Children.Single(row => row.Kind == "Skeleton")) && renderer.Meshes.SequenceEqual(originalMeshes) && Draw().SequenceEqual(beforePixels));
            host.History.Redo(); host.History.Redo();
            check("prepared advanced rig redo keeps skeleton tree adapters canonical", CompiledVehicleRig.BoneIndex(host.Selection.Active?.Source) == 1
                && Walk(dock.Children.Single(row => row.Kind == "Skeleton")).Where(row => row.Kind == "Bone").All(row =>
                    ReferenceEquals(row.Source, document.VehicleRig(dockFrame)!.Skeleton.Bones[CompiledVehicleRig.BoneIndex(row.Source)].Source)));
            host.History.Undo(); host.History.Undo();
        }
        finally
        {
            host.BeforeLinkedProxyRigGpuAllocation = null;
            while (host.History.UndoCount > originalHistory) host.History.Undo();
            initialized.Undo(); initialized.Discard(); host.History.Undo(); document.SaveWorkingCopy(); host.History.Clear();
        }
    }
}
