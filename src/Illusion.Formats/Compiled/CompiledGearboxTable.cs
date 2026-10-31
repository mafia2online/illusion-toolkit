using System.Buffers.Binary;
using System.Text;

namespace Illusion.Formats.Compiled;

/// <summary>Mafia III /xbin/car_gearboxes table, preserving its relocation trailer and unknown bytes.</summary>
public sealed class CompiledGearboxTable
{
    private readonly byte[] _bytes;
    public int Count { get; }
    private readonly int _rows;
    public bool IsDirty { get; private set; }
    private CompiledGearboxTable(byte[] bytes, int rows, int count)
    { _bytes = bytes; _rows = rows; Count = count; }

    public static CompiledGearboxTable? TryRead(ReadOnlySpan<byte> bytes)
    {
        // Serialized S_CarGearboxesTable type GUID. Restrict this adapter to this exact table.
        if (bytes.Length < 44 || BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..]) != 0x3759788ec437536c) return null;
        uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]);
        int relative = BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]);
        long start = 32L + relative, end = start + 148L * count;
        if (payloadSize > bytes.Length - 20 || count > 100000 || relative < 12 || start < 44 || end > 20L + payloadSize)
            throw new InvalidDataException("Invalid Mafia III gearbox table extent.");
        return new CompiledGearboxTable(bytes.ToArray(), checked((int)start), checked((int)count));
    }

    private int Offset(int row, int field, int size = 4)
    {
        if ((uint)row >= Count || field < 0 || field > 148 - size) throw new ArgumentOutOfRangeException(nameof(row));
        return _rows + row * 148 + field;
    }
    public string Description(int row)
    {
        var text = _bytes.AsSpan(Offset(row, 4, 32), 32);
        int end = text.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? text : text[..end]);
    }
    public uint ReadUInt32(int row, int field) => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(Offset(row, field)));
    public float ReadFloat(int row, int field) => BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(Offset(row, field)));
    public bool ReadAutomatic(int row) => _bytes[Offset(row, 36, 1)] != 0;
    public void WriteUInt32(int row, int field, uint value)
    {
        if (field is not (40 or 44)) throw new ArgumentOutOfRangeException(nameof(field));
        if (value > 7) throw new ArgumentOutOfRangeException(nameof(value), "The gearbox has seven ratio slots.");
        BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(Offset(row, field)), value); IsDirty = true;
    }
    public void WriteFloat(int row, int field, float value)
    {
        if (field < 48 || field > 144 || field % 4 != 0) throw new ArgumentOutOfRangeException(nameof(field));
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(Offset(row, field)), value); IsDirty = true;
    }
    public void WriteAutomatic(int row, bool value)
    { _bytes[Offset(row, 36, 1)] = value ? (byte)1 : (byte)0; IsDirty = true; }
    public byte[] ToBytes() => (byte[])_bytes.Clone();
}
