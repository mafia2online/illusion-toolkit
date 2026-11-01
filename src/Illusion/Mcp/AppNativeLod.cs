namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? NativeLodStatus(out IReadOnlyList<NativeLodInfo> groups)
    {
        groups = [];
        if (TargetHost?.Rnd is not { } renderer) return TargetNotOpen;
        var rows = new List<NativeLodInfo>(); int graphId = 0;
        foreach (var collection in renderer.Meshes.Where(mesh => mesh.NativeLod is not null).GroupBy(mesh => mesh.NativeLod!.Graph))
        {
            var graph = collection.Key;
            foreach (var mesh in collection) mesh.RefreshNativeLod(renderer.Camera.Position);
            for (int i = 0; i < graph.Count; i++)
            {
                var group = graph.Groups[i];
                var members = collection.Where(mesh => mesh.NativeLod!.Group == i).ToArray();
                rows.Add(new(graphId, $"0x{group.Id:X16}", group.Parent < 0 ? null : $"0x{graph.Groups[group.Parent].Id:X16}",
                    [group.WorldMin.X, group.WorldMin.Y, group.WorldMin.Z], [group.WorldMax.X, group.WorldMax.Y, group.WorldMax.Z],
                    group.MinDistanceSquared, group.ChildrenReady, graph.IsVisible(i, renderer.Camera.Position),
                    members.Length, members.Count(mesh => mesh.RenderVisible)));
            }
            graphId++;
        }
        groups = rows; return null;
    }
}
