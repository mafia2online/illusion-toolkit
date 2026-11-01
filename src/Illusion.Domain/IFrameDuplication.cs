namespace Illusion.Domain;

public interface ISceneHierarchyNode
{
    string Name { get; }
    string Kind { get; }
    string Category { get; }
    MeshData? Mesh { get; }
    IReadOnlyList<MeshData> LodMeshes { get; }
    SkeletonData? Skeleton { get; }
    ISceneSource? Source { get; }
    ISceneSource? GeometrySource => Source;
    IReadOnlyList<ISceneHierarchyNode> Children { get; }
}

public interface IFrameDuplication
{
    ISceneHierarchyNode Root { get; }
    IReadOnlyList<ISceneHierarchyNode> AdditionalRoots => [];
    void ValidateDetach() { }
    void ValidateReattach() { }
    void Detach();
    void Reattach();
}

public interface IFrameDuplicationProvider
{
    bool CanDuplicate(IFrameNode source);
    IFrameDuplication? Duplicate(IFrameNode source, out string? refusal);
}
