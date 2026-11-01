using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Native object-spawner scene-template transform, independent of point rasterization.</summary>
public static class CompiledPaintTransform
{
    /// <summary>
    /// Reproduces C_OTSMesh::ComputeMatrix(S_Point,float,float,float), then applies its returned
    /// uniform scale as CreateSOCell does. The small-mesh baked-buffer overload uses a separate
    /// caller-provided random value and must not be substituted with this position hash.
    /// </summary>
    public static Matrix4x4 Compute(Vector3 position, Vector3 normal, float scaleMin, float scaleMax, float normalAlignment)
    {
        uint hash = BitConverter.SingleToUInt32Bits(position.X) ^ unchecked(BitConverter.SingleToUInt32Bits(position.Y) * 0x34674b15u)
            ^ unchecked(BitConverter.SingleToUInt32Bits(position.Z) * 0xf1b7u);
        const float inverse24 = 5.960465188081798e-8f;
        float angle = ((hash & 0xffffff) * 6.2831854820251465f) * inverse24;
        float scale = scaleMin + (scaleMax - scaleMin) * ((BitOperations.RotateLeft(hash, 17) & 0xffffff) * inverse24);
        return ComputeBasis(position, normal, scaleMin, scaleMax, normalAlignment, angle, scale);
    }

    /// <summary>Native all-IsInstanced CreateMeshCell route: position-hash basis, fixed template-axis conversion,
    /// then uniform scale. This is distinct from both CreateSOCell and the 16-bin packed-buffer route.</summary>
    public static Matrix4x4 ComputeInstancedCell(Vector3 position, Vector3 normal, float scaleMin, float scaleMax, float normalAlignment)
    {
        uint hash = BitConverter.SingleToUInt32Bits(position.X) ^ unchecked(BitConverter.SingleToUInt32Bits(position.Y) * 0x34674b15u)
            ^ unchecked(BitConverter.SingleToUInt32Bits(position.Z) * 0xf1b7u);
        const float inverse24 = 5.960465188081798e-8f;
        float angle = ((hash & 0xffffff) * 6.2831854820251465f) * inverse24;
        float scale = scaleMin + (scaleMax - scaleMin) * ((BitOperations.RotateLeft(hash, 17) & 0xffffff) * inverse24);
        return ComputeBasis(position, normal, scaleMin, scaleMax, normalAlignment, angle, scale, true);
    }

