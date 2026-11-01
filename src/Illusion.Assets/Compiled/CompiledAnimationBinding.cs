using System.Numerics;
using System.Text;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Compiled;

/// <summary>Native animation-to-render skeleton binding, with transient render-only playback.</summary>
public sealed class CompiledAnimationBinding : IDisposable
{
    private readonly CompiledVehicleRig _rig;
    private readonly HavokAnimationPreview _preview;
    private readonly int[] _mapping;
    private readonly long _authoredRevision;
    public int MappedBones => _mapping.Count(i => i >= 0);
    public int RenderBones => _rig.Count;
    public IReadOnlyList<int> RenderToAnimation => _mapping;
    public CompiledAnimationBinding(CompiledVehicleRig rig, HavokAnimationPreview preview)
    {
        _rig = rig; _preview = preview; _authoredRevision = rig.AuthoredRevision;
        var names = preview.NativeSkeleton.BoneNames.Select((name, index) => (Hash: Fnv64.Hash(name, Encoding.UTF8), index))
            .ToDictionary(n => n.Hash, n => n.index);
        _mapping = Enumerable.Range(0, rig.Count).Select(i => i == 0 ? 0 : names.GetValueOrDefault(rig.NameHash(i), -1)).ToArray();
        if (rig.Count < 2 || MappedBones < 2 || (MappedBones - 1) * 2 < rig.Count - 1)
            throw new NotSupportedException("The native animation skeleton does not name enough bones of this render rig.");
    }
    public Matrix4x4[] Sample(float seconds)
    {
        if (_rig.AuthoredRevision != _authoredRevision)
            throw new NotSupportedException("The authored skeleton changed; bind the animation again after finishing the edit.");
        var animation = (Matrix4x4[])_preview.NativeSkeleton.ReferenceLocal.Clone();
        var tracks = _preview.Clip.Sample(seconds);
        for (int i = 0; i < tracks.Length; i++)
            if (_preview.Clip.TrackToBone[i] is >= 0 and var target)
                animation[target] = _preview.Clip.BlendHint == 0 ? tracks[i]
                    : Formats.Animations.HavokAnimationClip.ApplyAdditive(animation[target], tracks[i], _preview.Clip.BlendHint);
        var models = new Matrix4x4[_rig.Count];
        var completed = new bool[_rig.Count];
        Matrix4x4 Model(int index)
        {
            if (completed[index]) return models[index];
            var local = _rig.LocalRest(index);
            if (_mapping[index] is >= 0 and var source)
            {
                if (!Matrix4x4.Decompose(animation[source], out _, out var rotation, out var translation))
                    throw new InvalidDataException("Native animation transform cannot be decomposed.");
                if (_preview.NativeSkeleton.TranslationLocked is { Length: > 0 } locked && locked[source])
                    translation = local.Translation;
                // ApplyHavokAnimBuffer copies rotation/translation; animation scale is deliberately ignored.
                local = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
            }
            int parent = _rig.Parent(index);
            models[index] = parent < 0 ? local : local * Model(parent);
            completed[index] = true;
            return models[index];
        }
        for (int i = 0; i < models.Length; i++) _ = Model(i);
        return models;
    }
    public void Seek(float seconds) => _rig.SetAnimationPose(Sample(seconds));
    public void Dispose() => _rig.SetAnimationPose(null);
}
