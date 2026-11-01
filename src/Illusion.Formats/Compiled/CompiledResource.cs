using System.Buffers.Binary;
using System.Text;

namespace Illusion.Formats.Compiled;

/// <summary>GENR v9 compound reflection streams. Edits patch typed values and preserve all other bytes.</summary>
public sealed partial class CompiledResource
{
    private readonly byte[] _bytes;
    private readonly List<(ulong Hash, List<CompiledElement> Roots)> _streams = [];
    private readonly int _directoryOffset;
    private readonly bool _metadataOnly;
    private readonly Dictionary<CompiledElement, byte[]> _cloneValues = [];
    private readonly Dictionary<CompiledElement, byte[]> _variableValues = [];
    private readonly Dictionary<CompiledElement, int> _originalSizes = [];
    private bool _variableSaved;
    public string Path { get; }
    public List<CompiledElement> Roots { get; } = [];
    public List<CompiledElement> Elements { get; } = [];
    public bool IsDirty { get; private set; }
    /// <summary>Monotonic typed-value revision; undo also advances it, so transient consumers cannot retain stale values.</summary>
    public long Revision { get; private set; }

    private CompiledResource(string path, byte[] bytes, bool metadataOnly = false)
    {
        Path = path;
        _bytes = bytes;
        _metadataOnly = metadataOnly;
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        if (bytes.Length < 16) throw new InvalidDataException("Truncated compiled-resource header.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        int position = checked(16 + (int)count * 6);
        if (position > bytes.Length - 33 || !bytes.AsSpan(position, 4).SequenceEqual("GENR"u8))
            throw new InvalidDataException("The resource is not a GENR reflection stream.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 4)) != 9)
            throw new NotSupportedException("Only GENR version 9 is supported.");
        stream.Position = position + 20;
        if (reader.ReadUInt32() != 6 || reader.ReadUInt32() != 4 || reader.ReadByte() != 1)
            throw new NotSupportedException("Unsupported GENR compound-stream version.");
        uint streams = reader.ReadUInt32();
        _directoryOffset = checked((int)stream.Position);
        if (streams > (bytes.Length - stream.Position) / 12) throw new InvalidDataException("Invalid GENR directory count.");
        var lengths = new List<(ulong Hash, uint Length)>();
        for (uint i = 0; i < streams; i++)
        {
            ulong hash = reader.ReadUInt64();
            lengths.Add((hash, reader.ReadUInt32()));
        }
        foreach (var (hash, length) in lengths)
        {
            var roots = new List<CompiledElement>();
            _streams.Add((hash, roots));
            long end = checked(stream.Position + length);
            if (end > bytes.Length) throw new InvalidDataException("GENR stream exceeds its resource.");
            if (metadataOnly && hash != 0xd82fc7186b1cf2f9) stream.Position = end;
            else while (stream.Position < end)
            {
                var root = ReadElement(reader, end, 0);
                Roots.Add(root);
                roots.Add(root);
            }
            if (stream.Position != end) throw new InvalidDataException("GENR stream length mismatch.");
        }
        if (stream.Position != bytes.Length) throw new InvalidDataException("Trailing GENR bytes.");
    }

    /// <summary>Clones a complete resource, remapping GUID definitions and exact eight/twelve-byte
    /// references (including ToC/dependency records and explicitly supplied cache identities).
    /// Reflection type IDs, stream hashes, allocation metadata and opaque larger blobs are preserved.</summary>
    public CompiledResource RemapIds(IReadOnlyDictionary<ulong, ulong> ids, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var current = new CompiledResource(Path, ToArray());
        byte[] bytes = current.ToArray();
        ulong Remap(ulong id) => id == 0 ? 0 : ids.GetValueOrDefault(id, id);
        var originals = current.Elements.Where(e => e.Event == 2 && e.ObjectId != 0).Select(e => e.ObjectId).Distinct().ToArray();
        if (originals.Any(id => Remap(id) == 0) || originals.Select(Remap).Distinct().Count() != originals.Length)
            throw new ArgumentException("GUID remapping would remove or merge object definitions.", nameof(ids));
        foreach (var element in current.Elements)
        {
            if (element.Event == 2 && element.ObjectId != 0)
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(element.Offset + 4), Remap(element.ObjectId));
            else if (element.Event == 4 && element.DataSize is 8 or 12)
            {
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(current.Data(element));
                if (id != 0 && ids.TryGetValue(id, out ulong replacement))
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(element.DataOffset), replacement);
            }
        }
        return new CompiledResource(path ?? Path, bytes);
    }

    public static CompiledResource Load(string path) => new(path, File.ReadAllBytes(path));
    public static CompiledResource FromBytes(byte[] bytes, string name = "memory") => new(name, (byte[])bytes.Clone());
    /// <summary>Reads the resource's GUID table without decoding its object/buffer stream.</summary>
    public static IReadOnlyList<ulong> ReadObjectIds(string path)
    {
        var metadata = new CompiledResource(path, File.ReadAllBytes(path), metadataOnly: true);
        return metadata.Elements.Where(e => e.Event == 4 && e.TypeId == 0x799a4041 && e.DataSize == 8)
            .Select(e => BinaryPrimitives.ReadUInt64LittleEndian(metadata.Data(e))).ToArray();
    }
    /// <summary>Captures the dirty marker for a caller that rolls back all of its staged mutations.</summary>
    public Action CaptureDirtyCheckpoint()
    {
        bool original = IsDirty;
        return () => IsDirty = original;
    }

