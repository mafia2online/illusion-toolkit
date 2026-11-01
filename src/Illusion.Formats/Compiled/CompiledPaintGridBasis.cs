using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Native fixed-spacing paint grid preparation. Grid emission remains a separate consumer.</summary>
public static class CompiledPaintGridBasis
{
    public static Matrix4x4 Create(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Vector3 imageA, Vector3 imageB, Vector3 imageC)
    {
        var first = b - a; var second = c - a;
        float dyFirst = imageB.Y - imageA.Y, dySecond = imageC.Y - imageA.Y;
        var tangent = Normalize(first * dySecond - second * dyFirst);
        static float Quantize(float value) => MathF.Round(value * 10, MidpointRounding.AwayFromZero) / 10;
        tangent = new(Quantize(tangent.X), Quantize(tangent.Y), Quantize(tangent.Z));
        normal = new(Quantize(normal.X), Quantize(normal.Y), Quantize(normal.Z));
        var across = Normalize(Vector3.Cross(tangent, normal));
        tangent = Normalize(tangent); normal = Normalize(normal);
        var result = new Matrix4x4(tangent.X, tangent.Y, tangent.Z, 0, across.X, across.Y, across.Z, 0, normal.X, normal.Y, normal.Z, 0, 0, 0, 0, 1);
        if (!Finite(tangent) || !Finite(across) || !Finite(normal)) throw new InvalidDataException("Nonfinite native fixed-spacing grid basis.");
        return result;
    }
    internal static Vector3 Normalize(Vector3 value)
    {
        float squared = (value.X * value.X + value.Y * value.Y) + value.Z * value.Z;
        if (squared >= 1e-16f) return value * (1 / MathF.Sqrt(squared));
        if (value.X != 0) return new(value.X >= 0 ? 1 : -1, 0, 0);
        if (value.Y != 0) return new(0, value.Y >= 0 ? 1 : -1, 0);
        return new(0, 0, value.Z >= 0 ? 1 : -1);
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
