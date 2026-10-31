using System.Buffers.Binary;
using System.Text;
using Illusion.Formats.Hashing;

namespace Illusion.Formats.Archive;

/// <summary>PC version-20 envelopes, including the per-resource 64-bit identifier.</summary>
internal static class SdsV20
{
    internal static SdsArchive Load(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        byte[] prefix = ReadSafe(reader, 12);
        if (BinaryPrimitives.ReadUInt32BigEndian(prefix) != SdsArchive.Signature
            || BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4)) != 20)
            throw new SdsFormatException("Invalid version-20 SDS prefix.");
        if (BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(8)) != (uint)Platform.PC)
            throw new NotSupportedException("Console archives are not supported.");
        byte[] header = ReadSafe(reader, 52);
        uint H(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(offset));
        var archive = new SdsArchive
        {
            Version = 20, Flags = H(28), SlotRamRequired = H(12), SlotVramRequired = H(16),
            OtherRamRequired = H(20), OtherVramRequired = H(24), Unknown20 = header[32..48],
        };
        stream.Position = H(0);
        uint count = reader.ReadUInt32();
        if (count > (stream.Length - stream.Position) / 12) throw new SdsFormatException("Invalid type count.");
        for (uint i = 0; i < count; i++)
        {
            uint id = reader.ReadUInt32();
            int length = reader.ReadInt32();
            if (length < 0 || length > 1023) throw new SdsFormatException("Invalid type name length.");
            byte[] name = reader.ReadBytes(length);
            if (name.Length != length) throw new EndOfStreamException();
            archive.ResourceTypes.Add(new SdsResourceTypeEntry { Id = id, Name = Encoding.ASCII.GetString(name), Parent = reader.ReadUInt32() });
        }
        stream.Position = H(4);
        using var content = new MemoryStream(SdsBlockStream.Read(stream), writable: false);
        using var entries = new BinaryReader(content);
        if (H(48) > content.Length / 38) throw new SdsFormatException("Invalid resource count.");
        for (uint i = 0; i < H(48); i++)
        {
            using var raw = new MemoryStream(ReadSafe(entries, 34), writable: false);
            using var fields = new BinaryReader(raw);
            int type = fields.ReadInt32();
            uint size = fields.ReadUInt32();
            var entry = new ResourceEntry
            {
                TypeId = type, Version = fields.ReadUInt16(), FileHash = fields.ReadUInt64(),
                SlotRamRequired = fields.ReadUInt32(), SlotVramRequired = fields.ReadUInt32(),
                OtherRamRequired = fields.ReadUInt32(), OtherVramRequired = fields.ReadUInt32()
            };
            if (type < 0 || type >= archive.ResourceTypes.Count || size < 38 || size - 38 > content.Length - content.Position)
                throw new SdsFormatException("Invalid version-20 resource header.");
            entry.Data = entries.ReadBytes(checked((int)size - 38));
            archive.Entries.Add(entry);
        }
        if (content.Position != content.Length) throw new SdsFormatException("Trailing resource bytes.");
        if (H(8) != 0)
        {
            stream.Position = H(8);
            archive.ResourceInfoXml = Encoding.ASCII.GetString(reader.ReadBytes(checked((int)(stream.Length - stream.Position))));
        }
        return archive;
    }

    internal static void Save(SdsArchive archive, Stream output, SdsWriteOptions options)
    {
        if (archive.Platform != Platform.PC || archive.Version != 20)
            throw new NotSupportedException("Only PC version-20 archives are supported by this writer.");
        if (archive.Unknown20.Length != 16) throw new SdsFormatException("Invalid SDS header bytes.");
        foreach (var entry in archive.Entries)
            if (entry.TypeId < 0 || entry.TypeId >= archive.ResourceTypes.Count)
                throw new SdsFormatException("Resource type is outside the archive type table.");
        using var types = new MemoryStream();
        using (var writer = new BinaryWriter(types, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(archive.ResourceTypes.Count);
            foreach (var type in archive.ResourceTypes)
            {
                byte[] name = Encoding.ASCII.GetBytes(type.Name);
                writer.Write(type.Id); writer.Write(name.Length); writer.Write(name); writer.Write(type.Parent);
            }
        }
        using var content = new MemoryStream();
        using (var writer = new BinaryWriter(content, Encoding.ASCII, leaveOpen: true))
        {
            foreach (var entry in archive.Entries)
            {
                using var header = new MemoryStream();
                using (var fields = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true))
                {
                    fields.Write(entry.TypeId); fields.Write(checked(38u + (uint)(entry.Data?.Length ?? 0)));
                    fields.Write(entry.Version); fields.Write(entry.FileHash);
                    fields.Write(entry.SlotRamRequired); fields.Write(entry.SlotVramRequired);
                    fields.Write(entry.OtherRamRequired); fields.Write(entry.OtherVramRequired);
                }
                WriteSafe(writer, header.ToArray());
                writer.Write(entry.Data ?? []);
            }
        }
        using var blocks = new MemoryStream();
        SdsBlockStream.Write(blocks, content.ToArray(), 0x10000, options.Compress, options.CompressionRatio);
        using var result = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        byte[] prefix = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, SdsArchive.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), 20);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(8), (uint)Platform.PC);
        WriteSafe(result, prefix);
        using var fileHeader = new MemoryStream();
        using (var fields = new BinaryWriter(fileHeader, Encoding.ASCII, leaveOpen: true))
        {
            fields.Write(72u); fields.Write(checked(72u + (uint)types.Length));
            fields.Write(archive.ResourceInfoXml is null ? 0u : checked(72u + (uint)types.Length + (uint)blocks.Length));
            fields.Write(archive.SlotRamRequired); fields.Write(archive.SlotVramRequired);
            fields.Write(archive.OtherRamRequired); fields.Write(archive.OtherVramRequired); fields.Write(archive.Flags);
            if (archive.Unknown20.Length != 16) throw new SdsFormatException("Invalid SDS header bytes.");
            fields.Write(archive.Unknown20); fields.Write(archive.Entries.Count);
        }
        WriteSafe(result, fileHeader.ToArray());
        result.Write(types.ToArray()); result.Write(blocks.ToArray());
        if (archive.ResourceInfoXml is not null) result.Write(Encoding.ASCII.GetBytes(archive.ResourceInfoXml));
    }

    private static byte[] ReadSafe(BinaryReader reader, int length)
    {
        byte[] data = reader.ReadBytes(length);
        if (data.Length != length) throw new EndOfStreamException();
        if (reader.ReadUInt32() != Fnv32.Hash(data, 0, data.Length)) throw new SdsFormatException("SDS checksum mismatch.");
        return data;
    }

    private static void WriteSafe(BinaryWriter writer, byte[] data)
    {
        writer.Write(data); writer.Write(Fnv32.Hash(data, 0, data.Length));
    }
}
