using System.IO;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Formats.Animations;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledMorphAnimation(Action<string, bool> check)
    {
        var head = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "combinables", "auto", "fe_head_004_cassandra_fe_head_004_cassandra.sds"));
        var animation = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "cutscenes", "bios", "fmv_bio02_passion_cassandra_03.sds"));
        var skeleton = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "basic_anim", "skeletons.sds"));
        if (!head.Exists || !animation.Exists || !skeleton.Exists) return;
        var clip = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(animation)).GetFiles("hkAnimation")
            .SelectMany(path => HavokAnimationClip.Read(File.ReadAllBytes(path))).Single(c => c.SkeletonName == "prod_female");
        var nativeSkeleton = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(skeleton)).GetFiles("hkAnimation")
            .SelectMany(path => HavokAnimationClip.ReadSkeletons(File.ReadAllBytes(path))).First(s => s.Name == clip.SkeletonName);
        check("native facial clip exposes actual scalar tracks and named skeleton slots",
            clip.FrameCount == 2842 && clip.FloatTrackCount == 154 && nativeSkeleton.FloatSlotNames.Length == 167
            && clip.FloatTrackToSlot.Contains((short)-1));
        // Independent copied-native readNURBSCurve oracle checkpoints, including a block boundary and overshoot.
        var checkpoints = new (int Frame, int Track, float Value)[]
            { (0, 30, 2.2540376f), (0, 54, .0014796286f), (255, 0, 40.648582f),
              (510, 30, 21.48093f), (1020, 0, 127.23906f), (1020, 77, 24.768923f) };
        check("native scalar spline samples match original machine code checkpoints",
            checkpoints.All(point => Math.Abs(clip.SampleFloats(clip.Duration * point.Frame / (clip.FrameCount - 1))[point.Track] - point.Value) < .002f));
        float minimum = float.MaxValue, maximum = float.MinValue;
        for (int frame = 0; frame < clip.FrameCount; frame++)
        {
            var values = clip.SampleFloats(clip.Duration * frame / (clip.FrameCount - 1));
            minimum = Math.Min(minimum, values.Min()); maximum = Math.Max(maximum, values.Max());
            if (values.Any(value => !float.IsFinite(value))) throw new InvalidDataException("Non-finite facial float sample.");
        }
        check("actual facial scalar tracks retain native negative values and overshoot", minimum < -3 && maximum > 150);
        var document = new CompiledSceneDocument(head);
        var binding = EntityCreationRows(document.Roots).Select(n => n.Source)
            .OfType<CompiledSceneDocument.CompiledFrameNode>().Select(frame => frame.GeometryBinding).First(b => b is not null)!;
        var baseline = document.Graph.Resources.ToDictionary(resource => resource, resource => resource.ToArray());
        var morph = new CompiledMorphAnimationBinding(document, binding, new HavokAnimationPreview(clip, nativeSkeleton));
        var weights = morph.Sample(20);
        var original = binding.DecodeLod(0)!;
        var preview = document.PreviewAnimatedMorph(binding, 0, weights);
        check("native float hashes bind actual facial morph IDs and divide by one hundred without clamping",
            morph.MappedSlots == 151 && morph.MappedTargets == 149 && weights.Count(pair => pair.Value != 0) == 65
            && preview.Positions.Where((position, index) => position != original.Positions[index]).Count() == 6250);
        bool manualRefused = false;
        try { document.PreviewMorph(binding, 0, new Dictionary<int, float> { [84] = 1.5f }); }
        catch (ArgumentOutOfRangeException) { manualRefused = true; }
        var overshoot = document.PreviewAnimatedMorph(binding, 0, new Dictionary<int, float> { [84] = 1.5f, [48] = -.04f });
        check("animated signed weights remain finite while manual morph limits are preserved", manualRefused
            && overshoot.Positions.Concat(overshoot.Normals).Concat(overshoot.Tangents!).Concat(overshoot.Binormals!)
                .All(vector => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z)));
        var clear = document.PreviewAnimatedMorph(binding, 0, new Dictionary<int, float>());
        check("facial animation clear restores exact geometry and leaves all native resources unchanged",
            clear.Positions.SequenceEqual(original.Positions) && clear.Normals.SequenceEqual(original.Normals)
            && clear.Tangents!.SequenceEqual(original.Tangents!) && clear.Binormals!.SequenceEqual(original.Binormals!)
            && baseline.All(pair => pair.Key.ToArray().AsSpan().SequenceEqual(pair.Value)));
        var noFloatArchive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "tables", "ingame.sds"));
        if (noFloatArchive.Exists)
        {
            var noFloat = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(noFloatArchive)).GetFiles("hkAnimation")
                .SelectMany(path => HavokAnimationClip.Read(File.ReadAllBytes(path))).First(c => c.SkeletonName == "prod_male");
            var male = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(skeleton)).GetFiles("hkAnimation")
                .SelectMany(path => HavokAnimationClip.ReadSkeletons(File.ReadAllBytes(path))).First(s => s.Name == "prod_male");
            var empty = new CompiledMorphAnimationBinding(document, binding, new HavokAnimationPreview(noFloat, male));
            check("native clips without scalar tracks reset every named morph slot to zero",
                empty.Sample(1).Values.All(value => value == 0));
        }
    }
}
