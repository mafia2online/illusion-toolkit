using System.Numerics;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    public sealed record CollisionVolumeMetadata(string Kind, ushort ParentBone, Vector3 FullSize, ulong ShapeId);

    /// <summary>Native physical shape metadata, independent of display labels or Mafia II deform-volume roles.</summary>
    public CollisionVolumeMetadata? DescribeCollision(Domain.IFrameNode volume)
    {
        if (volume is not CompiledCollisionNode source || !_frames.ContainsValue(source.Frame)) return null;
        ushort bone = checked((ushort)(long)source.GetPropertyGroups().SelectMany(g => g.Properties)
            .Single(p => p.Id == "m_ParentBoneId").Get()!);
        string kind = source.Shape.TypeId switch
        {
            0x63ee7798 => "box", 0x4fd32ee0 => "capsule", 0x48430eeb => "convex", 0x7a0b5196 => "triangle", _ => "unknown"
        };
        Vector3 size = Vector3.Zero;
        if (kind == "box" && Graph.Field(source.Shape, "m_Size") is { DataSize: 12 } half)
            size = CompiledMeshDecoder.Vector(Graph.Data(half)) * 2;
        else if (kind == "capsule" && Graph.Field(source.Shape, "m_Radius") is { } radiusField
            && Graph.Field(source.Shape, "m_Height") is { } heightField)
        {
            float radius = CompiledMeshDecoder.Float(Graph.Data(radiusField), 0), height = CompiledMeshDecoder.Float(Graph.Data(heightField), 0);
            size = new(radius * 2, radius * 2, height + radius * 2);
            if (height == 0) kind = "sphere";
        }
        else if (CollisionGeometry(source.Shape) is { } geometry && geometry.Positions.Length > 0)
            size = geometry.Positions.Aggregate(Vector3.Max) - geometry.Positions.Aggregate(Vector3.Min);
        return new(kind, bone, size, source.Shape.ObjectId);
    }
}
