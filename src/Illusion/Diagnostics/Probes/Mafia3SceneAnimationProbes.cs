using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Frames;
using Illusion.Assets.Sds;
using Illusion.Formats.Animations;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledSceneAnimation(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "combinables", "character_persistent.sds"));
        var animation = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "tables", "ingame.sds"));
        var skeleton = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "basic_anim", "skeletons.sds"));
        if (!archive.Exists || !animation.Exists || !skeleton.Exists) return;
        var document = new CompiledSceneDocument(archive);
        var before = document.Graph.Resources.ToDictionary(r => r, r => r.ToArray());
        static IEnumerable<SdsFrameNode> Walk(IEnumerable<SdsFrameNode> rows)
        { foreach (var row in rows) { yield return row; foreach (var child in Walk(row.Children)) yield return child; } }
        var frame = Walk(document.Roots).Where(n => n.Name == "ma_debug_character_000")
            .Select(n => n.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().First(n => n.VehicleRig is not null);
        var rig = frame.VehicleRig!;
        var clips = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(animation)).GetFiles("hkAnimation")
            .SelectMany(p => HavokAnimationClip.Read(File.ReadAllBytes(p)));
        var clip = clips.First(c => c.SkeletonName == "prod_male");
        var nativeSkeleton = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(skeleton)).GetFiles("hkAnimation")
            .SelectMany(p => HavokAnimationClip.ReadSkeletons(File.ReadAllBytes(p))).First(s => s.Name == clip.SkeletonName);
        var authored = rig.LiveRest.ToArray();
        var authoredGlyphs = HelperGlyphBuilder.BuildRig([rig.Skeleton]).Segments;
        using var binding = new CompiledAnimationBinding(rig, new HavokAnimationPreview(clip, nativeSkeleton));
        check("native scene animation binds render bone names and root", binding.MappedBones == 102 && binding.RenderBones == 108
            && binding.RenderToAnimation[0] == 0);
        var start = binding.Sample(0);
        binding.Seek(clip.Duration * .37f);
        var pose = rig.LiveRest.ToArray();
        check("native scene animation drives actual skinned mesh pose", document.Meshes.Any(m => ReferenceEquals(m.LiveRest, rig.LiveRest))
            && !pose.SequenceEqual(start) && pose.All(m => float.IsFinite(m.M11) && float.IsFinite(m.M41)));
        int locked = 0;
        bool translations = true;
        for (int i = 0; i < rig.Count; i++)
        {
            int source = binding.RenderToAnimation[i];
            if (source < 0 || !nativeSkeleton.TranslationLocked[source]) continue;
            locked++;
            var local = pose[i];
            if (rig.Parent(i) is >= 0 and var parent)
            { Matrix4x4.Invert(pose[parent], out var inverse); local *= inverse; }
            translations &= Vector3.Distance(local.Translation, rig.LocalRest(i).Translation) < .0001f;
        }
        check("native scene animation preserves locked render translations", locked > 0 && translations);
        check("native scene animation overlay follows live pose", !HelperGlyphBuilder.BuildRig([rig.Skeleton]).Segments.SequenceEqual(authoredGlyphs));
        binding.Seek(0);
        check("native scene animation rewind is deterministic", rig.LiveRest.SequenceEqual(start));
        binding.Dispose();
        check("native scene animation stop restores authored pose and overlay", rig.LiveRest.SequenceEqual(authored)
            && HelperGlyphBuilder.BuildRig([rig.Skeleton]).Segments.SequenceEqual(authoredGlyphs));
        bool unrelatedRefused = false;
        try
        {
            var unrelated = nativeSkeleton with
            { BoneNames = nativeSkeleton.BoneNames.Select((_, i) => $"unrelated_animation_bone_{i}").ToArray() };
            using var refused = new CompiledAnimationBinding(rig, new HavokAnimationPreview(clip, unrelated));
        }
        catch (NotSupportedException) { unrelatedRefused = true; }
        check("native scene animation refuses unrelated skeleton binding", unrelatedRefused && rig.LiveRest.SequenceEqual(authored));
        bool authoredChangeRefused = false;
        using (var editingBinding = new CompiledAnimationBinding(rig, new HavokAnimationPreview(clip, nativeSkeleton)))
        {
            editingBinding.Seek(clip.Duration * .37f);
            var bone = rig.BoneSource(1);
            var local = bone.LocalTransform;
            try
            {
                var changed = local; changed.M41 += .125f; bone.LocalTransform = changed;
                try { editingBinding.Seek(0); }
                catch (NotSupportedException) { authoredChangeRefused = true; }
            }
            finally { bone.LocalTransform = local; }
        }
        check("native scene playback stops rather than hiding authored bone edits", authoredChangeRefused && rig.LiveRest.SequenceEqual(authored));
        check("native scene animation never mutates compiled resources", before.All(p => p.Key.ToArray().AsSpan().SequenceEqual(p.Value)
            && File.ReadAllBytes(p.Key.Path).AsSpan().SequenceEqual(p.Value)));
    }
}
