using System.Buffers.Binary;
using System.Text;

namespace Illusion.Formats.Compiled;

/// <summary>Mafia III sorted u64 localization keys with relative UTF-8 strings.</summary>
public sealed class CompiledStringTable
{
    private byte[] _bytes;
    private readonly int _rows;
    public int Count { get; private set; }
    private CompiledStringTable(byte[] bytes, int rows, int count) { _bytes = bytes; _rows = rows; Count = count; }
    public static CompiledStringTable? TryRead(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 44 || BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..]) != 0x5e42ef29e8a3e1d3) return null;
        long end = 20L + BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        long rows = 32L + BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]);
        if (end > bytes.Length || rows < 44 || count > 10000000 || rows + count * 16L > end)
            throw new InvalidDataException("Invalid Mafia III localized string vector.");
        var result = new CompiledStringTable(bytes.ToArray(), (int)rows, (int)count);
        for (int i = 0; i < result.Count; i++)
            if (i > 0 && result.Id(i) < result.Id(i - 1)) throw new InvalidDataException("Localization keys are not sorted.");
        return result;
    }
    private int Row(int row)
    { if ((uint)row >= Count) throw new ArgumentOutOfRangeException(nameof(row)); return _rows + row * 16; }
    public ulong Id(int row) => BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(Row(row)));
    public int Find(ulong id)
    {
        int low = 0, high = Count - 1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2; ulong current = Id(mid);
            if (current == id) return mid;
            if (current < id) low = mid + 1; else high = mid - 1;
        }
        return -1;
    }
    public string Text(int row)
    {
        int field = Row(row) + 8, relative = BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(field));
        if (relative == -1) return "";
        long target = field + (long)relative;
        int end = checked(20 + (int)BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(8)));
        if (target < _rows + Count * 16L || target >= end) throw new InvalidDataException("Invalid localized string pointer.");
        int zero = Array.IndexOf(_bytes, (byte)0, (int)target, end - (int)target);
        if (zero < 0) throw new InvalidDataException("Unterminated localized string.");
        return Encoding.UTF8.GetString(_bytes, (int)target, zero - (int)target);
    }
    public void Add(ulong id, string text, ulong templateId)
    {
        if (text.Contains('\0')) throw new ArgumentException("Localized text cannot contain null characters.", nameof(text));
        if (Find(id) >= 0) throw new InvalidOperationException("The localization key already exists.");
        int template = Find(templateId);
        if (template < 0) throw new InvalidOperationException("The source vehicle title is absent from this language table.");
        byte[] encoded = Encoding.UTF8.GetBytes(text);
        int index = 0; while (index < Count && Id(index) < id) index++;
        int insertion = _rows + index * 16;
        int end = checked(20 + (int)BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(8)));
        int stringAt = checked(end + 16), padding = checked((encoded.Length + 1 + 7) & ~7);
        var result = new byte[checked(_bytes.Length + 16 + padding)];
        _bytes.AsSpan(0, insertion).CopyTo(result);
        _bytes.AsSpan(insertion, end - insertion).CopyTo(result.AsSpan(insertion + 16));
        _bytes.AsSpan(end).CopyTo(result.AsSpan(stringAt + padding));
        for (int row = 0; row < Count; row++)
        {
            int before = Row(row), after = before + (row >= index ? 16 : 0);
            int relative = BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(before + 8));
            if (relative == -1) continue;
            _ = Text(row); // validate every pointer before publishing a rewritten table
            int target = checked(before + 8 + relative);
            if (target >= insertion) target += 16;
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(after + 8), checked(target - after - 8));
        }
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(insertion), id);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(insertion + 8), stringAt - insertion - 8);
        _bytes.AsSpan(Row(template) + 12, 4).CopyTo(result.AsSpan(insertion + 12));
        encoded.CopyTo(result, stringAt);
        // Stock tables contain duplicate keys with different strings. Inserting a key changes
        // binary-search midpoints; retain the exact record the native lookup previously chose.
        for (int first = 0; first < Count;)
        {
            int last = first;
            while (last + 1 < Count && Id(last + 1) == Id(first)) last++;
            if (last > first)
            {
                int oldWinner = Find(Id(first));
                int originalAt = Row(oldWinner) + (oldWinner >= index ? 16 : 0);
                int low = 0, high = Count, newWinner = -1;
                while (low <= high)
                {
                    int mid = low + (high - low) / 2;
                    ulong current = BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(_rows + mid * 16));
                    if (current == Id(first)) { newWinner = mid; break; }
                    if (current < Id(first)) low = mid + 1; else high = mid - 1;
                }
                int selectedAt = _rows + newWinner * 16;
                if (newWinner < 0) throw new InvalidDataException("A localized key disappeared during insertion.");
                if (selectedAt != originalAt)
                {
                    byte[] original = result.AsSpan(originalAt, 16).ToArray(), selected = result.AsSpan(selectedAt, 16).ToArray();
                    int originalRelative = BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(8));
                    int selectedRelative = BinaryPrimitives.ReadInt32LittleEndian(selected.AsSpan(8));
                    original.CopyTo(result, selectedAt); selected.CopyTo(result, originalAt);
                    if (originalRelative != -1) BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(selectedAt + 8), originalAt + originalRelative - selectedAt);
                    if (selectedRelative != -1) BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(originalAt + 8), selectedAt + selectedRelative - originalAt);
                }
            }
            first = last + 1;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)(end + 16 + padding - 20)));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36), checked((uint)++Count));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40), (uint)Count);
        _bytes = result;
    }
    public byte[] ToBytes() => (byte[])_bytes.Clone();
}
