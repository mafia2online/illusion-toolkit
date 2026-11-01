using System.Collections.ObjectModel;
using System.Numerics;

namespace Illusion.Domain;

public sealed partial class NativeLodGraph
{
    private sealed record RenderState(int[] Counts, bool[] ChildrenAvailable,
        ReadOnlyCollection<int> View, ReadOnlyCollection<int> Fallbacks);
    private RenderState? _renderState;

    /// <summary>Decoded mesh counts, independent of native completer activation and manual eyes.</summary>
    public IReadOnlyList<int>? RenderCoverage => Volatile.Read(ref _renderState)?.View;
    public IReadOnlyList<int> RenderFallbackGroups => Volatile.Read(ref _renderState)?.Fallbacks ?? (IReadOnlyList<int>)Array.Empty<int>();

    /// <summary>Publishes renderer capabilities without changing the native visibility oracle.</summary>
    public void SetRenderCoverage(IReadOnlyList<int> decodedMeshCounts)
    {
        ArgumentNullException.ThrowIfNull(decodedMeshCounts);
        if (decodedMeshCounts.Count != Count || decodedMeshCounts.Any(count => count < 0))
            throw new ArgumentException("Renderer coverage must contain one nonnegative count per native group.", nameof(decodedMeshCounts));
        var counts = decodedMeshCounts.ToArray();
        var subtree = counts.Select(count => count > 0).ToArray();
        var available = new bool[Count];
        for (int i = _order.Length - 1; i >= 0; i--)
        {
            int index = _order[i];
            available[index] = _groups[index].Children.All(child => subtree[child]);
            if (_groups[index].Parent >= 0) subtree[_groups[index].Parent] |= subtree[index];
        }
        var fallback = Enumerable.Range(0, Count).Where(index => _groups[index].ChildrenReady
            && _groups[index].Children.Count > 0 && !available[index]).ToArray();
        Volatile.Write(ref _renderState, new(counts, available, Array.AsReadOnly(counts), Array.AsReadOnly(fallback)));
    }

    private static byte RenderMask(NativeLodGroup group, int index, Vector3 point, float scale, bool hasScene, RenderState? coverage)
    {
        byte native = Mask(group, point, scale, hasScene);
        // Keep the coarse representation if any child subtree has no supported decoded mesh.
        return native == 2 && coverage is not null && !coverage.ChildrenAvailable[index] ? (byte)1 : native;
    }

    public void EvaluateRender(Vector3 point, Span<bool> visible, float switchScale = 1, bool hasScene = true)
    {
        if (visible.Length < Count) throw new ArgumentException("Native LOD visibility buffer is too small.", nameof(visible));
        float scale = ScaleSquared(point, switchScale);
        var groups = Volatile.Read(ref _bounds).Values;
        var coverage = Volatile.Read(ref _renderState);
        Span<byte> masks = Count <= 256 ? stackalloc byte[Count] : new byte[Count];
        foreach (int index in _order)
        {
            int parent = groups[index].Parent;
            byte mask = parent >= 0 && (masks[parent] & 2) == 0 ? (byte)0 : RenderMask(groups[index], index, point, scale, hasScene, coverage);
            masks[index] = mask; visible[index] = (mask & 1) != 0;
        }
    }

    public bool IsRenderVisible(int group, Vector3 point, float switchScale = 1, bool hasScene = true)
    {
        if ((uint)group >= Count) throw new ArgumentOutOfRangeException(nameof(group));
        float scale = ScaleSquared(point, switchScale);
        var groups = Volatile.Read(ref _bounds).Values;
        var coverage = Volatile.Read(ref _renderState);
        if ((RenderMask(groups[group], group, point, scale, hasScene, coverage) & 1) == 0) return false;
        for (int parent = groups[group].Parent; parent >= 0; parent = groups[parent].Parent)
            if ((RenderMask(groups[parent], parent, point, scale, hasScene, coverage) & 2) == 0) return false;
        return true;
    }
}
