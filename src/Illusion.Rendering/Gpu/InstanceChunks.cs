using System.Numerics;

namespace Illusion.Rendering.Gpu;

/// <summary>One spatial cell of an instanced mesh: a contiguous run in the cell-major sorted instance
/// buffer plus a conservative world AABB, so the renderer can frustum-cull whole cells and draw only
/// visible ranges via the StartInstanceLocation argument of DrawIndexedInstanced.</summary>
public struct InstanceCell
{
    public uint Start;   // first instance of the cell in the sorted buffer
    public uint Count;   // instances in the cell
    public Vector3 Min;  // world AABB over every instance-transformed prototype corner
    public Vector3 Max;

    /// <summary>How far away the game still draws these copies (0 = always). Every copy in a cell shares one
    /// value — the binning splits by distance precisely so this can be a per-cell test.</summary>
    public float DrawDistance;
}

/// <summary>
/// Partitions an instance cloud (city_crash / Translokator) into an XY grid. Without this the whole
/// cloud spans the map and a single per-mesh AABB can never be culled — every instance rasterizes
/// every frame regardless of where the camera looks.
/// </summary>
public static class InstanceChunks
{
    /// <summary>XY cell edge in world units. A street-level frustum intersects few cells at this size,
    /// while cells stay big enough that draw calls (parts × visible ranges) remain cheap.</summary>
    public const float CellSize = 200f;

    /// <summary>
    /// Bins instances by the XY of their translation, orders cells row-major (Y, then X) so spatial
    /// neighbors stay contiguous in the buffer (the renderer merges adjacent visible ranges into one
    /// draw), and returns the cell-major reordered matrices plus one <see cref="InstanceCell"/> per bin.
    /// Cell AABBs transform the 8 corners of the prototype's local AABB by every instance matrix —
    /// conservative under rotation/scale, always a superset, safe for culling.
    /// </summary>
    /// <param name="drawDistances">Per-instance draw distance (parallel to <paramref name="instances"/>), or null
    /// when the cloud is drawn regardless of range. Copies with different distances go into different cells even
    /// when they stand in the same place, so the renderer can drop a cell on distance alone.</param>
    public static (Matrix4x4[] Sorted, InstanceCell[] Cells) Build(
        Matrix4x4[] instances, Vector3 localMin, Vector3 localMax, float[]? drawDistances = null, int[]? sortedIndices = null)
    {
        if (sortedIndices is not null && sortedIndices.Length != instances.Length) throw new ArgumentException("Instance sort mapping must match instance count.", nameof(sortedIndices));
        var bins = new Dictionary<(int X, int Y, float Distance), List<int>>();
        for (int i = 0; i < instances.Length; i++)
        {
            Vector3 t = instances[i].Translation;
            float distance = drawDistances != null && i < drawDistances.Length ? drawDistances[i] : 0f;
            (int, int, float) key = ((int)MathF.Floor(t.X / CellSize), (int)MathF.Floor(t.Y / CellSize), distance);
            if (!bins.TryGetValue(key, out List<int>? list)) { list = new List<int>(); bins[key] = list; }
            list.Add(i);
        }

        var keys = new List<(int X, int Y, float Distance)>(bins.Keys);
        keys.Sort((a, b) =>
            a.Y != b.Y ? a.Y.CompareTo(b.Y)
            : a.X != b.X ? a.X.CompareTo(b.X)
            : a.Distance.CompareTo(b.Distance));

        // A prototype with no vertices has an inverted local AABB — fall back to instance translations.
        bool hasLocal = localMin.X <= localMax.X;

        var sorted = new Matrix4x4[instances.Length];
        var cells = new InstanceCell[keys.Count];
        uint next = 0;
        for (int c = 0; c < keys.Count; c++)
        {
            List<int> idx = bins[keys[c]];
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            uint start = next;
            foreach (int i in idx)
            {
                Matrix4x4 m = instances[i];
                if (sortedIndices is not null) sortedIndices[next] = i;
                sorted[next++] = m;
                if (hasLocal)
                {
                    for (int k = 0; k < 8; k++)
                    {
                        var corner = new Vector3(
                            (k & 1) == 0 ? localMin.X : localMax.X,
                            (k & 2) == 0 ? localMin.Y : localMax.Y,
                            (k & 4) == 0 ? localMin.Z : localMax.Z);
                        Vector3 wp = Vector3.Transform(corner, m);
                        min = Vector3.Min(min, wp);
                        max = Vector3.Max(max, wp);
                    }
                }
                else
                {
                    min = Vector3.Min(min, m.Translation);
                    max = Vector3.Max(max, m.Translation);
                }
            }
            cells[c] = new InstanceCell
            {
                Start = start,
                Count = (uint)idx.Count,
                Min = min,
                Max = max,
                DrawDistance = keys[c].Distance,
            };
        }
        return (sorted, cells);
    }
}
