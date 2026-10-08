using Illusion.Formats.Hashing;
using Illusion.Formats.IO;

namespace Illusion.Formats.Archive;

/// <summary>
/// A Mafia II <c>.sds.patch</c>: a delta applied to one base <c>.sds</c> as the engine streams it.
/// </summary>
/// <remarks>
/// <para>
/// The engine consults a patch provider for every archive it loads and, on a hit, applies the patch
/// during the archive's own load. Three operations are expressed, all keyed by <b>0-based ordinals
/// into the base archive's resource-entry list, in file order</b>:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="SkippedEntryIndices"/> — the base resource is dropped before a
/// resource manager is even resolved, so it never reaches a slot or a scene.</description></item>
/// <item><description><see cref="DeltaEntryIndices"/> — the base resource's body is rebuilt from an
/// old/new binary delta carried in the payload. Version 2 only.</description></item>
/// <item><description><see cref="Entries"/> — resources appended to the archive, indistinguishable
/// downstream from ones the archive shipped with.</description></item>
/// </list>
/// <para>
/// Replacement can also be expressed without a delta encoder: skip the base ordinal and append the
/// successor. Rockstar's own district patches do both — <c>italy_z</c> skips three nav resources and
/// delta-replaces four more.
/// </para>
/// </remarks>
public sealed class SdsPatchFile
{
    /// <summary>File signature.</summary>
    public const uint Signature = 0x0D010F0F;

    /// <summary>Second signature, immediately after the version.</summary>
    public const uint Marker = 0xF0F0010D;

    /// <summary>Bytes of a resource record that precede its payload: a 26-byte header and a checksum.</summary>
    private const int ResourceHeaderSize = 30;

    /// <summary>Format version. Every patch shipped with the game is 2; the delta list needs 2.</summary>
    public uint Version { get; set; } = 2;

    /// <summary>
    /// Types introduced by the patch, for resources whose type the base archive does not declare.
    /// A type id the base already declares is silently discarded by the engine, so this cannot be
    /// used to redefine one. Empty in every patch the game ships.
    /// </summary>
    public List<SdsResourceTypeEntry> ResourceTypes { get; } = new();

    /// <summary>Base-archive ordinals to drop. Sorted ascending on write.</summary>
    public List<int> SkippedEntryIndices { get; } = new();

    /// <summary>Base-archive ordinals to rebuild from a binary delta. Sorted ascending on write.</summary>
    public List<int> DeltaEntryIndices { get; } = new();

    /// <summary>Resources carried by the patch: appended resources, and the bodies for the delta list.</summary>
    /// <remarks>
    /// Populated on read only when <see cref="DeltaEntryIndices"/> is empty — see <see cref="Payload"/>.
    /// </remarks>
    public List<ResourceEntry> Entries { get; } = new();

    /// <summary>
    /// The decompressed payload exactly as it was read, and the only usable form when the patch
    /// carries binary deltas.
    /// </summary>
    /// <remarks>
    /// A delta record's header <c>size</c> is the size of the <i>reconstructed</i> resource, not the
    /// bytes the patch actually holds — Rockstar's <c>italy_z</c> patch declares 902,664 for a record
    /// inside a 497,153-byte payload. The delta stream self-terminates, so records cannot be walked
    /// without decoding it, and that encoding is not modelled here. Patches this class writes never
    /// contain deltas, so they always round-trip.
    /// </remarks>
    public byte[] Payload { get; private set; } = [];

    /// <summary>Resource count declared in the header, whether or not the records were parsed.</summary>
    public uint DeclaredResourceCount { get; private set; }

    /// <summary>Reads a patch. The byte-flipped console form is refused, as it is on the read side of the toolkit.</summary>
    public static SdsPatchFile Load(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var patch = new SdsPatchFile();

        var signature = input.ReadUInt32(bigEndian: false);
        FormatAssert.Ensure(signature == Signature, "Not an SDS patch: signature 0x{0:X8}.", signature);

        patch.Version = input.ReadUInt32(bigEndian: false);

        var marker = input.ReadUInt32(bigEndian: false);
        FormatAssert.Ensure(marker == Marker, "SDS patch marker 0x{0:X8} is wrong.", marker);

        var typeCount = input.ReadUInt32(bigEndian: false);
        for (var i = 0u; i < typeCount; i++)
        {
            var id = input.ReadUInt32(bigEndian: false);
            var nameLength = input.ReadUInt32(bigEndian: false);
            var name = input.ReadString(nameLength);
            var parent = input.ReadUInt32(bigEndian: false);
            patch.ResourceTypes.Add(new SdsResourceTypeEntry { Id = id, Name = name, Parent = parent });
        }

        ReadIndexList(input, patch.SkippedEntryIndices);
        if (patch.Version >= 2)
        {
            ReadIndexList(input, patch.DeltaEntryIndices);
        }

        patch.DeclaredResourceCount = input.ReadUInt32(bigEndian: false);
        patch.Payload = SdsBlockStream.Read(input);

        if (patch.DeltaEntryIndices.Count == 0)
        {
            ReadResources(patch.Payload, patch.DeclaredResourceCount, patch.Entries);
        }

        return patch;
    }

