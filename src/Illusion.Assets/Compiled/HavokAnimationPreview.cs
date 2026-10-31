using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Animations;

namespace Illusion.Assets.Compiled;

/// <summary>Read-only native clip playback against its named Havok skeleton. Never edits a compiled resource.</summary>
public sealed class HavokAnimationPreview
{
    public HavokAnimationClip Clip { get; }
    public HavokAnimationSkeleton NativeSkeleton { get; }
    public HavokAnimationPreview(HavokAnimationClip clip, HavokAnimationSkeleton skeleton)
    {
        if (!clip.SkeletonName.Equals(skeleton.Name, StringComparison.Ordinal))
            throw new ArgumentException("The animation binding names a different skeleton.", nameof(skeleton));
        if (clip.TrackToBone.Any(i => i < -1 || i >= skeleton.BoneNames.Length))
            throw new InvalidDataException("Animation track mapping exceeds the bound skeleton.");
        Clip = clip; NativeSkeleton = skeleton;
    }

    public SkeletonData Sample(float seconds, Matrix4x4? world = null)
    {
        var locals = (Matrix4x4[])NativeSkeleton.ReferenceLocal.Clone();
        var tracks = Clip.Sample(seconds);
        for (int i = 0; i < tracks.Length; i++)
            if (Clip.TrackToBone[i] is >= 0 and var target) locals[target] = Clip.BlendHint == 0 ? tracks[i]
                : HavokAnimationClip.ApplyAdditive(locals[target], tracks[i], Clip.BlendHint);
        var models = new Matrix4x4[locals.Length];
        for (int i = 0; i < models.Length; i++)
        {
            models[i] = locals[i];
            if (NativeSkeleton.Parents[i] >= 0) models[i] *= models[NativeSkeleton.Parents[i]];
        }
        return new SkeletonData { OwnerName = NativeSkeleton.Name, World = world ?? Matrix4x4.Identity,
            Attachments = [], Bones = Enumerable.Range(0, models.Length)
                .Select(i => new BoneData(NativeSkeleton.BoneNames[i], NativeSkeleton.Parents[i], models[i])).ToArray() };
    }
}
