using System.Numerics;
using System.Text.Json;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Adds a native connected triangle outside an ordinary boundary edge while retaining
    /// actual streaming identities. Bounds may grow for fully isolated sections. Returned edits are already applied.</summary>
    public async Task<NavigationDeformationResult> ExtrudeNavigationBoundaryAsync(int sectionIndex, int edgeIndex,
        Vector3? position = null, string? copiedExecutablePath = null, CancellationToken cancellationToken = default)
    {
        if ((uint)sectionIndex >= _navMeshes.Count) return new(null, "Navigation section index is out of range.");
        try
        {
            var file = _navMeshes[sectionIndex]; byte[] before = file.ToBytes();
            _ = file.PrepareBoundaryExtrusion(edgeIndex, position);
            byte[] cooked = await NativeNavigationCooker.CookAsync(before, new Dictionary<int, Vector3>(), copiedExecutablePath,
                cancellationToken, extrudeEdge: edgeIndex, extrusionPosition: position);
            if (!before.AsSpan().SequenceEqual(file.ToBytes())) return new(null, "Navigation changed during extrusion; retry the edit.");
            var action = new NavigationSubdivisionEdit(this, file, file.ApplyBoundaryExtrusionCook(edgeIndex, cooked, position));
            RefreshNavigationMeshNodes(file); return new(action, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException or
            UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        { return new(null, ex.Message); }
    }
}
