using System.Numerics;

namespace Illusion.Domain;

/// <summary>The native render-mesh LOD index, separate from scene LOD-group visibility.</summary>
public sealed class NativeMeshLod : IEquatable<NativeMeshLod>
{
    private readonly uint[] _index;
    public uint MaximumDistanceSquared { get; }
    public NativeMeshLod(IReadOnlyList<uint> distancesSquared, IReadOnlyList<bool> valid, IReadOnlyList<bool> empty)
    {
        if (distancesSquared.Count != valid.Count || valid.Count != empty.Count || valid.Count > 15)
            throw new ArgumentException("Invalid native mesh LOD table.");
        var index = new List<uint>();
        for (int level = valid.Count - 1; level >= 0; level--)
            if (valid[level])
            {
                MaximumDistanceSquared = Math.Max(MaximumDistanceSquared, distancesSquared[level]);
                if (index.Count < 5) index.Add((empty[level] ? 0xF0000000u : (uint)level << 28) | Math.Min(distancesSquared[level], 0x0FFFFFFFu));
            }
        for (int i = 0; i < index.Count; i++)
            index[i] = (index[i] & 0xF0000000u) | (i + 1 < index.Count ? index[i + 1] & 0x0FFFFFFFu : 0);
        _index = index.ToArray();
    }

    public bool Equals(NativeMeshLod? other) => other is not null && MaximumDistanceSquared == other.MaximumDistanceSquared && _index.AsSpan().SequenceEqual(other._index);
    public override bool Equals(object? obj) => obj is NativeMeshLod other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(MaximumDistanceSquared); foreach (uint value in _index) hash.Add(value); return hash.ToHashCode();
    }

    /// <summary>Native static-visual auto LOD, without weather overrides or temporal fade.</summary>
    public int Select(Vector3 eye, Vector3 min, Vector3 max, float scaleSquared = 1, float biasSquared = 0)
    {
        float x = Math.Max(Math.Max(min.X - eye.X, eye.X - max.X), 0);
        float y = Math.Max(Math.Max(min.Y - eye.Y, eye.Y - max.Y), 0);
        float z = Math.Max(Math.Max(min.Z - eye.Z, eye.Z - max.Z), 0);
        float distance = ((x * x + y * y) + z * z) * scaleSquared + biasSquared;
        if (!float.IsFinite(distance) || distance < 0 || distance >= 4294967296f) return -1;
        uint value = (uint)(long)distance;
        if (value >= MaximumDistanceSquared) return -1;
        foreach (uint entry in _index)
            if ((entry & 0x0FFFFFFFu) <= value) return (int)entry >> 28;
        return -1;
    }
}

/// <summary>One level of an automatic native mesh LOD cloud, with the authored visual local AABB.</summary>
public sealed record NativeInstanceLod(NativeMeshLod Table, int Level, Vector3 LocalMin, Vector3 LocalMax)
{
    /// <summary>Optional native baked-cell world query AABB, independent of the currently active placement prefix.</summary>
    public NativeLodBounds? QueryBounds { get; init; }
}
