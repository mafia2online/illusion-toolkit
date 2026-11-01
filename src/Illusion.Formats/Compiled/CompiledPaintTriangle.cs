using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Native random-area paint triangle rasterization, before object-spawner template transforms.</summary>
public static class CompiledPaintTriangle
{
    public readonly record struct Point(Vector3 Position, Vector3 Normal, Vector3 ImagePosition, byte Threshold);

    /// <summary>Native rasterizer's face normal, including its first signed-axis fallback for tiny triangles.</summary>
    public static Vector3 FlatNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        var cross = Vector3.Cross(b - a, c - a);
        float squared = (cross.X * cross.X + cross.Y * cross.Y) + cross.Z * cross.Z;
        if (squared >= 1e-8f) return cross * (1 / MathF.Sqrt(squared));
        if (cross.X != 0) return new(cross.X >= 0 ? 1 : -1, 0, 0);
        if (cross.Y != 0) return new(0, cross.Y >= 0 ? 1 : -1, 0);
        return new(0, 0, cross.Z >= 0 ? 1 : -1);
    }

    /// <summary>Source-owned port of the native non-fixed-spacing path. Density lookup is supplied by the caller.</summary>
    public static IReadOnlyList<Point> Generate(Vector3[] positions, Vector3[] normals, Vector3[] imagePositions,
        float[] vertexDensity, float minimumDensity, float maximumDensity, uint seed, Func<Vector2, byte> sampleDensity,
        int maximumCandidates = 1_000_000)
    {
        return GenerateCounted(positions, normals, imagePositions, vertexDensity, minimumDensity, maximumDensity, seed,
            sampleDensity, maximumCandidates, default, out _);
    }

    public static IReadOnlyList<Point> GenerateCounted(Vector3[] positions, Vector3[] normals, Vector3[] imagePositions,
        float[] vertexDensity, float minimumDensity, float maximumDensity, uint seed, Func<Vector2, byte> sampleDensity,
        int maximumCandidates, CancellationToken cancellationToken, out int candidateCount)
    {
        candidateCount = 0;
        cancellationToken.ThrowIfCancellationRequested();
        if (positions.Length != 3 || normals.Length != 3 || imagePositions.Length != 3 || vertexDensity.Length != 3)
            throw new ArgumentException("Native paint requires three triangle corners.");
        if (positions.Concat(normals).Concat(imagePositions).Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
            || vertexDensity.Any(value => !float.IsFinite(value)) || !float.IsFinite(minimumDensity) || !float.IsFinite(maximumDensity)
            || minimumDensity < 0 || maximumDensity < minimumDensity) throw new InvalidDataException("Invalid native paint triangle inputs.");
        var uvA = imagePositions[1] - imagePositions[0]; var uvB = imagePositions[2] - imagePositions[0];
        var uvCross = Vector3.Cross(uvA, uvB);
        if ((uvCross.X * uvCross.X + uvCross.Y * uvCross.Y) + uvCross.Z * uvCross.Z < 1e-16f) return [];
        float density = Math.Clamp(Math.Max(vertexDensity[0], Math.Max(vertexDensity[1], vertexDensity[2])), minimumDensity, maximumDensity);
        if (density <= 0) return [];
        ulong state = seed | ((ulong)seed << 32);
        float initial = (seed & 0xFFFFFF) * 5.960465188081798e-8f;
        state = BitConverter.SingleToUInt32Bits(initial) ^ BitOperations.RotateLeft(state, 7);
        var a = positions[1] - positions[0]; var b = positions[2] - positions[0]; var cross = Vector3.Cross(a, b);
        float area = MathF.Sqrt((cross.X * cross.X + cross.Y * cross.Y) + cross.Z * cross.Z) * .5f;
        float countFloat = MathF.Floor(area * density + initial * .9999989867210388f);
        if (!float.IsFinite(countFloat) || countFloat > maximumCandidates) throw new NotSupportedException("Native paint candidate count exceeds the bounded preview budget.");
        int count = (int)countFloat; candidateCount = count;
        var result = new List<Point>();
        float inverse = 1 / density;
        float d0 = vertexDensity[0] * inverse, d1 = vertexDensity[1] * inverse - d0, d2 = vertexDensity[2] * inverse - d0;
        float low = Math.Max(minimumDensity * inverse, 1e-8f), high = Math.Max(low, maximumDensity * inverse);
        for (int i = 0; i < count; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            float u = (state & 0xFFFFFF) * 5.960465188081798e-8f;
            ulong next = BitConverter.SingleToUInt32Bits(u) ^ BitOperations.RotateLeft(state, 7);
            float v = (next & 0xFFFFFF) * 5.960465188081798e-8f;
            ulong highBits = (state >> 50) & 0x7F;
            state = BitConverter.SingleToUInt32Bits(v) ^ ((next << 7) | highBits);
            if (u + v > 1) { u = 1 - u; v = 1 - v; }
            float relative = Math.Clamp((u * d1 + d0) + v * d2, low, high);
            uint hash = unchecked((uint)i * 0x638BCB5u) ^ seed;
            float quotient = (hash & 255) / relative;
            int truncated = quotient >= 2147483648f || !float.IsFinite(quotient) ? int.MinValue : (int)quotient;
            byte threshold = (byte)(Math.Min(unchecked((byte)truncated), (byte)254) + 1);
            Vector3 uv = Interpolate(imagePositions, u, v);
            if (sampleDensity(new(uv.X, uv.Y)) < threshold) continue;
            result.Add(new(Interpolate(positions, u, v), Interpolate(normals, u, v), uv, threshold));
        }
        return result;
    }

    private static Vector3 Interpolate(Vector3[] values, float u, float v)
    {
        var a = values[1] - values[0]; var b = values[2] - values[0];
        return new((a.X * u + values[0].X) + b.X * v, (a.Y * u + values[0].Y) + b.Y * v, (a.Z * u + values[0].Z) + b.Z * v);
    }
}
