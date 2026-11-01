using System.IO;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Animations;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestPreparedProxyAnimation(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_french_east_block_b_lod0.sds"));
        var sourceArchive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city_crash", "bc_city_crash.sds"));
        var skeletonArchive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "basic_anim", "skeletons.sds"));
        if (!archive.Exists || !sourceArchive.Exists || !skeletonArchive.Exists) return;
        var sourceFolder = SdsMeshLoader.EnsureExtracted(sourceArchive);
        var skeletons = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(skeletonArchive)).GetFiles("hkAnimation")
            .SelectMany(path => HavokAnimationClip.ReadSkeletons(File.ReadAllBytes(path))).ToArray();
        // Explicit native named-bone signature; Trace alone identifies hundreds of unrelated props.
        var skeleton = skeletons.Single(s => s.Name == "Trace" && s.BoneNames.SequenceEqual(new[] { "Trace", "BoneDrawer02", "BoneDrawer01", "cabinet_bone" }));
        var clip = HavokAnimationClip.Read(File.ReadAllBytes(Path.Combine(sourceFolder, "hkAnimation_2198_0000000000000000.bin"))).Single();
        var document = new CompiledSceneDocument(archive);
        if (document.PreparedLinkedProxyGroups.Count != 0) return;
        static IEnumerable<ISceneHierarchyNode> Walk(IEnumerable<ISceneHierarchyNode> rows)
        { foreach (var row in rows) { yield return row; foreach (var child in Walk(row.Children)) yield return child; } }
        var owner = Walk(document.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().First(frame => frame.SceneIdentity == "guid:0F947BBBC7BC8574");
        var preparation = document.PrepareLinkedProxyTransforms(document.InspectLinkedProxyPreparation(owner, out var refusal) ?? throw new InvalidDataException(refusal), out refusal)
            ?? throw new InvalidDataException(refusal);
        IFrameDuplication? group = null, imported = null;
        try
        {
            group = document.CreatePreparedLinkedProxyGroup(owner, "paired_animation_probe", out refusal) ?? throw new InvalidDataException(refusal);
            imported = document.ImportPreparedSkinnedProp(Path.Combine(sourceFolder, "Generic_4029_6E1E5C7CA4DCBFA3.ires.compiled"), 0x06A9E8CFD6F580AA,
                owner, (IFrameNode)group.Root.Source!, out refusal) ?? throw new InvalidDataException(refusal);
            var targets = document.GetPreparedAnimationTargets((IFrameNode)imported.Root.Source!, out refusal);
            var rigs = targets.Select(target => target.VehicleRig!).ToArray();
            var authored = rigs.Select(rig => rig.LiveRest.ToArray()).ToArray();
            var hashes = document.Graph.Resources.ToDictionary(resource => resource, resource => (Bytes: SHA256.HashData(resource.ToArray()), resource.Revision));
            using var binding = new CompiledAnimationBinding(rigs, new HavokAnimationPreview(clip, skeleton));
            check("paired animation explicitly selects a four-bone Trace candidate for cabinet retargeting", skeletons.Count(s => s.Name == "Trace") > 1
                && rigs.Length == 2 && binding.TargetCount == 2 && binding.MappedBones == 4 && binding.RenderBones == 5);
            binding.Seek(0);
            var initial = rigs.Select(rig => rig.LiveRest.ToArray()).ToArray();
            binding.Seek(clip.Duration * .63f);
            check("paired native cabinet clip synchronizes fine and far current bone matrices", rigs[0].LiveRest.SequenceEqual(rigs[1].LiveRest)
                && !rigs[0].LiveRest.SequenceEqual(initial[0]));
            binding.Seek(0);
            check("paired animation rewind restores both deterministic native poses", rigs.Select((rig, index) => rig.LiveRest.SequenceEqual(initial[index])).All(value => value));
            var restore = rigs[1].CaptureRevertedAuthoredMutationCheckpoint();
            rigs[1].NotifyAuthoredMutation();
            var before = rigs.Select(rig => rig.LiveRest.ToArray()).ToArray(); bool refused = false;
            try { binding.Seek(clip.Duration * .3f); } catch (NotSupportedException) { refused = true; }
            finally { restore(); }
            check("paired animation stages every target before commit when the second rig is stale", refused && rigs[0].LiveRest.SequenceEqual(before[0]));
            binding.Dispose();
            check("paired animation unbind restores both authored rigs without native bytes or revision changes", rigs.Select((rig, index) => rig.LiveRest.SequenceEqual(authored[index])).All(value => value)
                && hashes.All(pair => pair.Key.Revision == pair.Value.Revision && SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value.Bytes)));
        }
        finally { imported?.Detach(); group?.Detach(); preparation.Undo(); document.SaveWorkingCopy(); }
    }
}
