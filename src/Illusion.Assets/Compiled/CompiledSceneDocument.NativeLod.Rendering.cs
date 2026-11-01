namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Refreshes supported renderer coverage from decoded meshes, without changing native activation metadata.</summary>
    public void RefreshNativeLodRenderCoverage()
    {
        if (NativeLods is not { } graph) return;
        var counts = new int[graph.Count];
        foreach (var mesh in Meshes)
            if (mesh.NativeLod is { } membership && ReferenceEquals(membership.Graph, graph)
                && mesh.Positions.Length > 0 && mesh.Indices.Length >= 3)
                counts[membership.Group]++;
        graph.SetRenderCoverage(counts);
    }

    public string? NativeLodRenderFallback => NativeLods is { RenderFallbackGroups.Count: > 0 } graph
        ? $"Coarse LOD retained for {graph.RenderFallbackGroups.Count} native groups because a child subtree has no supported decoded renderer geometry; fine vegetation/instances are not represented by this fallback."
        : null;
}
