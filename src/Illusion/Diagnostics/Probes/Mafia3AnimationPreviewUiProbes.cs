using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Formats.Animations;
using Illusion.Formats.Archive;
using Illusion.Views;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledAnimationPreviewUi(Action<string, bool> check)
    {
        var head = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "combinables", "auto", "fe_head_004_cassandra_fe_head_004_cassandra.sds"));
        var animation = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "cutscenes", "bios", "fmv_bio02_passion_cassandra_03.sds"));
        var skeleton = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "basic_anim", "skeletons.sds"));
        if (!head.Exists || !animation.Exists || !skeleton.Exists) return;
        string fixture = Path.Combine(MafiaEnvironment.GameRoot, "animation-ui-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var copiedHead = new FileInfo(Path.Combine(fixture, head.Name));
        File.Copy(head.FullName, copiedHead.FullName);
        string extracted = MafiaEnvironment.ExtractedDir(copiedHead);
        string output = Path.Combine(fixture, "frame.png");
        var app = Application.Current; var shutdown = app?.ShutdownMode;
        if (app is not null) app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AnimationPreviewWindow? window = null;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            // Decode independent copied bytes, not a synthetic animation or skeleton.
            string clipPath = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(animation)).GetFiles("hkAnimation")
                .First(path => HavokAnimationClip.Read(File.ReadAllBytes(path)).Any(c => c.SkeletonName == "prod_female"));
            string skeletonPath = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(skeleton)).GetFiles("hkAnimation")
                .First(path => HavokAnimationClip.ReadSkeletons(File.ReadAllBytes(path)).Any(s => s.Name == "prod_female"));
            byte[] clipBytes = File.ReadAllBytes(clipPath), skeletonBytes = File.ReadAllBytes(skeletonPath);
            string copiedClip = Path.Combine(fixture, "clip.hkx"), copiedSkeleton = Path.Combine(fixture, "skeleton.hkx");
            File.WriteAllBytes(copiedClip, clipBytes); File.WriteAllBytes(copiedSkeleton, skeletonBytes);
            var clip = HavokAnimationClip.Read(File.ReadAllBytes(copiedClip)).First(c => c.SkeletonName == "prod_female");
            var nativeSkeleton = HavokAnimationClip.ReadSkeletons(File.ReadAllBytes(copiedSkeleton)).First(s => s.Name == "prod_female");
            var document = new CompiledSceneDocument(copiedHead);
            static IEnumerable<SdsFrameNode> Walk(IEnumerable<SdsFrameNode> rows)
            { foreach (var row in rows) { yield return row; foreach (var child in Walk(row.Children)) yield return child; } }
            var rig = Walk(document.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
                .Select(frame => frame.VehicleRig).First(value => value is not null)!;
            var authored = rig.LiveRest.ToArray();
            var bytes = document.Graph.Resources.ToDictionary(resource => resource, resource => resource.ToArray());
            window = new AnimationPreviewWindow();
            var previews = (List<(string Label, HavokAnimationPreview Preview)>)typeof(AnimationPreviewWindow).GetField("_previews", flags)!.GetValue(window)!;
            previews.Add(("Copied native Cassandra", new HavokAnimationPreview(clip, nativeSkeleton)));
            ((List<(int Index, int Count)>)typeof(AnimationPreviewWindow).GetField("_skeletonCandidates", flags)!.GetValue(window)!).Add((0, 1));
            var clips = (ComboBox)typeof(AnimationPreviewWindow).GetField("_clips", flags)!.GetValue(window)!;
            clips.Items.Add("Copied native Cassandra"); window.SelectClip(0);
            // Invoke Loaded without Show/Activate: actual own GPU and render target, no desktop surface.
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.SaveFrame(output);
            check("native animation unshown window renders actual copied clip to PNG", !window.IsVisible && new FileInfo(output).Length > 100);
            int clears = 0, changes = 0; bool refuse = false;
            window.BindToSceneRig(rig, () => changes++, () => true,
                (_, _) => { if (refuse) throw new NotSupportedException("probe forced facial callback refusal"); }, () => clears++);
            window.Seek(20);
            check("native animation binding changes transient rig pose", window.BoundBones > 0 && !rig.LiveRest.SequenceEqual(authored) && changes > 0);
            window.ClearSceneBinding();
            check("native animation explicit unbind restores authored pose and clears morph callback", window.BoundBones == 0 && clears == 1 && rig.LiveRest.SequenceEqual(authored));
            bool currentRig = true; int staleClears = 0;
            window.BindToSceneRig(rig, () => changes++, () => currentRig, morphClear: () => staleClears++);
            window.Seek(20); window.SetPlaying(false); currentRig = false;
            typeof(AnimationPreviewWindow).GetMethod("ValidateSceneBinding",flags)!.Invoke(window,null);
            check("paused animation invalidation clears stale rig binding and restores its transient pose",window.BoundBones == 0
                && staleClears == 1 && rig.LiveRest.SequenceEqual(authored) && window.Status.Contains("no scene rig",StringComparison.Ordinal));
            window.BindToSceneRig(rig, () => changes++, () => true,
                (_, _) => { if (refuse) throw new NotSupportedException("probe forced facial callback refusal"); }, () => clears++);
            window.SetPlaying(true); refuse = true; File.Delete(output);
            bool refused = false;
            try { window.SaveFrame(output); } catch (InvalidOperationException ex) { refused = ex.Message.Contains("forced facial callback refusal", StringComparison.Ordinal); }
            var image = (Image)typeof(AnimationPreviewWindow).GetField("_image", flags)!.GetValue(window)!;
            check("native animation failed bound morph sample clears old bitmap and refuses stale PNG", refused && image.Source is null && !File.Exists(output));
            check("native animation failed bound sample stops play and restores both transient layers", !window.IsPlaying && window.BoundBones == 0 && clears == 2 && rig.LiveRest.SequenceEqual(authored));
            refuse = false; window.Seek(0); window.SaveFrame(output);
            check("native animation failure can recover with independent skeletal playback", image.Source is BitmapSource && new FileInfo(output).Length > 100);
            window.Close(); bool closed = false;
            try { window.SaveFrame(output); } catch (InvalidOperationException ex) { closed = ex.Message.Contains("closed", StringComparison.Ordinal); }
            check("closed native animation window refuses frame export", closed);
            check("native animation preview leaves all copied resource and clip bytes exact", bytes.All(pair => pair.Key.ToArray().SequenceEqual(pair.Value))
                && File.ReadAllBytes(copiedClip).SequenceEqual(clipBytes) && File.ReadAllBytes(copiedSkeleton).SequenceEqual(skeletonBytes));
        }
        finally
        {
            window?.Close();
            if (app is not null && shutdown is { } previous) app.ShutdownMode = previous;
            // Only delete the two uniquely named probe-owned directories after verifying containment.
            string root = Path.GetFullPath(MafiaEnvironment.GameRoot) + Path.DirectorySeparatorChar;
            foreach (string directory in new[] { extracted, fixture })
                if (Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
