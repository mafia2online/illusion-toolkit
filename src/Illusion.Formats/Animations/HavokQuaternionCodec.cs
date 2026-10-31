using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Illusion.Formats.Animations;

/// <summary>Native Havok signed quaternion control-point reconstruction for modes 2–5.</summary>
public static class HavokQuaternionCodec
{
    /// <summary>Returns the native control point, before spline interpolation and final normalization.</summary>
    public static Vector4 Decode(int mode, ReadOnlySpan<byte> bytes)
    {
        int size = mode switch { 2 => 6, 3 => 3, 4 => 2, 5 => 16, _ => throw new ArgumentOutOfRangeException(nameof(mode)) };
        if (bytes.Length < size) throw new InvalidDataException("Truncated native quaternion.");
        if (mode == 5)
        {
            var result = new Vector4(BinaryPrimitives.ReadSingleLittleEndian(bytes),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[12..]));
            if (!float.IsFinite(result.LengthSquared()) || result.LengthSquared() == 0)
                throw new InvalidDataException("Invalid native uncompressed quaternion.");
            return result;
        }
        if (mode == 4)
        {
            var result = new Vector4((bytes[0] & 15) - 7, (bytes[0] >> 4) - 7,
                (bytes[1] & 15) - 7, (bytes[1] >> 4) - 7);
            if (result.LengthSquared() == 0) throw new InvalidDataException("Invalid zero native 16-bit quaternion.");
            return Vector4.Normalize(result);
        }
        int first = mode == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : bytes[0];
        int second = mode == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) : bytes[1];
        int third = mode == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) : bytes[2];
        int bits = mode == 2 ? 15 : 7;
        int omitted = (first >> bits) | ((second >> (bits - 1)) & 2);
        int mask = (1 << bits) - 1, center = mask >> 1;
        float quantum = mode == 2 ? .00004316100603318773f : .01122391689568758f;
        Span<float> components = stackalloc float[4];
        components.Clear();
        Span<int> packed = stackalloc int[] { first, second, third };

        for (int axis = 0, stored = 0; axis < 4; axis++)
        {
            if (axis == omitted) continue;
            float value = ((packed[stored++] & mask) - center) * quantum;
            components[axis] = value;
        }
        // Native SSE horizontal sum pairs X/Z and Y/W, then uses the raw reciprocal-square-root estimate.
        float remainder = 1 - ((components[0] * components[0] + components[2] * components[2])
            + (components[1] * components[1] + components[3] * components[3]));
        float missing = remainder <= 0 ? 0 : Sse.IsSupported
            ? remainder * Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(remainder)).ToScalar()
            : MathF.Sqrt(remainder);
        components[omitted] = missing * ((third & (1 << bits)) == 0 ? 1 : -1);
        return new Vector4(components[0], components[1], components[2], components[3]);
    }
}
