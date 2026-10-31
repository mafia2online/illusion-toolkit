using System.Buffers.Binary;
using System.Text;

namespace Illusion.Formats.Compiled;

/// <summary>CarTuningItem vector inside Mafia III's S_Tables SystemObjectDatabase.</summary>
public sealed class CompiledCarTuningTable
{
    private readonly byte[] _bytes;
    private readonly int _rows;
    public int Count { get; }
    public bool IsDirty { get; private set; }
    private CompiledCarTuningTable(byte[] bytes, int rows, int count)
    { _bytes = bytes; _rows = rows; Count = count; }

    public static CompiledCarTuningTable? TryRead(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 72 || BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..]) != 0x5d69a41cdc82936c) return null;
        uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (payloadSize > bytes.Length - 20) throw new InvalidDataException("Truncated Mafia III tables resource.");
        long table = 68L + BinaryPrimitives.ReadInt32LittleEndian(bytes[68..]);
        if (table < 72 || table > 20L + payloadSize - 12) throw new InvalidDataException("Invalid CarTuningItem table pointer.");
        int position = checked((int)table);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + 4)..]);
        long rows = table + BinaryPrimitives.ReadInt32LittleEndian(bytes[position..]);
        if (count > 100000 || rows < table + 12 || rows + 212L * count > 20L + payloadSize)
            throw new InvalidDataException("Invalid CarTuningItem row extent.");
        return new CompiledCarTuningTable(bytes.ToArray(), checked((int)rows), checked((int)count));
    }
    private int Offset(int row, int field)
    {
        if ((uint)row >= Count || field < 0 || field > 208 || field % 4 != 0) throw new ArgumentOutOfRangeException(nameof(row));
        return _rows + row * 212 + field;
    }
    public string Description(int row)
    {
        var text = _bytes.AsSpan(Offset(row, 8), 32);
        int end = text.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? text : text[..end]);
    }
    public int ReadInt32(int row, int field) => BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(Offset(row, field)));
    public uint ReadUInt32(int row, int field) => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(Offset(row, field)));
    public float ReadFloat(int row, int field) => BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(Offset(row, field)));
    public void WriteFloat(int row, int field, float value)
    {
        if (!(field is >= 60 and <= 104 or >= 112 and <= 180 or >= 192 and <= 208) || field % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(field));
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(Offset(row, field)), value); IsDirty = true;
    }
    public void WriteGearbox(int row, int value)
    {
        if (value is < -1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        BinaryPrimitives.WriteInt32LittleEndian(_bytes.AsSpan(Offset(row, 108)), value); IsDirty = true;
    }
    public void WriteUInt32(int row, int field, uint value)
    {
        if (field is not (40 or 184 or 188)) throw new ArgumentOutOfRangeException(nameof(field));
        BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(Offset(row, field)), value); IsDirty = true;
    }
    public byte[] ToBytes() => (byte[])_bytes.Clone();
}