    /// <summary>Captures revision and dirty state for a fully reverted, unobservable preflight.
    /// The caller must restore every payload and AST mutation before invoking the returned action.
    /// This does not restore bytes and must not be used for ordinary undo or a partial rollback.</summary>
    public Action CaptureRevertedMutationCheckpoint()
    {
        bool dirty = IsDirty; long revision = Revision;
        return () => { IsDirty = dirty; Revision = revision; };
    }

    public bool HasVariableChanges => _variableValues.Count != 0 || _variableSaved;
    public byte[] ToArray() => _variableValues.Count == 0 ? (byte[])_bytes.Clone()
        : Rewrite(new CompiledRewrite { AllowNativeAllocatorFallback = true }).ToArray();
    public ReadOnlySpan<byte> Data(CompiledElement element) => _cloneValues.TryGetValue(element, out var data)
        ? data : _variableValues.TryGetValue(element, out data) ? data : _bytes.AsSpan(element.DataOffset, element.DataSize);

    /// <summary>Stages a resized typed value without moving its canonical element or siblings. The PC
    /// allocator falls back to global allocations if its original contiguous allowance is exhausted.</summary>
    public void ReplaceVariable(CompiledElement element, ReadOnlySpan<byte> value)
    {
        if (element.Event != 4 || !Elements.Contains(element)) throw new ArgumentException("Value belongs to another resource.", nameof(element));
        if (Data(element).SequenceEqual(value)) return;
        if (_cloneValues.ContainsKey(element)) _cloneValues[element] = value.ToArray();
        else
        {
            if (!_originalSizes.TryGetValue(element, out int originalSize)) _originalSizes.Add(element, originalSize = element.DataSize);
            if (value.Length == originalSize && value.SequenceEqual(_bytes.AsSpan(element.DataOffset, originalSize))) _variableValues.Remove(element);
            else _variableValues[element] = value.ToArray();
        }
        element.DataSize = value.Length;
        IsDirty = true; Revision++;
    }

    public void Replace(CompiledElement element, ReadOnlySpan<byte> value)
    {
        if (element.Event != 4 || value.Length != element.DataSize)
            throw new ArgumentException("A compiled value replacement must preserve its serialized size.", nameof(value));
        if (Data(element).SequenceEqual(value)) return;
        if (_variableValues.ContainsKey(element)) { ReplaceVariable(element, value); return; }
        if (_cloneValues.TryGetValue(element, out var data)) value.CopyTo(data);
        else value.CopyTo(_bytes.AsSpan(element.DataOffset, element.DataSize));
        IsDirty = true; Revision++;
    }

    public void Save()
    {
        if (!IsDirty) return;
        if (HasVariableChanges) { SaveSnapshot(new CompiledRewrite { AllowNativeAllocatorFallback = true }); return; }
        string temporary = Path + ".illusion.tmp";
        try
        {
            File.WriteAllBytes(temporary, _bytes);
            File.Move(temporary, Path, overwrite: true);
            IsDirty = false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private CompiledElement ReadElement(BinaryReader reader, long limit, int depth)
    {
        if (depth > 256 || Elements.Count > 2_000_000) throw new InvalidDataException("GENR hierarchy exceeds supported limits.");
        int start = checked((int)reader.BaseStream.Position);
        uint tag = reader.ReadUInt32();
        CompiledElement element;
        switch (tag)
        {
            case 0:
                element = new CompiledElement
                {
                    Event = tag, Offset = start, TypeId = reader.ReadUInt32(),
                    TypeFlags = reader.ReadByte(), TypeKind = reader.ReadUInt32(), NativeSize = reader.ReadUInt32()
                };
                break;
            case 2:
                element = new CompiledElement { Event = tag, Offset = start, ObjectId = reader.ReadUInt64(), TypeId = reader.ReadUInt32() };
                break;
            case 4:
                uint type = reader.ReadUInt32();
                int length = checked((int)reader.ReadUInt32());
                if (length > limit - reader.BaseStream.Position) throw new InvalidDataException("Truncated GENR value.");
                element = new CompiledElement
                {
                    Event = tag, Offset = start, TypeId = type,
                    DataOffset = checked((int)reader.BaseStream.Position), DataSize = length
                };
                reader.BaseStream.Position += length;
                break;
            case 5:
                int nameLength = reader.ReadByte();
                byte[] name = reader.ReadBytes(nameLength);
                if (name.Length != nameLength) throw new EndOfStreamException();
                element = new CompiledElement { Event = tag, Offset = start, Name = Encoding.UTF8.GetString(name) };
                break;
            case 1 or 3:
                element = new CompiledElement { Event = tag, Offset = start };
                break;
            default:
                throw new InvalidDataException($"Unknown GENR element {tag} at 0x{start:X}.");
        }
        Elements.Add(element);
        if (tag is 0 or 2)
        {
            uint terminator = tag == 0 ? 1u : 3u;
            while (true)
            {
                var child = ReadElement(reader, limit, depth + 1);
                if (child.Event == terminator) break;
                if (child.Event is 1 or 3) throw new InvalidDataException("Mismatched GENR terminator.");
                element.Children.Add(child);
            }
        }
        if (reader.BaseStream.Position > limit) throw new InvalidDataException("Truncated GENR element.");
        element.EndOffset = checked((int)reader.BaseStream.Position);
        return element;
    }
}
