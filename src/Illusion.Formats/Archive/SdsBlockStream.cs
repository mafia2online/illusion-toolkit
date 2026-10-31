using System.IO.Compression;
using Illusion.Formats.IO;

namespace Illusion.Formats.Archive;

/// <summary>
/// The <c>UEzl</c> block stream that carries an archive's (or a patch's) resource payloads: a short
/// header, then length-prefixed chunks that are individually zlib-deflated, then a terminator.
/// </summary>
/// <remarks>
/// Layout, little-endian throughout:
/// <code>
/// 'UEzl'            u32   signature
/// chunkSize         u32   0x4000 in every archive seen
/// version           u8    4
/// per chunk:
///     blockLength   u32   payload bytes that follow (compressed: 32-byte header + deflated data)
///     compressed    u8    1 = zlib, 0 = stored
///     [compressed only] 32-byte chunk header, then the deflated bytes
///     [stored only]     the raw bytes
/// terminator        u32   0
///                   u8    0
/// </code>
/// The terminator is five bytes, not four: a reader consumes length and flag together and stops on a
/// zero length, so the trailing flag byte is part of the stream. An empty stream is therefore exactly
/// fourteen bytes, which is what Mafia II's own no-op patch contains.
/// </remarks>
public static class SdsBlockStream
{
    /// <summary>Stream signature, <c>'UEzl'</c> read little-endian.</summary>
    public const uint Signature = 0x6C7A4555;

    /// <summary>Uncompressed bytes per chunk.</summary>
    public const int ChunkSize = 0x4000;

    private const byte StreamVersion = 4;
    private const int CompressedChunkHeaderSize = 32;

    /// <summary>An empty block stream — the payload of a patch that carries no resources.</summary>
    public static byte[] Empty
    {
        get
        {
            using var buffer = new MemoryStream();
            Write(buffer, ReadOnlySpan<byte>.Empty);
            return buffer.ToArray();
        }
    }

    /// <summary>Writes <paramref name="payload"/> as a block stream.</summary>
    public static void Write(Stream output, ReadOnlySpan<byte> payload, int chunkSize = ChunkSize, bool compress = true, float compressionRatio = 1f)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (chunkSize is <= 0 or > 0x10000) throw new ArgumentOutOfRangeException(nameof(chunkSize));

        output.Write(Signature, bigEndian: false);
        output.Write((uint)chunkSize, bigEndian: false);
        output.WriteByte(StreamVersion);

        for (var offset = 0; offset < payload.Length; offset += chunkSize)
        {
            var chunk = payload.Slice(offset, Math.Min(chunkSize, payload.Length - offset));
            var deflated = Deflate(chunk);

            // A chunk that does not get smaller is stored verbatim; the engine reads both forms.
            if (compress && deflated.Length < compressionRatio * chunk.Length && deflated.Length + CompressedChunkHeaderSize < chunk.Length && deflated.Length <= ushort.MaxValue)
            {
                output.Write((uint)(deflated.Length + CompressedChunkHeaderSize), bigEndian: false);
                output.WriteByte(1);
                WriteCompressedChunkHeader(output, chunk.Length, deflated.Length, chunkSize);
                output.Write(deflated, 0, deflated.Length);
            }
            else
            {
                output.Write((uint)chunk.Length, bigEndian: false);
                output.WriteByte(0);
                output.Write(chunk);
            }
        }

        output.Write(0u, bigEndian: false);
        output.WriteByte(0);
    }

    /// <summary>Reads a block stream written by <see cref="Write"/> or shipped by the game.</summary>
    public static byte[] Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ReadUInt32(bigEndian: false) != Signature)
        {
            throw new InvalidDataException("Not a UEzl block stream.");
        }

        uint alignment = input.ReadUInt32(bigEndian: false);
        if (input.ReadByte() != StreamVersion || alignment == 0 || alignment > 0x2000000)
            throw new InvalidDataException("Invalid UEzl stream header.");

        using var payload = new MemoryStream();
        while (true)
        {
            var blockLength = input.ReadUInt32(bigEndian: false);
            var compressed = input.ReadByte();
            if (compressed is not (0 or 1)) throw new InvalidDataException("Invalid UEzl compression flag.");
            if (blockLength == 0)
            {
                break;
            }

            if (blockLength > int.MaxValue || (input.CanSeek && blockLength > input.Length - input.Position))
                throw new InvalidDataException($"Invalid UEzl block length: {blockLength}.");

            if (compressed == 1)
            {
                if (blockLength < CompressedChunkHeaderSize)
                    throw new InvalidDataException($"UEzl compressed block is smaller than its header: {blockLength}.");
                var header = new byte[CompressedChunkHeaderSize];
                input.ReadExactly(header);

                uint rawLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
                uint headerSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                short count = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(10));
                if (headerSize != 32 || count < 1 || count > 8)
                    throw new InvalidDataException("Unsupported UEzl compressed header.");
                int compressedLength = 0;
                long start = payload.Length;
                for (int i = 0; i < count; i++)
                {
                    ushort length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16 + i * 2));
                    compressedLength += length;
                    if (compressedLength > blockLength - headerSize) throw new InvalidDataException("Invalid UEzl chunk sizes.");
                    byte[] deflated = new byte[length];
                    input.ReadExactly(deflated);
                    using var source = new MemoryStream(deflated);
                    using var inflater = new ZLibStream(source, CompressionMode.Decompress);
                    inflater.CopyTo(payload);
                }
                if (compressedLength != blockLength - headerSize || payload.Length - start != rawLength)
                    throw new InvalidDataException("UEzl decompressed length mismatch.");
            }
            else
            {
                var stored = new byte[blockLength];
                input.ReadExactly(stored);
                payload.Write(stored);
            }
        }

        return payload.ToArray();
    }

    private static byte[] Deflate(ReadOnlySpan<byte> chunk)
    {
        using var buffer = new MemoryStream();
        using (var deflater = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflater.Write(chunk);
        }

        return buffer.ToArray();
    }

    // Constant apart from the two lengths in every archive and patch inspected; the engine reads the
    // raw length from it and ignores the rest, but it is reproduced so output matches shipped files.
    private static void WriteCompressedChunkHeader(Stream output, int rawLength, int deflatedLength, int chunkSize)
    {
        output.Write((uint)rawLength, bigEndian: false);
        output.Write(0x20u, bigEndian: false);
        output.Write(unchecked((ushort)chunkSize), bigEndian: false);
        output.Write((ushort)1, bigEndian: false);
        output.Write((ushort)0x0001, bigEndian: false);
        output.WriteByte(0x0F);
        output.WriteByte(0x08);
        output.Write((uint)deflatedLength, bigEndian: false);
        output.Write(new byte[12]);
    }
}
