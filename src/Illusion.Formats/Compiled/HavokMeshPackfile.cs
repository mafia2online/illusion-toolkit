using System.Buffers.Binary;
using System.Text;

namespace Illusion.Formats.Compiled;

/// <summary>Checked, non-mutating Havok 2014.2 Win64 packfile fixup view.</summary>
internal sealed class HavokMeshPackfile
{
    private readonly byte[] _data;
    private readonly record struct Section(int Start, int Local, int Global, int Virtual, int End);
    private readonly Section[] _sections;
    private readonly Dictionary<int, int> _pointers = [];
    public IReadOnlyList<(string Type, int Address)> Variants { get; }
    public HavokMeshPackfile(ReadOnlySpan<byte> input)
    {
        _data = input.ToArray(); Check(0, 80);
        if (I(0) != 0x57e0e057 || I(4) != 0x10c0c010 || I(12) != 11 || _data[16] != 8 || _data[17] != 1 ||
            !_data.AsSpan(40, 14).SequenceEqual("hk_2014.2.0-r1"u8) || _data[54] != 0 || _data[55] != 255)
            throw new NotSupportedException("Only Mafia III Havok 2014.2 little-endian Win64 packfiles are supported.");
        int count = I(20); if (count is < 1 or > 32) throw new InvalidDataException("Invalid Havok section count.");
        Check(80, checked(count * 64)); _sections = new Section[count];
        for (int i = 0; i < count; i++)
        {
            int h = 80 + i * 64; var s = new Section(I(h + 20), I(h + 24), I(h + 28), I(h + 32), I(h + 36));
            if (s.Local < 0 || s.Global < s.Local || s.Virtual < s.Global || s.End < s.Virtual) throw new InvalidDataException("Invalid Havok fixup ranges.");
            Check(s.Start, s.End); _sections[i] = s;
        }
        for (int i = 0; i < count; i++)
        {
            var s = _sections[i];
            for (int p = s.Local; p <= s.Global - 8; p += 8) if (I(s.Start + p) >= 0)
                Add(i, I(s.Start + p), i, I(s.Start + p + 4));
            for (int p = s.Global; p <= s.Virtual - 12; p += 12) if (I(s.Start + p) >= 0)
                Add(i, I(s.Start + p), I(s.Start + p + 4), I(s.Start + p + 8));
        }
        int root = Address(I(24), I(28), 16), n = I(root + 8);
        if (n is < 0 or > 100000) throw new InvalidDataException("Invalid Havok root variant count.");
        var variants = new List<(string, int)>(); int array = n == 0 ? 0 : Pointer(root); CheckObject(array, checked(n * 24));
        for (int i = 0; i < n; i++)
        {
            int entry = array + i * 24, text = Pointer(entry + 8), end = text;
            while (Byte(end) != 0) { end++; CheckObject(text, end - text + 1); }
            variants.Add((Encoding.ASCII.GetString(_data, text, end - text), Pointer(entry + 16)));
        }
        Variants = variants;
    }
    private void Add(int section, int offset, int targetSection, int target)
    {
        int from = Address(section, offset, 8), to = Address(targetSection, target, 1);
        if (!_pointers.TryAdd(from, to)) throw new InvalidDataException("Duplicate Havok pointer fixup.");
    }
    private int Address(int section, int offset, int size)
    {
        if ((uint)section >= _sections.Length || offset < 0 || size < 0 || offset > _sections[section].Local - size)
            throw new InvalidDataException("Havok pointer exceeds its data section.");
        return checked(_sections[section].Start + offset);
    }
    public void CheckObject(int p, int size)
    {
        if (size == 0) return;
        if (size < 0 || !_sections.Any(s => p >= s.Start && (long)p + size <= (long)s.Start + s.Local))
            throw new InvalidDataException("Havok object extent exceeds its data section.");
    }
    private void Check(int p, int size) { if (p < 0 || size < 0 || p > _data.Length - size) throw new InvalidDataException("Truncated Havok packfile."); }
    public int Pointer(int p) => _pointers.TryGetValue(p, out int to) ? to : throw new InvalidDataException("Missing Havok pointer fixup.");
    public (int Address, int Count) Array(int p, int stride)
    {
        CheckObject(p, 16); int count = I(p + 8);
        if (count < 0 || count > 100000000 || stride <= 0 || count > int.MaxValue / stride) throw new InvalidDataException("Invalid Havok array count.");
        int address = count == 0 ? 0 : Pointer(p); CheckObject(address, checked(count * stride)); return (address, count);
    }
    public byte Byte(int p) { Check(p, 1); return _data[p]; }
    public int I(int p) { Check(p, 4); return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(p)); }
    public uint U(int p) => unchecked((uint)I(p));
    public ushort U16(int p) { Check(p, 2); return BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(p)); }
    public ulong U64(int p) { Check(p, 8); return BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(p)); }
    public float F(int p) => BitConverter.Int32BitsToSingle(I(p));
}
