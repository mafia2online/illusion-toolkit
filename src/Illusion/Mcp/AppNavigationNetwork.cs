using System.Numerics;

namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public Task<string?> DeformNavigationNetworkAsync(string name, int section, int[] loadedSections, int[] vertices,
        float[][] positions, string? executable, bool moveRepresentatives = false)
    {
        if (section < 0 || loadedSections is not { Length: >= 2 and <= 32 } || loadedSections.Any(index => index < 0)
            || loadedSections.Distinct().Count() != loadedSections.Length || !loadedSections.Contains(section)
            || vertices is not { Length: >= 1 and <= 100000 } || positions is null || positions.Length != vertices.Length
            || vertices.Any(index => index < 0) || vertices.Distinct().Count() != vertices.Length
            || positions.Any(position => position is not { Length: 3 } || position.Any(value => !float.IsFinite(value))))
            return Task.FromResult<string?>("Select distinct loaded section indices including the source, and unique vertices with matching finite XYZ positions.");
        if (TargetHost is not { } host) return Task.FromResult<string?>(TargetNotOpen);
        if (Resolve(host, name, out var node) is { } refusal) return Task.FromResult<string?>(refusal);
        var changes = vertices.Select((vertex, index) => (vertex, Position: new Vector3(positions[index][0], positions[index][1], positions[index][2])))
            .ToDictionary(pair => pair.vertex, pair => pair.Position);
        return host.DeformCompiledNavigationNetworkAsync(node!, section, loadedSections, changes, executable, moveRepresentatives);
    }
}
