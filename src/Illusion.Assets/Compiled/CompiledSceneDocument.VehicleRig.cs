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

    /// <summary>One canonical editable skeleton per owning scene object.</summary>
    public CompiledVehicleRig? VehicleRig(CompiledFrameNode frame)
    {
        if (!_vehicleRigs.TryGetValue(frame.Element, out var rig))
            _vehicleRigs[frame.Element] = rig = CompiledVehicleRig.TryCreate(Graph, frame.Element, frame, frame.Name);
        return rig;
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
