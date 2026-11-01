using System.Numerics;

namespace Illusion.Domain;

/// <summary>
/// Render-neutral geometry of a single mesh: decoded MafiaToolkit vertices,
/// ready to upload into Silk.NET buffers, split into parts by material.
/// </summary>
public sealed class MeshData
{
    public string Name { get; init; } = null!;
    public Matrix4x4 World { get; init; }
    public Vector3[] Positions { get; init; } = null!;
    public Vector3[] Normals { get; init; } = null!;
    public Vector2[]? UVs { get; init; }
    public Vector4[]? Colors { get; init; }
    /// <summary>Optional native TEXCOORD2 used by verified vehicle T018 decals.</summary>
    public Vector2[]? DecalUVs { get; init; }
    /// <summary>Per-vertex tangent/binormal (world of the local frame) for normal mapping; null when the
    /// source mesh has no tangent channel — then the shader falls back to the vertex normal.</summary>
    public Vector3[]? Tangents { get; init; }
    public Vector3[]? Binormals { get; init; }
    public uint[] Indices { get; init; } = null!;
    public MeshPart[] Parts { get; init; } = null!;

    /// <summary>Which level of detail of its frame this geometry is — 0 unless the viewport was switched
    /// to a coarser one, and already clamped to what the mesh ships (a mesh with a single level answers 0
    /// however high the request was). Everything that edits the mesh writes back into THIS level.</summary>
    public int Lod { get; init; }

    /// <summary>Optional native visual coarse/fine hierarchy membership. Manual tree visibility and
    /// editor-helper geometry remain independent; null preserves the ordinary rendering policy.</summary>
    public NativeLodMembership? NativeLod { get; set; }

    /// <summary>
    /// Per-vertex bone influences, four per vertex, flattened: vertex i owns [4i, 4i+4). Null for a mesh with
    /// no skin, which is nearly everything — only a skinned model has these.
    /// <para>
    /// The indices are ALREADY resolved to the model's own bone list. In the file they are not: a vertex's
    /// four ids index a per-LOD remap pool, and which pool depends on the face group being drawn (see
    /// <c>--probe-skinning</c>). Resolving that at load is what lets the renderer treat a bone id as a bone.
    /// </para>
    /// </summary>
    public byte[]? BoneIndices { get; init; }

    /// <summary>Weights parallel to <see cref="BoneIndices"/>; they sum to one per vertex.</summary>
    public float[]? BoneWeights { get; init; }

    /// <summary>The rig this mesh is skinned to, in the pose it was authored in. Null when there is no skin.</summary>
    public SkeletonData? Skeleton { get; init; }

    /// <summary>
    /// The rig's rest transforms as the document holds them — the LIVE array, not a copy. The renderer reads
    /// it every frame, so a bone moved by anything at all shows up without that thing having to say so.
    /// <para>
    /// This exists because the notification route did not survive contact: a bone edited through the gizmo,
    /// through an undo, or through a push from Blender each had to remember to refresh the pose, and each
    /// found a new way not to. Reading the source of truth costs one small matrix multiply per bone per
    /// frame and cannot go stale.
    /// </para>
    /// </summary>
    public IReadOnlyList<Matrix4x4>? LiveRest { get; init; }

    /// <summary>True when the mesh carries everything a skinned draw needs.</summary>
    public bool IsSkinned => BoneIndices != null && BoneWeights != null && Skeleton != null;

    /// <summary>
    /// World matrices of copies for hardware instancing (city_crash / Translokator). null for a regular
    /// mesh — then the single <see cref="World"/> is used. Vertex positions here are in the prototype's
    /// LOCAL space: each matrix is already = refTransform·instanceTRS.
    /// </summary>
    public Matrix4x4[]? Instances { get; init; }

    /// <summary>
    /// How far away the game still draws each copy, parallel to <see cref="Instances"/>. The crash table
    /// carries this per object (a bin at 20 m, a billboard at 300 m), and honouring it is what makes the
    /// viewport show the same clutter the game does. null (or 0 for an entry) = always drawn.
    /// </summary>
    public float[]? InstanceDrawDistances { get; init; }

    public int VertexCount => Positions.Length;
    public int TriangleCount => Indices.Length / 3;
}
