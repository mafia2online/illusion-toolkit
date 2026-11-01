namespace Illusion.Mcp;

public sealed record NativeLodInfo(int Graph, string Id, string? Parent, float[] BoundsMin, float[] BoundsMax,
    float SwitchDistanceSquared, bool ChildrenReady, bool Visible, int Meshes, int RenderVisibleMeshes);
