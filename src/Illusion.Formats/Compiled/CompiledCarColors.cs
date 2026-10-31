using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>The RGB color palette consumed by C_Car::SetColor and InitCarTables.</summary>
public sealed class CompiledCarColors
{
    private readonly byte[] _bytes;
    private readonly int _rows;
    public int Count { get; }
    private CompiledCarColors(byte[] bytes, int rows, int count) { _bytes = bytes; _rows = rows; Count = count; }
    public static CompiledCarColors? TryRead(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 48 || BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..]) != 0x5d69a41cdc82936c) return null;
        long end = 20L + BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        long table = 44L + BinaryPrimitives.ReadInt32LittleEndian(bytes[44..]);
        if (end > bytes.Length || table < 48 || table + 12 > end) throw new InvalidDataException("Invalid native vehicle palette.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[((int)table + 4)..]);
        long rows = table + BinaryPrimitives.ReadInt32LittleEndian(bytes[(int)table..]);
        if (count > 100000 || rows < table + 12 || rows + count * 96L > end) throw new InvalidDataException("Invalid native vehicle palette extent.");
        return new CompiledCarColors(bytes.ToArray(), (int)rows, (int)count);
    }
    private int Offset(int row)
    {
        if ((uint)row >= Count) throw new ArgumentOutOfRangeException(nameof(row));
        return _rows + row * 96;
    }
    public uint Id(int row) => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(Offset(row)));
    public Vector3 Rgb(int row)
    {
        int p = Offset(row) + 12;
        return new Vector3(_bytes[p], _bytes[p + 1], _bytes[p + 2]);
    }
    public Vector3 Linear(int row)
    {
        var rgb = Rgb(row) / 255;
        static float Convert(float value) => value <= .04045f ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f);
        return new Vector3(Convert(rgb.X), Convert(rgb.Y), Convert(rgb.Z));
    }
    public void SetRgb(int row, Vector3 rgb)
    {
        if (!float.IsFinite(rgb.X) || !float.IsFinite(rgb.Y) || !float.IsFinite(rgb.Z)
            || Vector3.Min(rgb, Vector3.Zero) != Vector3.Zero || Vector3.Max(rgb, new(255)) != new Vector3(255))
            throw new ArgumentOutOfRangeException(nameof(rgb), "Palette RGB values must be between 0 and 255.");
        int p = Offset(row) + 12;
        _bytes[p] = (byte)MathF.Round(rgb.X); _bytes[p + 1] = (byte)MathF.Round(rgb.Y); _bytes[p + 2] = (byte)MathF.Round(rgb.Z);
    }
    public byte[] ToBytes() => (byte[])_bytes.Clone();
}
