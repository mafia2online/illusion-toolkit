using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace Illusion.Formats.Shaders;

/// <summary>A retail PC DX11 constant descriptor. The final eight bytes remain opaque.</summary>
public sealed class NativeShaderConstantDescriptor
{
    private readonly byte[] _bytes;
    internal NativeShaderConstantDescriptor(byte[] bytes) => _bytes = bytes;
    public string Name => Encoding.ASCII.GetString(_bytes, 0, 4);
    public byte Kind => _bytes[4];
    public byte Slot => _bytes[5];
    public ushort Size => BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(6));
    public uint Offset => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(8));
    public ReadOnlyMemory<byte> Raw => _bytes;
}

/// <summary>One native table; Hash is the exact key used by binary shader records.</summary>
public sealed class NativeShaderConstantTable
{
    internal NativeShaderConstantTable(int fileOffset, byte flags, byte[] declarations,
        byte[] buffers, IReadOnlyList<NativeShaderConstantDescriptor> items, uint hash)
    {
        FileOffset = fileOffset; Flags = flags; RawDeclarations = declarations;
        RawBuffers = buffers; Items = items; Hash = hash;
    }
    public int FileOffset { get; }
    /// <summary>Uninterpreted table byte, retained verbatim.</summary>
    public byte Flags { get; }
    public uint Hash { get; }
    public IReadOnlyList<NativeShaderConstantDescriptor> Items { get; }
    public ReadOnlyMemory<byte> RawDeclarations { get; }
    public ReadOnlyMemory<byte> RawBuffers { get; }
}

/// <summary>Strict bounds-checked reader for retail PC shaders_constants_[dx11].sc.
/// Native hash is FNV1(seed zero) over sorted 20-byte descriptors, 8-byte buffer
/// descriptors, then little-endian texture/sampler uint64 masks. Unknown descriptor
/// fields, table bytes and trailing bytes are retained; no semantic role is inferred.</summary>
public sealed class NativeShaderConstantCatalog
{
    private readonly byte[] _original;
    private readonly Dictionary<uint, NativeShaderConstantTable> _byHash;
    private NativeShaderConstantCatalog(byte[] original, List<NativeShaderConstantTable> tables,
        Dictionary<uint, NativeShaderConstantTable> byHash, int end)
    { _original = original; Tables = tables.AsReadOnly(); _byHash = byHash; ParsedLength = end; }

    public ReadOnlyCollection<NativeShaderConstantTable> Tables { get; }
    public int ParsedLength { get; }
    public ReadOnlyMemory<byte> TrailingData => _original.AsMemory(ParsedLength);
    public byte[] ToArray() => _original.ToArray();
    public bool TryFind(uint hash, out NativeShaderConstantTable? table) => _byHash.TryGetValue(hash, out table);
    public NativeShaderConstantTable Find(uint hash) => _byHash.TryGetValue(hash, out var table)
        ? table : throw new InvalidDataException($"Native shader constant table {hash:X8} is absent.");

    public static NativeShaderConstantCatalog Read(ReadOnlySpan<byte> bytes)
    {
        var original = bytes.ToArray();
        int p = 0;
        uint count = ReadUInt(bytes, ref p);
        if (count > (uint)(bytes.Length - p) / 9)
            throw new InvalidDataException("Native shader constant table count exceeds the input.");
        var tables = new List<NativeShaderConstantTable>((int)count);
        var index = new Dictionary<uint, NativeShaderConstantTable>();
        Span<byte> masks = stackalloc byte[16];
        for (uint i = 0; i < count; i++)
        {
            int start = p;
            uint itemCount = ReadUInt(bytes, ref p);
            Require(bytes, p, 1); byte flags = bytes[p++];
            int declarationSize = Length(bytes, p, itemCount, 20);
            byte[] declarations = bytes.Slice(p, declarationSize).ToArray(); p += declarationSize;
            uint bufferCount = ReadUInt(bytes, ref p);
            int bufferSize = Length(bytes, p, bufferCount, 8);
            byte[] buffers = bytes.Slice(p, bufferSize).ToArray(); p += bufferSize;
            var items = new List<NativeShaderConstantDescriptor>((int)itemCount);
            var names = new HashSet<uint>(); ulong textures = 0, samplers = 0;
            for (int j = 0; j < declarations.Length; j += 20)
            {
                var raw = declarations.AsSpan(j, 20);
                if (!names.Add(BinaryPrimitives.ReadUInt32LittleEndian(raw)))
                    throw new InvalidDataException("Duplicate native shader parameter name.");
                byte kind = raw[4], slot = raw[5];
                if (kind is 1 or 2)
                {
                    if (slot >= 64) throw new InvalidDataException("Native shader resource slot exceeds its 64-bit mask.");
                    if (kind == 1) textures |= 1UL << slot; else samplers |= 1UL << slot;
                }
                items.Add(new(raw.ToArray()));
            }
            uint hash = Hash(0, declarations); hash = Hash(hash, buffers);
            BinaryPrimitives.WriteUInt64LittleEndian(masks, textures);
            BinaryPrimitives.WriteUInt64LittleEndian(masks[8..], samplers);
            hash = Hash(hash, masks);
            var table = new NativeShaderConstantTable(start, flags, declarations, buffers, items.AsReadOnly(), hash);
            if (index.TryGetValue(hash, out var existing))
            {
                if (!existing.RawDeclarations.Span.SequenceEqual(declarations) || !existing.RawBuffers.Span.SequenceEqual(buffers))
                    throw new InvalidDataException($"Conflicting native shader constant hash {hash:X8}.");
            }
            else index.Add(hash, table);
            tables.Add(table);
        }
        return new(original, tables, index, p);
    }

    private static uint Hash(uint seed, ReadOnlySpan<byte> bytes)
    { foreach (byte value in bytes) seed = unchecked(seed * 16777619) ^ value; return seed; }
    private static uint ReadUInt(ReadOnlySpan<byte> bytes, ref int p)
    { Require(bytes, p, 4); uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[p..]); p += 4; return value; }
    private static int Length(ReadOnlySpan<byte> bytes, int p, uint count, int stride)
    {
        if (count > (uint)((bytes.Length - p) / stride)) throw new InvalidDataException("Native shader constant array exceeds the input.");
        return checked((int)count * stride);
    }
    private static void Require(ReadOnlySpan<byte> bytes, int p, int length)
    { if (p < 0 || length < 0 || p > bytes.Length - length) throw new InvalidDataException("Truncated native shader constant catalog."); }
}
