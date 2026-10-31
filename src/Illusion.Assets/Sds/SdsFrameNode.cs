using Illusion.Domain;

namespace Illusion.Assets.Sds;

/// <summary>CPU node of the internal SDS hierarchy: frame/mesh/light/… + children. A mesh has <see cref="Mesh"/> set.</summary>
public sealed class SdsFrameNode : ISceneHierarchyNode
{
    public string Name { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public string Category { get; set; } = "Normal"; // for scenes: Proxy / Normal (filters during streaming)
    public MeshData? Mesh { get; set; }         // non-null only on single-level mesh nodes

    /// <summary>
    /// One entry per level of detail, in order, for a mesh that ships more than one — then <see cref="Mesh"/>
    /// is null and the geometry lives here instead. The tree turns these into child rows ("LOD 0", "LOD 1")
    /// so a level can be shown, hidden and edited on its own; a single-level mesh grows no rows at all.
    /// </summary>
    public List<MeshData> LodMeshes { get; } = new();

    /// <summary>The rig, on a skinned model (<c>FrameObjectModel</c>) that has one. For a car its bones are
    /// the parts — doors, covers, axles — and the archive's collision hulls and points hang off them.</summary>
    public SkeletonData? Skeleton { get; set; }
    /// <summary>Backing source: an <see cref="IFrameNode"/> (frame/mesh) or an <see cref="IFrameScene"/>
    /// (scene folder). Carried to the UI node — the UI only ever sees these Domain ports.</summary>
    public ISceneSource? Source { get; set; }
    public ISceneSource? GeometrySource { get; set; }
    public List<SdsFrameNode> Children { get; } = new();
    IReadOnlyList<ISceneHierarchyNode> ISceneHierarchyNode.Children => Children;
    IReadOnlyList<MeshData> ISceneHierarchyNode.LodMeshes => LodMeshes;
}
