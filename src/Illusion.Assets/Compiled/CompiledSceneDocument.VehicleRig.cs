using System.Buffers.Binary;
using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    public sealed partial class CompiledFrameNode
    {
        public CompiledVehicleRig? VehicleRig => _document.VehicleRig(this);
    }
    private readonly Dictionary<CompiledElement, CompiledVehicleRig?> _vehicleRigs = [];

    /// <summary>Restores canonical rig adapter identity after a completely reverted, unobservable staging failure.
    /// Native immutable references and authored data must already have been restored before invoking it.</summary>
    public Action CaptureRevertedRigCacheCheckpoint()
    {
        var before = _vehicleRigs.ToArray();
        return () =>
        {
            _vehicleRigs.Clear();
            foreach (var pair in before) _vehicleRigs.Add(pair.Key, pair.Value);
        };
    }

    /// <summary>Restores native wrapper staging after every failed edit has first undone its
    /// allocations and budget actions. Never invoke this to discard a successfully applied edit.</summary>
    public Action CaptureRevertedCarryCheckpoint()
    {
        var keys = _vehicleResources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restore = _vehicleResources.Values.Select(manager => manager.CaptureFlushCheckpoint()).ToArray();
        return () =>
        {
            foreach (var key in _vehicleResources.Keys.Where(key => !keys.Contains(key)).ToArray()) _vehicleResources.Remove(key);
            foreach (var checkpoint in restore) checkpoint();
        };
    }

    /// <summary>One canonical editable skeleton per owning scene object.</summary>
    public CompiledVehicleRig? VehicleRig(CompiledFrameNode frame)
    {
        var currentImmutable = Components(frame.Element).Select(component => Graph.Resolve(Graph.Field(component, "m_ImmutableData")))
            .FirstOrDefault(immutable => immutable?.TypeId == 0x623e708e);
        if (_vehicleRigs.TryGetValue(frame.Element, out var cached) && !ReferenceEquals(cached?.Immutable, currentImmutable))
            _vehicleRigs.Remove(frame.Element);
        if (!_vehicleRigs.TryGetValue(frame.Element, out var rig))
            _vehicleRigs[frame.Element] = rig = CompiledVehicleRig.TryCreate(Graph, frame.Element, frame, frame.Name);
        if (rig is not null) rig.MutationRefusal = () => !_vehicleRigs.TryGetValue(frame.Element, out var activeRig) || !ReferenceEquals(activeRig, rig)
            || !Components(frame.Element).Any(component =>
                ReferenceEquals(Graph.Resolve(Graph.Field(component, "m_ImmutableData")), rig.Immutable))
            ? "This skeleton no longer belongs to its canonical frame; refresh the skeleton tree before editing it."
            : ValidateSkeletonProxyMutation(rig.Immutable);
        return rig;
    }

    private string? ValidateSkeletonProxyMutation(CompiledElement immutable)
    {
        foreach (var owner in _frames.Values)
            if (Components(owner.Element).Any(component => ReferenceEquals(Graph.Resolve(Graph.Field(component, "m_ImmutableData")), immutable))
                && ValidateSceneCollectionMutation(owner) is { } refusal) return refusal;
        return null;
    }

    public SkeletonData? VehicleSkeleton(CompiledFrameNode frame) => VehicleRig(frame)?.Skeleton;

    internal (ushort[]? Palette, SkeletonData? Skeleton, IReadOnlyList<Matrix4x4>? Live) VehicleSkin(
        CompiledFrameNode frame, CompiledElement visual, int level, Matrix4x4? geometryBind)
    {
        var palettes = Graph.Items(Graph.Field(visual, "m_SkeletonBoneIDsPerLOD"));
        if (palettes.Count == 0 || VehicleRig(frame) is not { } rig) return default;
        var data = Graph.Data(palettes[Math.Min(level, palettes.Count - 1)]);
        if (data.Length < 4) throw new InvalidDataException("Invalid compiled skin palette.");
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data));
        if (data.Length != 4L + count * 2L) throw new InvalidDataException("Invalid compiled skin palette extent.");
        var palette = new ushort[count];
        for (int i = 0; i < count; i++) palette[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(4 + i * 2)..]);
        return (palette, rig.RenderSkeleton(geometryBind), rig.LiveRest);
    }
}
