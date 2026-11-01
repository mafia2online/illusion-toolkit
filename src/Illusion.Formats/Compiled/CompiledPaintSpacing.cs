using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Native fixed-spacing paint emission in a declared additive-tier context.</summary>
public static class CompiledPaintSpacing
{
    public static IReadOnlyList<CompiledPaintTriangle.Point> Generate(Vector3[] positions, Vector3 normal, Vector3[] imagePositions,
        float spacingX, float spacingY, float deviationX, float deviationY, uint seed, int levels, int level,
        Func<Vector2, byte> sampleDensity, int maximumCandidates = 65535, CancellationToken cancellationToken = default)
    {
        return GenerateCounted(positions, normal, imagePositions, spacingX, spacingY, deviationX, deviationY, seed, levels, level,
            sampleDensity, maximumCandidates, cancellationToken, out _);
    }

    public static IReadOnlyList<CompiledPaintTriangle.Point> GenerateCounted(Vector3[] positions, Vector3 normal, Vector3[] imagePositions,
        float spacingX, float spacingY, float deviationX, float deviationY, uint seed, int levels, int level,
        Func<Vector2, byte> sampleDensity, int maximumCandidates, CancellationToken cancellationToken, out int candidateCount)
    {
        candidateCount = 0;
        if (positions.Length != 3 || imagePositions.Length != 3 || levels is < 1 or > 255 || level < 0 || level >= levels
            || !float.IsFinite(spacingX) || !float.IsFinite(spacingY) || !float.IsFinite(deviationX) || !float.IsFinite(deviationY)
            || spacingX <= 0 || spacingY <= 0 || maximumCandidates < 0
            || positions.Concat(imagePositions).Append(normal).Any(value => !float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))) throw new ArgumentException("Invalid fixed-spacing paint inputs.");
        cancellationToken.ThrowIfCancellationRequested();
        var uvCross = Vector3.Cross(imagePositions[1] - imagePositions[0], imagePositions[2] - imagePositions[0]);
        if ((uvCross.X * uvCross.X + uvCross.Y * uvCross.Y) + uvCross.Z * uvCross.Z < 1e-16f) return [];
        var basis = CompiledPaintGridBasis.Create(positions[0], positions[1], positions[2], normal, imagePositions[0], imagePositions[1], imagePositions[2]);
        float cofactor00 = basis.M22 * basis.M33 - basis.M23 * basis.M32;
        float cofactor01 = basis.M12 * basis.M33 - basis.M13 * basis.M32;
        float cofactor02 = basis.M12 * basis.M23 - basis.M13 * basis.M22;
        float determinantBasis = (basis.M11 * cofactor00 - basis.M21 * cofactor01) + basis.M31 * cofactor02;
        float ix0, ix1, ix2, iy0, iy1, iy2;
        if (MathF.Abs(determinantBasis) < 1e-16f) { ix0 = iy1 = 1; ix1 = ix2 = iy0 = iy2 = 0; }
        else
        {
            float reciprocal = 1 / determinantBasis, negative = -reciprocal;
            ix0 = cofactor00 * reciprocal;
            iy0 = (basis.M21 * basis.M33 - basis.M23 * basis.M31) * negative;
            ix1 = cofactor01 * negative;
            iy1 = (basis.M11 * basis.M33 - basis.M13 * basis.M31) * reciprocal;
            ix2 = cofactor02 * reciprocal;
            iy2 = (basis.M11 * basis.M23 - basis.M13 * basis.M21) * negative;
        }
        float tx = -((ix0 * 0 + ix1 * 0) + ix2 * 0), ty = -((iy0 * 0 + iy1 * 0) + iy2 * 0);
        Vector3 Coordinates(Vector3 value) => new((basis.M11 * value.X + basis.M12 * value.Y) + basis.M13 * value.Z,
            (basis.M21 * value.X + basis.M22 * value.Y) + basis.M23 * value.Z,
            (basis.M31 * value.X + basis.M32 * value.Y) + basis.M33 * value.Z);
        var coordinates = positions.Select(Coordinates).ToArray();
        var min = coordinates.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min);
        var max = coordinates.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max);
        float stepX = levels * spacingX;
        float startX = MathF.Floor(min.X / stepX) * stepX, endX = MathF.Floor(max.X / stepX) * stepX + stepX;
        float startY = MathF.Floor(min.Y / spacingY) * spacingY, endY = MathF.Floor(max.Y / spacingY) * spacingY + spacingY;
        float height = MathF.Ceiling(max.Z) + 2;
        if (!float.IsFinite(startX) || !float.IsFinite(startY) || !float.IsFinite(endX) || !float.IsFinite(endY) || !float.IsFinite(height))
            throw new NotSupportedException("Native fixed-spacing grid exceeds finite coordinate bounds.");
        var origin = new Vector3(((ix0 * startX + ix1 * startY) + ix2 * min.Z) + tx,
            ((iy0 * startX + iy1 * startY) + iy2 * min.Z) + ty, height);
        var first = positions[1] - positions[0]; var second = positions[2] - positions[0];
        var face = CompiledPaintGridBasis.Normalize(Vector3.Cross(positions[2] - positions[1], positions[0] - positions[1]));
        if (MathF.Abs(face.Z) < 1e-8f) return [];
        float plane = (face.X * positions[0].X + face.Y * positions[0].Y) + face.Z * positions[0].Z;
        float aa = (first.X * first.X + first.Y * first.Y) + first.Z * first.Z;
        float ab = (first.X * second.X + first.Y * second.Y) + first.Z * second.Z;
        float bb = (second.X * second.X + second.Y * second.Y) + second.Z * second.Z;
        float denominator = aa * bb - ab * ab;
        float determinant = 1 / denominator;
        ulong state = seed | ((ulong)seed << 32); float initial = (seed & 0xffffff) * 5.960465188081798e-8f;
        state = BitConverter.SingleToUInt32Bits(initial) ^ BitOperations.RotateLeft(state, 7);
        var output = new List<CompiledPaintTriangle.Point>();
        for (float x = 0; x < endX - startX; x += stepX)
            for (float y = 0; y < endY - startY; y += spacingY)
            {
                if (++candidateCount > maximumCandidates) throw new NotSupportedException("Native fixed-spacing candidate budget exceeded.");
                cancellationToken.ThrowIfCancellationRequested();
                float u = (state & 0xffffff) * 5.960465188081798e-8f;
                ulong next = BitConverter.SingleToUInt32Bits(u) ^ BitOperations.RotateLeft(state, 7);
                float v = (next & 0xffffff) * 5.960465188081798e-8f;
                state = BitConverter.SingleToUInt32Bits(v) ^ ((next << 7) | ((state >> 50) & 127));
                uint phase = unchecked((uint)(long)MathF.Abs(y / spacingY + origin.Y) + (uint)level) % 3;
                float distanceX = phase * spacingX + x;
                float px = ((u * deviationX - deviationX * .5f) * spacingX + origin.X) + distanceX * basis.M11;
                float py = ((v * deviationY - deviationY * .5f) * spacingY + origin.Y) + distanceX * basis.M12;
                float pz = origin.Z + distanceX * basis.M13;
                px += y * basis.M21; py += y * basis.M22; pz += y * basis.M23;
                pz -= (((face.X * px + face.Y * py) + face.Z * pz) - plane) / face.Z;
                var position = new Vector3(px, py, pz); var delta = position - positions[0];
                float da = (first.X * delta.X + first.Y * delta.Y) + first.Z * delta.Z;
                float db = (second.X * delta.X + second.Y * delta.Y) + second.Z * delta.Z;
                float weightC = (db * aa - da * ab) * determinant;
                float weightB = (da * bb - db * ab) * determinant;
                float weightA = (1 - weightB) - weightC;
                if (weightA < -1.1920928955078125e-7f || weightB < -1.1920928955078125e-7f || weightC < -1.1920928955078125e-7f) continue;
                var uv = imagePositions[0] * weightA + imagePositions[1] * weightB + imagePositions[2] * weightC;
                // CVTTSS2SI followed by signed16 narrowing maps nonfinite native image coordinates to pixel zero.
                // Image coordinates are no longer consumed after density selection; preserve their original result.
                var lookup = new Vector2(float.IsFinite(uv.X) ? uv.X : 0, float.IsFinite(uv.Y) ? uv.Y : 0);
                if (sampleDensity(lookup) != 0) output.Add(new(position, normal, uv, 1));
            }
        return output;
    }
}
