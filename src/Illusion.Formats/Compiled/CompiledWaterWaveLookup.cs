using System.Buffers.Binary;

namespace Illusion.Formats.Compiled;

/// <summary>User supplied native sincos.dds, stored as legacy L8A8 and sampled by the engine
/// as linear R8G8 UNORM. No game texture bytes are embedded in the toolkit.</summary>
public sealed class CompiledWaterWaveLookup
{
    public int Width => 64;
    public int Height => 1;
    public IReadOnlyList<ReadOnlyMemory<byte>> Mips { get; }
    public ReadOnlyMemory<byte> Rg8Bytes => Mips[0];
    private CompiledWaterWaveLookup(ReadOnlyMemory<byte>[] mips) => Mips = Array.AsReadOnly(mips);
    public static CompiledWaterWaveLookup Load(string path) => Read(File.ReadAllBytes(path));
    public static CompiledWaterWaveLookup Read(ReadOnlySpan<byte> bytes)
    {
        static uint U(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
        if (bytes.Length < 128 || U(bytes, 0) != 0x20534444 || U(bytes, 4) != 124 || U(bytes, 76) != 32
            || U(bytes, 12) != 1 || U(bytes, 16) != 64 || U(bytes, 80) != 0x20001 || U(bytes, 84) != 0 || U(bytes, 88) != 16
            || U(bytes, 92) != 0xff || U(bytes, 96) != 0 || U(bytes, 100) != 0 || U(bytes, 104) != 0xff00
            || U(bytes, 112) != 0) throw new InvalidDataException("Expected native 64x1 L8A8 sincos.dds lookup.");
        uint rawCount = U(bytes, 28);
        if (rawCount > 7) throw new InvalidDataException("Invalid sincos lookup mip count.");
        int count = (int)Math.Max(rawCount, 1);
        var mips = new ReadOnlyMemory<byte>[count]; int offset = 128;
        for (int level = 0; level < count; level++)
        {
            int size = Math.Max(1, 64 >> level) * 2;
            if (offset + size > bytes.Length) throw new InvalidDataException("Truncated sincos lookup mip data.");
            mips[level] = bytes.Slice(offset, size).ToArray(); offset += size;
        }
        if (offset != bytes.Length) throw new InvalidDataException("Unexpected trailing sincos lookup data.");
        return new(mips);
    }
}