    /// <summary>
    /// Native small-mesh CopyBuffers orientation bin (0..15), followed by PatchInstance translation.
    /// This matrix precedes native packed-buffer requantization. The caller must preserve the native
    /// shuffled point order and ceil(instanceCount/16) bin assignment; it is not position-hashed.
    /// </summary>
    public static Matrix4x4 ComputeBakedBin(Vector3 position, Vector3 normal, float scaleMin, float scaleMax, float normalAlignment, int bin)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bin);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bin, 15);
        float fraction = bin * .0625f;
        return ComputeBasis(position, normal, scaleMin, scaleMax, normalAlignment, fraction * 6.2831854820251465f,
            scaleMin + (scaleMax - scaleMin) * fraction);
    }

    private static Matrix4x4 ComputeBasis(Vector3 position, Vector3 normal, float scaleMin, float scaleMax, float normalAlignment, float angle, float scale, bool instancedCell = false)
    {
        if (!Finite(position) || !Finite(normal) || !float.IsFinite(scaleMin) || !float.IsFinite(scaleMax) || !float.IsFinite(normalAlignment))
            throw new InvalidDataException("Native paint transforms require finite inputs.");
        Span<float> matrix = stackalloc float[12];
        matrix.Clear();
        if (normalAlignment <= .01f)
        {
            // Native upright basis contains its original cos(pi/2) float residual. Replacing it
            // with an ideal identity/yaw basis changes authored template orientation.
            const float residual = -4.371138828673793e-8f;
            float sine = MathF.Sin(angle + 3.1415927410125732f), cosine = MathF.Cos(angle + 3.1415927410125732f);
            float negativeZero = BitConverter.UInt32BitsToSingle(0x80000000);
            matrix[0] = cosine; matrix[4] = -sine - cosine * negativeZero;
            matrix[8] = sine * residual + cosine * negativeZero;
            matrix[1] = 0; matrix[5] = residual; matrix[9] = 1;
            matrix[2] = -sine; matrix[6] = -(sine * residual) * 0 - cosine;
            matrix[10] = sine * 0 + cosine * residual;
        }
        else
        {
            if (normalAlignment < .99f)
            {
                normal = new Vector3(0 + normal.X * normalAlignment, 0 + normal.Y * normalAlignment, 1 + (normal.Z - 1) * normalAlignment);
                float squared = (normal.X * normal.X + normal.Y * normal.Y) + normal.Z * normal.Z;
                normal = squared >= 1e-8f ? normal * (1 / MathF.Sqrt(squared))
                    : normal.X != 0 ? new Vector3(normal.X >= 0 ? 1 : -1, 0, 0)
                    : normal.Y != 0 ? new Vector3(0, normal.Y >= 0 ? 1 : -1, 0)
                    : new Vector3(0, 0, normal.Z >= 0 ? 1 : -1);
            }
            float lengthSquared = (normal.X * normal.X + normal.Y * normal.Y) + normal.Z * normal.Z;
            normal /= MathF.Sqrt(lengthSquared);
            Vector3 third; float denominator;
            if (1 - normal.Z * normal.Z > 2.384185791015625e-7f)
            {
                third = new Vector3(0 - normal.X * normal.Z, 0 - normal.Y * normal.Z, 1 - normal.Z * normal.Z);
                denominator = MathF.Sqrt(1 - normal.Z * normal.Z);
            }
            else
            {
                third = new Vector3(0 - normal.X * normal.Y, 1 - normal.Y * normal.Y, 0 - normal.Z * normal.Y);
                denominator = MathF.Sqrt(1 - normal.Y * normal.Y);
            }
            third *= 1 / denominator;
            var first = Vector3.Cross(normal, third);
            float sine = MathF.Sin(angle), cosine = MathF.Cos(angle);
            for (int row = 0; row < 3; row++)
            {
                if (MathF.Abs(angle) <= 1e-8f)
                {
                    matrix[row * 4] = first[row]; matrix[row * 4 + 1] = normal[row]; matrix[row * 4 + 2] = third[row];
                }
                else
                {
                    matrix[row * 4] = cosine * first[row] + 0 * normal[row] + sine * third[row];
                    matrix[row * 4 + 1] = 0 * first[row] + normal[row] + 0 * third[row];
                    matrix[row * 4 + 2] = 0 * normal[row] - sine * first[row] + cosine * third[row];
                }
            }
        }
        if (instancedCell)
        {
            // Preserve original multiply/add order, including the native cos(pi/2) residual and signed zeros.
            const float residual = -4.371138828673793e-8f;
            float negativeZero = BitConverter.UInt32BitsToSingle(0x80000000);
            for (int row = 0; row < 3; row++)
            {
                int at = row * 4; float x = matrix[at], y = matrix[at + 1], z = matrix[at + 2];
                float rx = ((x * 1 + 0) + y * 0) + z * 0;
                float ry = ((x * 0 + 0) + y * residual) + z * -1;
                float rz = ((x * negativeZero + 0) + y * 1) + z * residual;
                float translation = ((x * 0 + position[row]) + y * 0) + z * 0;
                matrix[at] = ((rx * scale + 0) + ry * 0) + rz * 0;
                matrix[at + 1] = ((rx * 0 + 0) + ry * scale) + rz * 0;
                matrix[at + 2] = ((rx * 0 + 0) + ry * 0) + rz * scale;
                matrix[at + 3] = ((rx * 0 + translation) + ry * 0) + rz * 0;
            }
            position = new(matrix[3], matrix[7], matrix[11]);
        }
        else for (int row = 0; row < 3; row++) for (int column = 0; column < 3; column++) matrix[row * 4 + column] *= scale;
        if (!float.IsFinite(scale) || matrix.ContainsAnyExceptInRange(float.MinValue, float.MaxValue))
            throw new InvalidDataException("Native paint transform is degenerate or overflows its finite representation.");
        return new Matrix4x4(matrix[0], matrix[4], matrix[8], 0, matrix[1], matrix[5], matrix[9], 0,
            matrix[2], matrix[6], matrix[10], 0, position.X, position.Y, position.Z, 1);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
