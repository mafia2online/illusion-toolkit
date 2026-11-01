using System.Buffers.Binary;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private (MeshData?[] Meshes, NativeMeshLod Table, NativeLodBounds Bounds, bool RestBaked) DecodeNativePlacementTemplate(
        CompiledFrameNode frame, CompiledElement visual, CompiledMeshDecoder decoder)
    {
    var set = Graph.Resolve(Graph.Field(visual, "m_MeshSet"));
    var levels = Graph.Items(Graph.Field(set, "m_LODs"));
    int count = levels.Count; bool restBaked = false;
    var meshes = new MeshData?[count]; var distances = new uint[count]; var valid = new bool[count]; var empty = new bool[count];
    for (int level = 0; level < count; level++)
    {
        var native = Graph.Resolve(Graph.Field(levels[level], "m_Mesh"));
        if (native is null) continue;
        var distanceField = Graph.Field(native, "m_LODdistance2") ?? throw new InvalidDataException("Native prototype mesh has no LOD distance.");
        float distance = BinaryPrimitives.ReadSingleLittleEndian(Graph.Data(distanceField));
        if (!float.IsFinite(distance) || distance < 0) throw new NotSupportedException("Invalid native mesh LOD distance.");
        distances[level] = distance >= 4294967296f ? uint.MaxValue : (uint)(long)distance;
        empty[level] = Graph.Field(native, "m_MeshFlags") is { } flags && (Graph.Integer(flags) & 4) != 0;
        valid[level] = empty[level] || Graph.Resolve(Graph.Field(Graph.Field(native, "m_IndexWrapper"), "m_Buffer")) is not null;
        if (!valid[level] || empty[level]) continue;
        var mesh = DecodeVisual(frame, visual, decoder, level)
            ?? throw new NotSupportedException("Native placement prototype has an unsupported mesh buffer.");
        if (mesh.IsSkinned) { mesh = CompiledPlacementRestMesh.Bake(mesh); restBaked = true; }
        meshes[level] = mesh;
    }
    if (!meshes.Any(mesh => mesh is not null)) throw new NotSupportedException("Native placement prototype has no mesh levels.");
    if (Graph.Field(visual, "m_Data") is not { DataSize: 88 } data
        || !CompiledMeshBinding.TryImmutableRigidBind(Graph, frame.Element, visual, out var bind))
        throw new NotSupportedException("Native placement visual bounds require a verified rigid binding.");
    var bytes = Graph.Data(data);
    var bounds = TransformNativeBounds(new(CompiledMeshDecoder.Vector(bytes[16..]), CompiledMeshDecoder.Vector(bytes[28..])), bind);
        return (meshes, new(distances, valid, empty), bounds, restBaked);
    }
}
