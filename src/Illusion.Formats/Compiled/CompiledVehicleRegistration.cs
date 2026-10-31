using System.Buffers.Binary;
using System.Text;

namespace Illusion.Formats.Compiled;

/// <summary>PC vehicle and paint-combination vectors, preserving unknown row fields and mount trailers.</summary>
public sealed class CompiledVehicleRegistration
{
    private byte[] _bytes;
    private readonly bool _paint;
    private int _rows;
    public int Count { get; private set; }
    public bool IsPaint => _paint;
    public uint TrafficCommonFlags(int row) => !_paint ? BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(Offset(row) + 4))
        : throw new InvalidOperationException("Paint rows have no traffic flags.");
    public void ExcludeFromTraffic(int row)
    {
        if (_paint) throw new InvalidOperationException("Paint rows have no traffic flags.");
        BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(Offset(row) + 4), 0);
    }
    private int Stride => _paint ? 48 : 232;
    private int NameOffset => _paint ? 16 : 20;

    private CompiledVehicleRegistration(byte[] bytes, bool paint, int rows, int count)
    { _bytes = bytes; _paint = paint; _rows = rows; Count = count; }

    public static CompiledVehicleRegistration? TryRead(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 44) return null;
        ulong type = BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..]);
        if (type is not (0xa869f8a3ac7656e1 or 0xdc3279441da347e2)) return null;
        bool paint = type == 0xdc3279441da347e2;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]);
        long rows = 32L + BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]);
        int stride = paint ? 48 : 232;
        if (size > bytes.Length - 20 || count > 100000 || rows < 44 || rows + count * (long)stride > 20L + size)
            throw new InvalidDataException("Invalid compiled vehicle registration vector.");
        var result = new CompiledVehicleRegistration(bytes.ToArray(), paint, (int)rows, (int)count);
        if (paint) for (int row = 0; row < count; row++) _ = result.Colors(row);
        return result;
    }

    private int Offset(int row)
    {
        if ((uint)row >= Count) throw new ArgumentOutOfRangeException(nameof(row));
        return _rows + row * Stride;
    }
    public uint Id(int row) => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(Offset(row)));
    public string Name(int row)
    {
        var text = _bytes.AsSpan(Offset(row) + NameOffset, 32);
        int end = text.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? text : text[..end]);
    }
    public int Find(string name) => Enumerable.Range(0, Count).FirstOrDefault(i => Name(i).Equals(name, StringComparison.OrdinalIgnoreCase), -1);
    public ulong DisplayTextId(int row) => !_paint ? BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(Offset(row) + 96))
        : throw new InvalidOperationException("Paint rows have no vehicle display name.");
    public void SetDisplayName(int row, ulong textId, string modelKey)
    {
        if (_paint) throw new InvalidOperationException("Paint rows have no vehicle display name.");
        byte[] key = Encoding.UTF8.GetBytes(modelKey);
        if (key.Length > 31 || modelKey.Contains('\0')) throw new ArgumentException("Vehicle model UI keys require at most31 UTF-8 bytes.", nameof(modelKey));
        int at = Offset(row);
        BinaryPrimitives.WriteUInt64LittleEndian(_bytes.AsSpan(at + 96), textId);
        _bytes.AsSpan(at + 104, 64).Clear(); // full custom title replaces the source's brand/model combination
        key.CopyTo(_bytes, at + 136);
    }
    public int[] Colors(int row)
    {
        if (!_paint) throw new InvalidOperationException("This is not a paint-combination table.");
        int field = Offset(row) + 4;
        long data = field + (long)BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(field));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(field + 4));
        long end = 20L + BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(8));
        if (count > 100000 || data < 44 || data + count * 4L > end)
            throw new InvalidDataException("Invalid compiled paint choices.");
        var colors = new int[count];
        for (int i = 0; i < colors.Length; i++) colors[i] = BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan((int)data + 4 * i));
        return colors;
    }

    public void SetColor(int row, int choice, int colorIndex)
    {
        int[] colors = Colors(row);
        if ((uint)choice >= colors.Length || colorIndex < 0) throw new ArgumentOutOfRangeException(nameof(choice));
        int field = Offset(row) + 4;
        int data = checked(field + BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(field)));
        BinaryPrimitives.WriteInt32LittleEndian(_bytes.AsSpan(data + choice * 4), colorIndex);
    }

    /// <summary>Append an independent row cloned from a compatible table. Existing data remains at its original offsets.</summary>
    public void Append(CompiledVehicleRegistration source, int sourceRow, uint id, string name)
    {
        if (source._paint != _paint) throw new ArgumentException("Incompatible registration table.", nameof(source));
        byte[] text = Encoding.UTF8.GetBytes(name);
        if (text.Length is 0 or > 31 || text.Contains((byte)0)) throw new ArgumentException("Model names require 1–31 UTF-8 bytes.", nameof(name));
        if (Find(name) >= 0 || Enumerable.Range(0, Count).Any(i => Id(i) == id))
            throw new InvalidOperationException("Vehicle name or ID is already registered.");
        byte[] sourceBytes = source._bytes.AsSpan(source.Offset(sourceRow), Stride).ToArray();
        int[] colors = _paint ? source.Colors(sourceRow) : [];
        int end = checked(20 + (int)BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(8)));
        int rows = (end + 7) & ~7;
        int choices = checked(rows + (Count + 1) * Stride);
        int newEnd = checked((choices + colors.Length * 4 + 7) & ~7);
        var result = new byte[checked(_bytes.Length + newEnd - end)];
        _bytes.AsSpan(0, end).CopyTo(result);
        _bytes.AsSpan(end).CopyTo(result.AsSpan(newEnd));
        for (int row = 0; row < Count; row++)
        {
            int oldAt = Offset(row), newAt = rows + row * Stride;
            _bytes.AsSpan(oldAt, Stride).CopyTo(result.AsSpan(newAt));
            if (_paint)
            {
                int target = checked(oldAt + 4 + BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(oldAt + 4)));
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(newAt + 4), target - newAt - 4);
            }
        }
        int added = rows + Count * Stride;
        sourceBytes.CopyTo(result, added);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(added), id);
        result.AsSpan(added + NameOffset, 32).Clear(); text.CopyTo(result, added + NameOffset);
        if (_paint)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(added + 4), choices - added - 4);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(added + 8), (uint)colors.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(added + 12), (uint)colors.Length);
            for (int i = 0; i < colors.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(choices + 4 * i), colors[i]);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)(newEnd - 20));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(32), rows - 32);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36), (uint)++Count);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40), (uint)Count);
        _rows = rows; _bytes = result;
    }
    public byte[] ToBytes() => (byte[])_bytes.Clone();
}
