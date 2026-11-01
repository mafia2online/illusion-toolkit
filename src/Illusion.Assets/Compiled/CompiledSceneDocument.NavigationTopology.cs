using System.Numerics;
using System.Text.Json;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Flips a compatible internal diagonal using a separately isolated native graph cook.
    /// The returned edit is applied and preserves canonical navigation adapters for undo.</summary>
    public async Task<NavigationDeformationResult> FlipNavigationEdgeAsync(int sectionIndex, int edgeIndex,
        string? copiedExecutablePath = null, CancellationToken cancellationToken = default)
    {
        if ((uint)sectionIndex >= _navMeshes.Count) return new(null, "Navigation section index is out of range.");
        try
        {
            var file = _navMeshes[sectionIndex]; byte[] before = file.ToBytes();
            _ = file.PrepareEdgeFlip(edgeIndex);
            byte[] cooked = await NativeNavigationCooker.CookAsync(before, new Dictionary<int, Vector3>(),
                copiedExecutablePath, cancellationToken, flipEdge: edgeIndex);
            if (!before.AsSpan().SequenceEqual(file.ToBytes())) return new(null, "Navigation changed during the native cook; retry the edit.");
            return new(new NavigationTranslationEdit(file.ApplyEdgeFlipCook(edgeIndex, cooked)), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException or
            UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        { return new(null, ex.Message); }
    }
}
