using System.Numerics;

namespace Illusion.Formats.Animations;

public sealed partial class HavokAnimationClip
{
    /// <summary>Applies a full-weight native additive track to a reference parent-local transform.</summary>
    public static Matrix4x4 ApplyAdditive(Matrix4x4 reference, Matrix4x4 delta, byte blendHint)
    {
        if (blendHint is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(blendHint));
        if (!Matrix4x4.Decompose(reference, out var baseScale, out var baseRotation, out var baseTranslation)
            || !Matrix4x4.Decompose(delta, out var deltaScale, out var deltaRotation, out var deltaTranslation))
            throw new InvalidDataException("Native additive pose is not a TRS transform.");
        // Native hkaAdditiveAnimationUtility::applyAdditiveTransform deliberately does not scale translation.
        var translation = blendHint == 2
            ? baseTranslation + Vector3.Transform(deltaTranslation, baseRotation)
            : deltaTranslation + Vector3.Transform(baseTranslation, deltaRotation);
        var rotation = blendHint == 2 ? baseRotation * deltaRotation : deltaRotation * baseRotation;
        return Matrix4x4.CreateScale(baseScale * deltaScale)
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation))
            * Matrix4x4.CreateTranslation(translation);
    }
}
