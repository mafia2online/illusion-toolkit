using System.Text;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Compiled;

/// <summary>Native named animation float slots to sparse morph targets. Sampling is transient and never edits resources.</summary>
public sealed class CompiledMorphAnimationBinding
{
    private readonly HavokAnimationPreview _preview;
    private readonly int[] _slotToTarget;
    public int MappedSlots => _slotToTarget.Count(target => target >= 0);
    public int MappedTargets => _slotToTarget.Where(target => target >= 0).Distinct().Count();
    public CompiledMorphAnimationBinding(CompiledSceneDocument document, CompiledMeshBinding binding, HavokAnimationPreview preview, int lod = 0)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(preview);
        _preview = preview;
        if (preview.Clip.BlendHint != 0)
            throw new NotSupportedException("Additive facial float blending requires a native animation blend stack and is not previewed as an isolated clip.");
        var targets = document.MorphChannels(binding, lod).TargetIds;
        var indices = new Dictionary<ulong, int>();
        for (int i = 0; i < targets.Length; i++)
            if (targets[i] != 0) indices.TryAdd(targets[i], i); // Native lower_bound chooses the first duplicate ID.
        _slotToTarget = preview.NativeSkeleton.FloatSlotNames
            .Select(name => indices.GetValueOrDefault(Fnv64.Hash(name, Encoding.UTF8), -1)).ToArray();
        if (preview.Clip.FloatTrackToSlot.Any(slot => slot < -1 || slot >= _slotToTarget.Length))
            throw new InvalidDataException("Animation float mapping exceeds the named skeleton float slots.");
    }
    /// <summary>Native C_MorphsComponent values: sampled skeleton floats divided by 100, without clamping.</summary>
    public IReadOnlyDictionary<int, float> Sample(float seconds)
    {
        // hkaPose::setToReferencePose zeros float slots rather than copying referenceFloats.
        var values = new float[_slotToTarget.Length];
        var tracks = _preview.Clip.SampleFloats(seconds);
        for (int i = 0; i < tracks.Length; i++)
            if (_preview.Clip.FloatTrackToSlot[i] is >= 0 and var slot) values[slot] = tracks[i];
        var result = new Dictionary<int, float>();
        for (int i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i])) throw new InvalidDataException("Native facial animation produced a non-finite float.");
            if (_slotToTarget[i] >= 0) result[_slotToTarget[i]] = values[i] / 100f;
        }
        return result;
    }
}
