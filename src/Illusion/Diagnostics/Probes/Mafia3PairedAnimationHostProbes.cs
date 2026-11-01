using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Formats.Animations;
using Illusion.Formats.Archive;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Scene;
using Illusion.Viewport;
using Illusion.Views;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestPreparedProxyAnimationHost(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_french_east_block_b_lod0.sds"));
        var skeletonArchive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "basic_anim", "skeletons.sds"));
        string source = Path.Combine(MafiaEnvironment.ResourcesFolder!, "sds_retail", "city_crash", "bc_city_crash.sds", "Generic_4029_6E1E5C7CA4DCBFA3.ires.compiled");
        if (!archive.Exists || !skeletonArchive.Exists || !File.Exists(source)) return;
        var document = new CompiledSceneDocument(archive); if (document.PreparedLinkedProxyGroups.Count != 0) return;
        var nativeSkeletons = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(skeletonArchive)).GetFiles("hkAnimation")
            .SelectMany(path => HavokAnimationClip.ReadSkeletons(File.ReadAllBytes(path))).ToArray();
        int candidate = Array.FindIndex(nativeSkeletons, skeleton => skeleton.Name == "Trace"
            && skeleton.BoneNames.SequenceEqual(new[] { "Trace", "BoneDrawer02", "BoneDrawer01", "cabinet_bone" }));
        var clip = HavokAnimationClip.Read(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(source)!, "hkAnimation_2198_0000000000000000.bin"))).Single();
        using var gpu = new GpuContext(); using var host = new D3DImageHost();
        var renderer = new SceneRenderer(gpu) { ShowSky = false };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ViewportControl).GetProperty("Renderer", flags)!.SetValue(host, renderer);
        using var target = new SharedRenderTarget(gpu, 192, 192);
        var root = new SceneNode("FrameResource", "FrameResource", true) { Source = document }; host.Roots.Add(root);
        var leaves = new List<SceneNode>(); foreach (var native in document.Roots) root.AddChild(SceneTree.BuildSceneTree(native, leaves));
        foreach (var leaf in leaves) if (leaf.Pending is { } data) { var mesh = renderer.AddMesh(data); mesh.Owner = leaf; leaf.Mesh = mesh; leaf.Pending = null; }
        host.Tree.MeshCount = renderer.Meshes.Count;
        static IEnumerable<SceneNode> Walk(SceneNode row) { yield return row; foreach (var child in row.Children) foreach (var descendant in Walk(child)) yield return descendant; }
        var owner = Walk(root).First(row => row.Source is CompiledSceneDocument.CompiledFrameNode frame && frame.SceneIdentity == "guid:0F947BBBC7BC8574");
        var refusal = host.InspectCompiledLinkedProxy(owner, out var plan);
        if (refusal is not null || plan is null) throw new InvalidDataException(refusal);
        if (host.PrepareCompiledLinkedProxy(owner, plan) is { } preparation) throw new InvalidDataException(preparation);
        var application = Application.Current; var shutdown = application?.ShutdownMode;
        if (application is not null) application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AnimationPreviewWindow? window = null;
        try
        {
            var group = host.CreateLinkedProxyGroup(owner, "paired_animation_host", out var parent);
            if (group is not null || parent is null) throw new InvalidDataException(group);
            foreach (var mesh in renderer.Meshes) mesh.Visible = false;
            var import = host.ImportLinkedProxySkin(owner, parent, source, 0x06A9E8CFD6F580AA, out var imported);
            if (import is not null || imported is null) throw new InvalidDataException(import);
            var targets = document.GetPreparedAnimationTargets((CompiledSceneDocument.CompiledFrameNode)imported.Source!, out _);
            var rigs = targets.Select(frame => frame.VehicleRig!).ToArray(); var authored = rigs.Select(rig => rig.LiveRest.ToArray()).ToArray();
            var bytes = document.Graph.Resources.ToDictionary(resource => resource, resource => (Hash: SHA256.HashData(resource.ToArray()), resource.Revision));
            window = new AnimationPreviewWindow();
            ((List<(string Label, HavokAnimationPreview Preview)>)typeof(AnimationPreviewWindow).GetField("_previews", flags)!.GetValue(window)!)
                .Add(("Explicit retail cabinet Trace candidate", new HavokAnimationPreview(clip, nativeSkeletons[candidate])));
            ((List<(int Index, int Count)>)typeof(AnimationPreviewWindow).GetField("_skeletonCandidates", flags)!.GetValue(window)!).Add((candidate, 2));
            ((ComboBox)typeof(AnimationPreviewWindow).GetField("_clips", flags)!.GetValue(window)!).Items.Add("Explicit retail cabinet Trace candidate");
            bool ambiguous = false; try { window.SelectClip(0); } catch (NotSupportedException) { ambiguous = true; }
            check("native animation duplicate-name candidates require explicit programmatic selection", ambiguous && window.SelectedClip == -1);
            window.SelectClip(0, candidate); window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            if (host.BindCompiledAnimation(window, imported, includeMorphs: false) is { } bind) throw new InvalidDataException(bind);
            host.Selection.Select(null);
            GpuMesh Mesh(CompiledSceneDocument.CompiledFrameNode frame) => renderer.Meshes.First(mesh => mesh.Owner is SceneNode row
                && (row.GeometrySource ?? row.Source) is ICompiledMeshSource visual && ReferenceEquals(visual.CanonicalFrame, frame));
            var near = Mesh(targets[0]); var far = Mesh(targets[1]); var center = (near.BoundsMin + near.BoundsMax) * .5f;
            var direction = Vector3.Normalize(new Vector3(3, -4, 2)); renderer.Camera.Orthographic = true; renderer.Camera.OrthoHeight = 5;
            byte[] Draw(float distance) { renderer.Camera.LookAt(center + direction * distance, center); renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
            window.Seek(0); var nearStart = Draw(6); var farStart = Draw(1500);
            window.Seek(clip.Duration * .63f); var nearMoved = Draw(6); var farMoved = Draw(1500);
            check("prepared native animation binds both canonical fine and far render rigs", window.BoundRigCount == 2 && window.BoundBones == 4 && rigs[0].LiveRest.SequenceEqual(rigs[1].LiveRest));
            check("prepared native animation changes actual near and far skinned GPU pixels", !nearStart.SequenceEqual(nearMoved) && !farStart.SequenceEqual(farMoved) && !near.RenderVisible && far.RenderVisible);
            window.Seek(0);
            check("prepared native animation rewinds near and far GPU images exactly", Draw(6).SequenceEqual(nearStart) && Draw(1500).SequenceEqual(farStart));
            window.ClearSceneBinding();
            check("prepared animation unbind clears both poses and preserves every native byte and revision", rigs.Select((rig, index) => rig.LiveRest.SequenceEqual(authored[index])).All(value => value)
                && bytes.All(pair => pair.Key.Revision == pair.Value.Revision && SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value.Hash)));
            if (host.BindCompiledAnimation(window, imported, includeMorphs: false) is { } rebound) throw new InvalidDataException(rebound);
            window.Seek(clip.Duration * .63f);
            if (host.SetLinkedProxyBone(imported, 2, rigs[0].LocalRest(2) * Matrix4x4.CreateTranslation(.1f, 0, 0)) is { } edited) throw new InvalidDataException(edited);
            window.Seek(0);
            check("prepared animation clears both obsolete rig poses after authored skeleton copy on write", window.BoundRigCount == 0
                && rigs.Select((rig, index) => rig.LiveRest.SequenceEqual(authored[index])).All(value => value));
            host.History.Undo();
        }
        finally
        {
            window?.Close(); if (application is not null && shutdown is { } old) application.ShutdownMode = old;
            while (host.History.UndoCount > 0) host.History.Undo(); document.SaveWorkingCopy(); host.History.Clear();
        }
    }
}
