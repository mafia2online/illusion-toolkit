using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Native object-spawner cell dimensions and additive density tiers under an explicit graphics context.</summary>
public sealed class CompiledPaintCellContext
{
    public float Radius { get; }
    public int Levels { get; }
    private readonly Vector3[] _sizes;
    private readonly float[] _radii, _density;
    public CompiledPaintCellContext(float radius = 150, int levels = 3, bool highQuality = false)
    {
        if (!float.IsFinite(radius) || radius <= 0 || levels is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(radius));
        Radius = highQuality ? radius * 4 : radius; Levels = levels;
        if (!float.IsFinite(Radius)) throw new ArgumentOutOfRangeException(nameof(radius));
        Vector3 grid = highQuality ? new(32, 32, 8) : new(8, 8, 2);
        _sizes = new Vector3[levels]; _radii = new float[levels + 1]; _density = new float[levels];
        float diameter = Radius + Radius;
        _sizes[0] = new(diameter / grid.X, diameter / grid.Y, diameter / grid.Z); _radii[0] = Radius;
        for (int i = 1; i < levels; i++) { _sizes[i] = _sizes[i - 1] * .5f; _radii[i] = _radii[i - 1] * (1 / 3f); }
        _radii[levels] = _radii[levels - 1] * .5f;
        float weight = 1;
        for (int i = levels - 1; i >= 0; i--) { _density[i] = weight; weight *= .33f; }
        float accumulated = _density[0];
        for (int i = 1; i < levels; i++) { _density[i] -= accumulated; accumulated += _density[i]; }
    }
    public Vector3 Size(int level) => _sizes[level];
    public float RefinementRadius(int level) => _radii[level];
    public float DensityScale(int level) => _density[level];

    public Matrix4x4 CellMatrix(int x, int y, int z, int level)
    {
        var size = Size(level);
        float Center(float value, int coordinate) { float offset = (float)coordinate * value; return ((value + offset) + offset) * .5f; }
        return new(size.X, 0, 0, 0, 0, size.Y, 0, 0, 0, 0, size.Z, 0, Center(size.X, x), Center(size.Y, y), Center(size.Z, z), 1);
    }
    /// <summary>Native affine inverse operation order, including signed-zero coefficients.</summary>
    public Matrix4x4 CellInverseMatrix(int x, int y, int z, int level)
    {
        var world = CellMatrix(x, y, z, level);
        float a = world.M11, b = world.M22, c = world.M33;
        float determinant = (a * (b * c) - 0f) + 0f;
        if (MathF.Abs(determinant) < 1e-16f) return Matrix4x4.Identity;
        float reciprocal = 1 / determinant;
        float ix = (b * c) * reciprocal, iy = (a * c) * reciprocal, iz = (a * b) * reciprocal;
        float negativeZero = BitConverter.Int32BitsToSingle(int.MinValue);
        float tx = -((world.M41 * ix + world.M42 * negativeZero) + world.M43 * 0f);
        float ty = -((world.M41 * negativeZero + world.M42 * iy) + world.M43 * negativeZero);
        float tz = -((world.M41 * 0f + world.M42 * negativeZero) + world.M43 * iz);
        return new(ix, negativeZero, 0, 0, negativeZero, iy, negativeZero, 0, 0, negativeZero, iz, 0, tx, ty, tz, 1);
    }

    /// <summary>Native mapping-query composition: inverse translation is added before the last two terms.</summary>
    public Matrix4x4 WorldToCellMatrix(Matrix4x4 world, int x, int y, int z, int level)
    {
        var inverse = CellInverseMatrix(x, y, z, level);
        Span<float> a = stackalloc float[12] { inverse.M11, inverse.M21, inverse.M31, inverse.M41,
            inverse.M12, inverse.M22, inverse.M32, inverse.M42, inverse.M13, inverse.M23, inverse.M33, inverse.M43 };
        Span<float> b = stackalloc float[12] { world.M11, world.M21, world.M31, world.M41,
            world.M12, world.M22, world.M32, world.M42, world.M13, world.M23, world.M33, world.M43 };
        Span<float> output = stackalloc float[12];
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 4; column++)
            {
                float value = a[row * 4] * b[column] + (column == 3 ? a[row * 4 + 3] : 0);
                value = a[row * 4 + 1] * b[4 + column] + value;
                output[row * 4 + column] = a[row * 4 + 2] * b[8 + column] + value;
            }
        return new(output[0], output[4], output[8], 0, output[1], output[5], output[9], 0,
            output[2], output[6], output[10], 0, output[3], output[7], output[11], 1);
    }

    public static Vector3 TransformPoint(Vector3 point, Matrix4x4 matrix) => new(
        ((point.X * matrix.M11 + point.Y * matrix.M21) + point.Z * matrix.M31) + matrix.M41,
        ((point.X * matrix.M12 + point.Y * matrix.M22) + point.Z * matrix.M32) + matrix.M42,
        ((point.X * matrix.M13 + point.Y * matrix.M23) + point.Z * matrix.M33) + matrix.M43);

}
