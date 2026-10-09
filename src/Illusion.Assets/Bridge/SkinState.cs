using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Mathematics;

namespace Illusion.Assets.Bridge;

/// <summary>
/// Everything about a skinned model that a geometry push changes BESIDE its buffers: the remap pools and the
/// face groups that draw from them, the per-bone face ranges with their pieces and hit boxes, and the
/// skeleton's own account of the pools with its per-bone boxes.
/// <para>
/// A push rewrites these, and they are not derived from the buffers alone — which pool a face group draws
/// from is a choice, and the vertex bytes only mean anything read through that choice. Undoing the buffers
/// while leaving them is how an undone push kept the old vertices and read them through the new pools: on a
/// car whose body had moved to another pool, one bone id in six came out as another bone. So a push carries
/// the state before and after, and puts back whichever belongs with the buffers it puts back.
/// </para>
/// A snapshot is never handed to the model itself: applying it installs copies, because the next push edits
/// the split table in place and a redo must still find this one as it was taken.
/// </summary>
internal sealed class SkinState
{
    private readonly FrameBlendInfo.BoneIndexInfo[]? _levels;
    private readonly FrameObjectModel.WeightedByMeshSplit[] _splits;
    private readonly FrameObjectModel.HitBoxInfo[] _hitBoxes;
    private readonly (int PhysSplitSize, int HitBoxSize, short NPhysSplits) _counters;

    private readonly bool _hasSkeleton;
    private readonly int[] _levelCounts = [];
    private readonly int _blendIds;
    private readonly byte[] _levelMasks = [];
    private readonly FrameSkeleton.MappingForBlendingInfo[] _mappings = [];

    private SkinState(FrameObjectModel model)
    {
        try { _levels = Copy(model.GetBlendInfoObject().BoneIndexInfos ?? []); }
        catch (Exception) { _levels = null; } // no blend info to read: nothing of it to put back either

        _splits = Copy(model.BlendMeshSplits ?? []);
        _hitBoxes = [.. (model.HitBoxes ?? []).Select(box => new FrameObjectModel.HitBoxInfo(box))];
        _counters = model.StoredSplitCounters;

        try
        {
            FrameSkeleton skeleton = model.GetSkeletonObject();
            _levelCounts = [.. skeleton.LodRemapIDCount ?? []];
            _blendIds = skeleton.NumBlendIDs;
            _levelMasks = [.. skeleton.BoneLODUsage ?? []];
            _mappings = Copy(skeleton.MappingForBlendingInfos ?? []);
            _hasSkeleton = true;
        }
        catch (Exception)
        {
            _hasSkeleton = false;
        }
    }

    /// <summary>The model's skin state as it stands.</summary>
    public static SkinState Take(FrameObjectModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new SkinState(model);
    }

    /// <summary>Puts the model's skin state back to what it was when this was taken.</summary>
    public void Apply(FrameObjectModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_levels != null)
        {
            try { model.GetBlendInfoObject().BoneIndexInfos = Copy(_levels); }
            catch (Exception) { /* the blend info it was read from is gone — nothing to write into */ }
        }

        model.BlendMeshSplits = Copy(_splits);
        model.HitBoxes = [.. _hitBoxes.Select(box => new FrameObjectModel.HitBoxInfo(box))];
        // The counters as they were stored, not recomputed: this is "as it was", and a shipped model is
        // entitled to whatever it shipped with.
        model.StoredSplitCounters = _counters;

        if (!_hasSkeleton) return;
        try
        {
            FrameSkeleton skeleton = model.GetSkeletonObject();
            skeleton.LodRemapIDCount = [.. _levelCounts];
            skeleton.NumBlendIDs = _blendIds;
            skeleton.BoneLODUsage = [.. _levelMasks];
            skeleton.MappingForBlendingInfos = Copy(_mappings);
        }
        catch (Exception)
        {
            // as above
        }
    }

    private static FrameBlendInfo.BoneIndexInfo[] Copy(FrameBlendInfo.BoneIndexInfo[] levels) =>
        [.. levels.Select(level => new FrameBlendInfo.BoneIndexInfo
        {
            BonesPerRemapPool = [.. level.BonesPerRemapPool ?? []],
            BoneRemapIDs = [.. level.BoneRemapIDs ?? []],
            SkinnedMaterialInfo = [.. level.SkinnedMaterialInfo ?? []],
        })];

    // The types' own copy constructors copy one level down and share everything below it; the face-range
    // rebuild replaces a piece's bursts in place, so the copy has to go all the way.
    private static FrameObjectModel.WeightedByMeshSplit[] Copy(FrameObjectModel.WeightedByMeshSplit[] splits) =>
        [.. splits.Select(split => new FrameObjectModel.WeightedByMeshSplit(split)
        {
            Data = [.. (split.Data ?? []).Select(piece => new FrameObjectModel.BlendMeshSplitInfo(piece)
            {
                Data = [.. (piece.Data ?? []).Select(burst => new FrameObjectModel.MiniMaterialBurst(burst)
                {
                    Data = [.. (burst.Data ?? []).Select(range => new FrameObjectModel.FacesBurst(range))],
                })],
            })],
        })];

    private static FrameSkeleton.MappingForBlendingInfo[] Copy(FrameSkeleton.MappingForBlendingInfo[] mappings) =>
        [.. mappings.Select(map => new FrameSkeleton.MappingForBlendingInfo
        {
            Bounds = [.. map.Bounds ?? Array.Empty<BoundingBox>()],
            RefToUsageArray = [.. map.RefToUsageArray ?? []],
            UsageArray = [.. map.UsageArray ?? []],
        })];
}