    /// <summary>Writes the patch. Index lists are sorted here, so callers need not pre-sort them.</summary>
    public void Save(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        Validate();

        var skipped = SkippedEntryIndices.Order().ToList();
        var delta = DeltaEntryIndices.Order().ToList();

        output.Write(Signature, bigEndian: false);
        output.Write(Version, bigEndian: false);
        output.Write(Marker, bigEndian: false);

        output.Write((uint)ResourceTypes.Count, bigEndian: false);
        foreach (var type in ResourceTypes)
        {
            output.Write(type.Id, bigEndian: false);
            output.Write((uint)EndianStreamExtensions.DefaultEncoding.GetByteCount(type.Name), bigEndian: false);
            output.WriteString(type.Name);
            output.Write(type.Parent, bigEndian: false);
        }

        WriteIndexList(output, skipped);
        if (Version >= 2)
        {
            WriteIndexList(output, delta);
        }

        output.Write((uint)Entries.Count, bigEndian: false);
        SdsBlockStream.Write(output, BuildResourcePayload());
    }

    /// <summary>
    /// Throws when the patch would be rejected or would misbehave in the engine. The empty case is
    /// the important one: a patch with nothing in it crashes the game, some distance from the load.
    /// </summary>
    public void Validate()
    {
        if (SkippedEntryIndices.Count == 0 && DeltaEntryIndices.Count == 0 && Entries.Count == 0)
        {
            throw new InvalidOperationException(
                "An empty patch crashes the engine — write no patch when a diff produces no changes.");
        }

        if (Version < 2 && DeltaEntryIndices.Count > 0)
        {
            throw new InvalidOperationException("Binary-delta replacement needs version 2.");
        }

        var overlap = SkippedEntryIndices.Intersect(DeltaEntryIndices).Order().ToList();
        if (overlap.Count > 0)
        {
            throw new InvalidOperationException(
                $"Ordinals appear in both the skip and delta lists: {string.Join(", ", overlap)}.");
        }

        var negative = SkippedEntryIndices.Concat(DeltaEntryIndices).Any(index => index < 0);
        if (negative)
        {
            throw new InvalidOperationException("Resource ordinals cannot be negative.");
        }

        if (SkippedEntryIndices.Distinct().Count() != SkippedEntryIndices.Count ||
            DeltaEntryIndices.Distinct().Count() != DeltaEntryIndices.Count)
        {
            throw new InvalidOperationException("Resource ordinals must be unique within a list.");
        }
    }

    private byte[] BuildResourcePayload()
    {
        using var payload = new MemoryStream();
        foreach (var entry in Entries)
        {
            var data = entry.Data ?? [];

            var header = new byte[26];
            using (var headerStream = new MemoryStream(header))
            {
                headerStream.Write((uint)entry.TypeId, bigEndian: false);
                headerStream.Write((uint)(data.Length + ResourceHeaderSize), bigEndian: false);
                headerStream.Write(entry.Version, bigEndian: false);
                headerStream.Write(entry.SlotRamRequired, bigEndian: false);
                headerStream.Write(entry.SlotVramRequired, bigEndian: false);
                headerStream.Write(entry.OtherRamRequired, bigEndian: false);
                headerStream.Write(entry.OtherVramRequired, bigEndian: false);
            }

            payload.Write(header);
            payload.Write(Fnv32.Hash(header, 0, header.Length), bigEndian: false);
            payload.Write(data);
        }

        return payload.ToArray();
    }

    private static void ReadResources(byte[] payload, uint count, List<ResourceEntry> entries)
    {
        using var source = new MemoryStream(payload);
        for (var i = 0u; i < count; i++)
        {
            var entry = new ResourceEntry
            {
                TypeId = (int)source.ReadUInt32(bigEndian: false),
            };

            var size = source.ReadUInt32(bigEndian: false);
            if (size < ResourceHeaderSize || size - 8 > source.Length - source.Position)
                throw new InvalidDataException($"Invalid SDS patch resource size: {size}.");
            entry.Version = source.ReadUInt16(bigEndian: false);
            entry.SlotRamRequired = source.ReadUInt32(bigEndian: false);
            entry.SlotVramRequired = source.ReadUInt32(bigEndian: false);
            entry.OtherRamRequired = source.ReadUInt32(bigEndian: false);
            entry.OtherVramRequired = source.ReadUInt32(bigEndian: false);

            // The per-resource checksum is validated by neither the engine nor the toolkit's reader.
            source.ReadUInt32(bigEndian: false);

            entry.Data = new byte[size - ResourceHeaderSize];
            source.ReadExactly(entry.Data);
            entries.Add(entry);
        }
    }

    private static void ReadIndexList(Stream input, List<int> target)
    {
        var count = input.ReadUInt32(bigEndian: false);
        for (var i = 0u; i < count; i++)
        {
            target.Add(input.ReadInt32(bigEndian: false));
        }
    }

    private static void WriteIndexList(Stream output, List<int> values)
    {
        output.Write((uint)values.Count, bigEndian: false);
        foreach (var value in values)
        {
            output.Write(value, bigEndian: false);
        }
    }
}
