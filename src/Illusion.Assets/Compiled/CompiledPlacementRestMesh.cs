using System.Numerics;
using Illusion.Domain;

namespace Illusion.Assets.Compiled;

/// <summary>Freezes the existing four-influence rendering equation at a rig's authored model-space rest pose.
/// This is an immutable static placement snapshot; it does not simulate wind or animation.</summary>
internal static class CompiledPlacementRestMesh
{
    internal static MeshData Bake(MeshData mesh)
    {
        if (!mesh.IsSkinned) return mesh;
        var rig = mesh.Skeleton!; int vertices = mesh.Positions.Length;
        if (mesh.BoneIndices!.Length != vertices * 4 || mesh.BoneWeights!.Length != vertices * 4
            || mesh.Normals.Length != vertices || rig.Bones.Count == 0 || rig.Bones.Count > 128
            || rig.InverseBind.Count != rig.Bones.Count)
            throw new NotSupportedException("Native placement rest skin requires complete supported bind and influence arrays.");
        var rest = mesh.LiveRest?.ToArray() ?? rig.Bones.Select(bone => bone.Rest).ToArray();
        if (rest.Length != rig.Bones.Count) throw new NotSupportedException("Native placement rest pose extent differs from its rig.");
        var palette = new Matrix4x4[rig.Bones.Count];
        for (int bone = 0; bone < palette.Length; bone++)
        {
            var current = rest[bone]; current.M14 = current.M24 = current.M34 = 0; current.M44 = 1;
            palette[bone] = rig.InverseBind[bone] * current;
            if (!Finite(palette[bone])) throw new InvalidDataException("Nonfinite native placement rest matrix.");
        }
        var positions = new Vector3[vertices]; var normals = new Vector3[vertices];
        Vector3[]? tangents = mesh.Tangents is null ? null : new Vector3[vertices];
        Vector3[]? binormals = mesh.Binormals is null ? null : new Vector3[vertices];
        if (mesh.Tangents is not null && mesh.Tangents.Length != vertices || mesh.Binormals is not null && mesh.Binormals.Length != vertices)
            throw new InvalidDataException("Native placement rest tangent channel is truncated.");
        var used = new bool[vertices]; foreach (uint index in mesh.Indices)
        { if (index >= vertices) throw new InvalidDataException("Native placement rest index is out of range."); used[index] = true; }
        for (int vertex = 0; vertex < vertices; vertex++)
        {
            Matrix4x4 skin = default;
            for (int influence = 0; influence < 4; influence++)
            {
                int index = vertex * 4 + influence; float weight = mesh.BoneWeights[index]; int bone = mesh.BoneIndices[index];
                if (!float.IsFinite(weight) || weight < 0 || weight != 0 && bone >= palette.Length)
                {
                    if (used[vertex]) throw new NotSupportedException("Native placement rest has an invalid used bone influence.");
                    continue;
                }
                if (weight != 0) skin += palette[bone] * weight;
            }
            positions[vertex] = Vector3.Transform(mesh.Positions[vertex], skin);
            normals[vertex] = Vector3.TransformNormal(mesh.Normals[vertex], skin);
            if (tangents is not null) tangents[vertex] = Vector3.TransformNormal(mesh.Tangents![vertex], skin);
            if (binormals is not null) binormals[vertex] = Vector3.TransformNormal(mesh.Binormals![vertex], skin);
            if (!Finite(positions[vertex]) || !Finite(normals[vertex]) || tangents is not null && !Finite(tangents[vertex])
                || binormals is not null && !Finite(binormals[vertex])) throw new InvalidDataException("Nonfinite native placement rest geometry.");
        }
        return new MeshData
        {
            Name = mesh.Name, World = mesh.World, Positions = positions, Normals = normals, Tangents = tangents, Binormals = binormals,
            UVs = mesh.UVs, Colors = mesh.Colors, SecondaryColors = mesh.SecondaryColors, DecalUVs = mesh.DecalUVs, Indices = mesh.Indices, Parts = mesh.Parts, Lod = mesh.Lod,
            NativeLod = mesh.NativeLod
        };
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Matrix4x4 value) => Finite(new Vector3(value.M11, value.M12, value.M13)) && Finite(new Vector3(value.M21, value.M22, value.M23))
        && Finite(new Vector3(value.M31, value.M32, value.M33)) && Finite(value.Translation);
}
