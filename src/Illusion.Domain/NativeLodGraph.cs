using System.Collections.ObjectModel;
using System.Numerics;

namespace Illusion.Domain;

/// <summary>One native visual LOD group. ChildrenReady is the parent's spatial switch flag,
/// set by activation of a child's completer, not by the group's own completer.</summary>
public sealed record NativeLodGroup(ulong Id, int Parent, IReadOnlyList<int> Children,
    Vector3 WorldMin, Vector3 WorldMax, float MinDistanceSquared, bool ChildrenReady);

/// <summary>Immutable native coarse/fine hierarchy. This evaluates stable native masks;
/// temporal transition fading and manual scene-tree visibility are separate concerns.</summary>
public sealed class NativeLodGraph
{
    private readonly NativeLodGroup[] _groups;
    private readonly int[] _order;
    public ReadOnlyCollection<NativeLodGroup> Groups { get; }
    public int Count => _groups.Length;

    public NativeLodGraph(IEnumerable<NativeLodGroup> groups)
    {
        _groups = groups.Select(group => group with { Children = Array.AsReadOnly(group.Children.ToArray()) }).ToArray();
        if (_groups.Select(group => group.Id).Distinct().Count() != Count) throw new ArgumentException("Duplicate native LOD group identity.", nameof(groups));
        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        var incoming = new int[Count];
        for (int i = 0; i < Count; i++)
        {
            var group = _groups[i];
            if (group.Parent < -1 || group.Parent >= Count || group.Parent == i || !Finite(group.WorldMin) || !Finite(group.WorldMax)
                || group.WorldMin.X > group.WorldMax.X || group.WorldMin.Y > group.WorldMax.Y || group.WorldMin.Z > group.WorldMax.Z
                || !float.IsFinite(group.MinDistanceSquared) || group.MinDistanceSquared < 0)
                throw new ArgumentException("Invalid native LOD group metadata.", nameof(groups));
            foreach (int child in group.Children)
            {
                if ((uint)child >= Count || child == i || _groups[child].Parent != i || ++incoming[child] != 1)
                    throw new ArgumentException("Inconsistent native LOD parent/child references.", nameof(groups));
            }
        }
        for (int i = 0; i < Count; i++)
            if (incoming[i] != (_groups[i].Parent < 0 ? 0 : 1)) throw new ArgumentException("Unresolved native LOD parent/child references.", nameof(groups));
        var order = new List<int>(Count); var pending = new Stack<int>();
        for (int i = Count - 1; i >= 0; i--) if (_groups[i].Parent < 0) pending.Push(i);
        while (pending.TryPop(out int index))
        {
            order.Add(index);
            foreach (int child in _groups[index].Children.Reverse()) pending.Push(child);
        }
        if (order.Count != Count) throw new ArgumentException("Cyclic native LOD hierarchy.", nameof(groups));
        _order = order.ToArray();
        // Native Synchronize/AbsorbChildAABBs expands each parent by every child recursively.
        for (int i = _order.Length - 1; i >= 0; i--)
        {
            var group = _groups[_order[i]];
            if (group.Parent < 0) continue;
            var parent = _groups[group.Parent];
            _groups[group.Parent] = parent with { WorldMin = Vector3.Min(parent.WorldMin, group.WorldMin), WorldMax = Vector3.Max(parent.WorldMax, group.WorldMax) };
        }
        Groups = Array.AsReadOnly(_groups);
    }

    public static float DistanceSquaredToBounds(Vector3 point, Vector3 min, Vector3 max)
    {
        float x = MathF.Max(MathF.Max(min.X - point.X, point.X - max.X), 0);
        float y = MathF.Max(MathF.Max(min.Y - point.Y, point.Y - max.Y), 0);
        float z = MathF.Max(MathF.Max(min.Z - point.Z, point.Z - max.Z), 0);
        return (x * x + y * y) + z * z;
    }

    private byte Mask(int index, Vector3 point, float scaleSquared, bool hasScene)
    {
        var group = _groups[index];
        if (group.Children.Count == 0) return 1;
        return hasScene && DistanceSquaredToBounds(point, group.WorldMin, group.WorldMax) <= group.MinDistanceSquared * scaleSquared
            ? (byte)(group.ChildrenReady ? 2 : 1) : (byte)1;
    }

    private static float ScaleSquared(Vector3 point, float scale)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z) || !float.IsFinite(scale))
            throw new ArgumentException("Native LOD point and switch scale must be finite.");
        return scale * scale;
    }

    /// <summary>Writes automatic group visibility only; callers combine it with manual visibility.</summary>
    public void Evaluate(Vector3 point, Span<bool> visible, float switchScale = 1, bool hasScene = true)
    {
        if (visible.Length < Count) throw new ArgumentException("Native LOD visibility buffer is too small.", nameof(visible));
        float scale = ScaleSquared(point, switchScale);
        Span<byte> masks = Count <= 256 ? stackalloc byte[Count] : new byte[Count];
        foreach (int index in _order)
        {
            int parent = _groups[index].Parent;
            byte mask = parent >= 0 && (masks[parent] & 2) == 0 ? (byte)0 : Mask(index, point, scale, hasScene);
            masks[index] = mask; visible[index] = (mask & 1) != 0;
        }
    }

    /// <summary>Allocation-free visibility query, including every ancestor's native recursion mask.</summary>
    public bool IsVisible(int group, Vector3 point, float switchScale = 1, bool hasScene = true)
    {
        if ((uint)group >= Count) throw new ArgumentOutOfRangeException(nameof(group));
        float scale = ScaleSquared(point, switchScale);
        if ((Mask(group, point, scale, hasScene) & 1) == 0) return false;
        for (int parent = _groups[group].Parent; parent >= 0; parent = _groups[parent].Parent)
            if ((Mask(parent, point, scale, hasScene) & 2) == 0) return false;
        return true;
    }
}

public sealed record NativeLodMembership(NativeLodGraph Graph, int Group);
