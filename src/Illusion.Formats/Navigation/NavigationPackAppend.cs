using System.Buffers.Binary;

namespace Illusion.Formats.Navigation;

/// <summary>Appends flat arrays to the final native data section, preserving all object addresses.</summary>
internal sealed class NavigationPackAppend
{
    private readonly byte[] _source;
    private readonly int _start, _local, _global, _virtual, _end;
    private readonly MemoryStream _data = new();
    private readonly Dictionary<int, int> _replaced = [];
    private const int Header = 100 + 2 * 64;
    private int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(_source.AsSpan(at));
    public NavigationPackAppend(byte[] source)
    {
        _source = source;
        if (source.Length > 64 * 1024 * 1024) throw new NotSupportedException("Navigation append source exceeds the bounded resource size.");
        if (I(40) != 3) throw new NotSupportedException("Navigation growth requires the stock three-section Win64 packfile.");
        _start = checked(20 + I(Header + 20)); _local = I(Header + 24); _global = I(Header + 28);
        _virtual = I(Header + 32); _end = I(Header + 36);
        if (_start + _end != source.Length) throw new NotSupportedException("Navigation data section is not the final file extent.");
        _data.Write(source.AsSpan(_start, _local));
    }
    public int Append(ReadOnlySpan<byte> bytes)
    {
        if (_start + _data.Length + bytes.Length + 16 > 64 * 1024 * 1024) throw new NotSupportedException("Navigation growth exceeds the bounded resource size.");
        while ((_data.Length & 15) != 0) _data.WriteByte(0);
        int address = checked(_start + (int)_data.Length); _data.Write(bytes); return address;
    }
    public void Write(int address, ReadOnlySpan<byte> bytes)
    {
        if (address < _start || address > _start + _data.Length - bytes.Length) throw new InvalidDataException("Navigation append write exceeds data.");
        long position = _data.Position; _data.Position = address - _start; _data.Write(bytes); _data.Position = position;
    }
    public void Int(int address, int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); Write(address, bytes); }
    public void Array(int header, byte[] data, int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        int target = Append(data); Write(header, new byte[8]); Int(header + 8, count); Int(header + 12, count | unchecked((int)0x80000000));
        _replaced.Add(header - _start, target - _start);
    }
    public byte[] Finish()
    {
        using var writer = new BinaryWriter(_data, System.Text.Encoding.ASCII, leaveOpen: true);
        void Pad() { while ((_data.Position & 15) != 0) writer.Write((byte)255); }
        Pad(); int local = checked((int)_data.Position);
        for (int p = _local; p <= _global - 8; p += 8)
            if (I(_start + p) >= 0 && !_replaced.ContainsKey(I(_start + p))) writer.Write(_source.AsSpan(_start + p, 8));
        foreach (var (from, to) in _replaced.OrderBy(p => p.Key)) { writer.Write(from); writer.Write(to); }
        Pad(); int global = checked((int)_data.Position);
        for (int p = _global; p <= _virtual - 12; p += 12)
            if (I(_start + p) >= 0 && !_replaced.ContainsKey(I(_start + p))) writer.Write(_source.AsSpan(_start + p, 12));
        Pad(); int virtuals = checked((int)_data.Position);
        for (int p = _virtual; p <= _end - 12; p += 12) if (I(_start + p) >= 0) writer.Write(_source.AsSpan(_start + p, 12));
        Pad(); int end = checked((int)_data.Position);
        byte[] result = new byte[checked(_start + end)]; _source.AsSpan(0, _start).CopyTo(result); _data.ToArray().CopyTo(result, _start);
        foreach (var (offset, value) in new[] { (24, local), (28, global), (32, virtuals), (36, end), (40, end), (44, end) })
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(Header + offset), value);
        BinaryPrimitives.WriteInt32LittleEndian(result, result.Length - 4);
        _ = Mafia3NavMeshFile.Read(result); return result;
    }
}
