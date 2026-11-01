using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>The sparse, two-texel RGBA16F morph stream used by Mafia III PC vertex shaders.</summary>
public sealed class CompiledMorphAtlas
{
    private readonly byte[] _pixels;
    public int Width { get; }
    public int Height { get; }

    public readonly record struct Influence(int Target, Vector3 Position, Vector2 Normal, Vector2 Tangent);

    public CompiledMorphAtlas(ReadOnlySpan<byte> dds)
    {
        if (dds.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(dds) != 0x20534444
            || BinaryPrimitives.ReadUInt32LittleEndian(dds[4..]) != 124
            || BinaryPrimitives.ReadUInt32LittleEndian(dds[76..]) != 32
            || BinaryPrimitives.ReadUInt32LittleEndian(dds[84..]) != 113)
            throw new NotSupportedException("Morph atlas requires a legacy A16B16G16R16F DDS.");
        Height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(dds[12..]));
        Width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(dds[16..]));
        // The compiled native vertex shaders use a literal nine-bit row shift.
        if (Width != 512 || Height <= 0 || BinaryPrimitives.ReadUInt32LittleEndian(dds[28..]) > 1
            || BinaryPrimitives.ReadUInt32LittleEndian(dds[24..]) > 1 || BinaryPrimitives.ReadUInt32LittleEndian(dds[112..]) != 0
            || dds.Length != checked(128 + Width * Height * 8))
            throw new NotSupportedException("Unsupported morph atlas dimensions, mip chain, or payload length.");
        _pixels = dds[128..].ToArray();
    }

    public Influence[] ReadVertex(ReadOnlySpan<byte> psize1, int pixelAlignment, float precision, int targetCount)
    {
        if (psize1.Length != 4 || pixelAlignment <= 0 || !float.IsFinite(precision) || precision <= 0 || targetCount is < 0 or > 256)
            throw new InvalidDataException("Invalid native morph vertex parameters.");
        int count = psize1[1];
        int start = checked(BinaryPrimitives.ReadUInt16LittleEndian(psize1[2..]) * pixelAlignment);
        if (count == 0) return [];
        if (start > Width * Height - count * 2) throw new InvalidDataException("Sparse morph range exceeds its atlas.");
        var result = new Influence[count];
        for (int i = 0; i < count; i++)
        {
            var a = Texel(start + 2 * i); var b = Texel(start + 2 * i + 1);
            int target = checked((int)a.W);
            if (a.W < 0 || target >= targetCount) throw new InvalidDataException("Morph target index exceeds its correction table.");
            var position = new Vector3(a.X, a.Y, a.Z) * (precision * 3.2768f);
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new InvalidDataException("Morph position precision overflowed.");
            result[i] = new(target, position,
                new Vector2(b.X, b.Y), new Vector2(b.Z, b.W));
        }
        return result;
    }

    /// <summary>Evaluates the shader's additive position term before skinning, without modifying the atlas.</summary>
    public Vector3 PositionDelta(ReadOnlySpan<byte> psize1, int pixelAlignment, float precision, ReadOnlySpan<float> weights)
    {
        foreach (float weight in weights)
            if (!float.IsFinite(weight)) throw new InvalidDataException("Morph weights must be finite.");
        var delta = Vector3.Zero;
        foreach (var influence in ReadVertex(psize1, pixelAlignment, precision, weights.Length))
            delta += influence.Position * weights[influence.Target];
        if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y) || !float.IsFinite(delta.Z))
            throw new InvalidDataException("Morph position evaluation overflowed.");
        return delta;
    }

    private Vector4 Texel(int index)
    {
        int offset = checked(index * 8);
        float Read(int component) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(_pixels.AsSpan(offset + component * 2)));
        var value = new Vector4(Read(0), Read(1), Read(2), Read(3));
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
            throw new InvalidDataException("Morph atlas contains non-finite half floats.");
        return value;
    }
}
