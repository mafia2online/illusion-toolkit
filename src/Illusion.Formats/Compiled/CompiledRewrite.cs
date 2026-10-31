using System.Buffers.Binary;

namespace Illusion.Formats.Compiled;

/// <summary>An atomic reflection-tree rewrite. Input elements belong to the original resource;
/// the result is a new resource, so callers can retain the original for undo.</summary>
public sealed class CompiledRewrite
{
    public Dictionary<CompiledElement, IReadOnlyList<CompiledElement>> Children { get; } = [];
    public Dictionary<CompiledElement, byte[]> Values { get; } = [];

    /// <summary>Explicit native allocation budget, when a verified operation needs additional
    /// allocations. Without this, rewrites may only maintain or shrink the serialized payload.</summary>
    public uint? AllocationBudget { get; set; }
    /// <summary>Use the verified PC resource allocator's global-allocation fallback if its original
    /// contiguous allowance is exhausted. No estimated extra allowance is written to the resource.</summary>
    public bool AllowNativeAllocatorFallback { get; set; }
    public Dictionary<uint, int> AllocationCountDeltas { get; } = [];
    public Dictionary<uint, byte[]> AdditionalSchemas { get; } = [];
    public int SchemaStreamIndex { get; set; }
}

public sealed partial class CompiledResource
{
    public int StreamIndex(CompiledElement element)
    {
        if (!Elements.Contains(element)) throw new ArgumentException("Element belongs to another resource.", nameof(element));
        for (int i = 0; i < _streams.Count; i++)
            if (_streams[i].Roots.Any(root => element.Offset >= root.Offset && element.Offset < root.EndOffset)) return i;
        throw new NotSupportedException("Element has no original stream anchor.");
    }

    public byte[] ReadSchema(CompiledElement schema)
    {
        if (!Elements.Contains(schema) || schema.Event != 0) throw new ArgumentException("Not a schema of this resource.", nameof(schema));
        return _bytes.AsSpan(schema.Offset, schema.EndOffset - schema.Offset).ToArray();
    }

    /// <summary>Creates independent live values while retaining serialized type schemas in the source.
    /// The clone is not persisted until included in a rewrite's child lists.</summary>
    public CompiledElement CloneSubtree(CompiledElement source, IReadOnlyDictionary<ulong, ulong> ids, CompiledRewrite? state = null)
        => CopySubtree(this, source, ids, state);

    /// <summary>Copies values into this resource. Callers must verify that every compound schema
    /// used by the copied tree is already declared in the destination.</summary>
    public CompiledElement CopySubtree(CompiledResource origin, CompiledElement source, IReadOnlyDictionary<ulong, ulong> ids, CompiledRewrite? state = null)
    {
        if (!origin.Elements.Contains(source) || source.Event is 0 or 1 or 3 or 5)
            throw new ArgumentException("Copy source must be an object or typed value of its origin resource.", nameof(source));
        CompiledElement Clone(CompiledElement original)
        {
            var clone = new CompiledElement
            {
                Event = original.Event, TypeId = original.TypeId,
                ObjectId = ids.GetValueOrDefault(original.ObjectId, original.ObjectId),
                TypeKind = original.TypeKind, TypeFlags = original.TypeFlags, NativeSize = original.NativeSize,
                Name = original.Name, Offset = original.Offset, DataOffset = original.DataOffset,
                DataSize = original.DataSize, EndOffset = original.EndOffset
            };
            Elements.Add(clone);
            if (original.Event == 4)
            {
                byte[] data = state is not null && state.Values.TryGetValue(original, out var stagedValue) ? (byte[])stagedValue.Clone() : origin.Data(original).ToArray();
                if (data.Length is 8 or 12 && ids.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(data), out ulong replacement))
                    BinaryPrimitives.WriteUInt64LittleEndian(data, replacement);
                _cloneValues.Add(clone, data);
            }
            var children = state is not null && state.Children.TryGetValue(original, out var stagedChildren) ? stagedChildren : original.Children;
            foreach (var child in children.Where(c => c.Event != 0)) clone.Children.Add(Clone(child));
            return clone;
        }
        return Clone(source);
    }

    public void SetClonedValue(CompiledElement value, byte[] data)
    {
        if (!_cloneValues.ContainsKey(value)) throw new ArgumentException("Only independent cloned values can resize.", nameof(value));
        _cloneValues[value] = (byte[])data.Clone();
        value.DataSize = data.Length;
    }

    public CompiledElement CloneElement(CompiledElement original)
    {
        if (!Elements.Contains(original)) throw new ArgumentException("Clone source belongs to another resource.", nameof(original));
        var clone = new CompiledElement
        {
            Event = original.Event, TypeId = original.TypeId, ObjectId = original.ObjectId,
            TypeKind = original.TypeKind, TypeFlags = original.TypeFlags, NativeSize = original.NativeSize,
            Name = original.Name, Offset = original.Offset, DataOffset = original.DataOffset,
            DataSize = original.DataSize, EndOffset = original.EndOffset
        };
        Elements.Add(clone);
        if (original.Event == 4) _cloneValues.Add(clone, Data(original).ToArray());
        return clone;
    }

    public CompiledElement CreateValue(uint type, ReadOnlySpan<byte> data)
    {
        var value = new CompiledElement { Event = 4, TypeId = type, DataSize = data.Length };
        Elements.Add(value);
        _cloneValues.Add(value, data.ToArray());
        return value;
    }

    /// <summary>Writes an independently validated rewrite without changing the live elements or
    /// their offsets. Staged topology plans can remain attached to undo descriptors after save.</summary>
    public void SaveSnapshot(CompiledRewrite changes)
    {
        byte[] bytes = Rewrite(changes).ToArray();
        string temporary = Path + ".illusion.tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, Path, overwrite: true);
            _variableSaved |= _variableValues.Count != 0;
            IsDirty = false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Rebuilds compound stream lengths while preserving stream hashes, native allocation
    /// metadata, GUIDs and schema declarations. This does not repair application-level references
    /// or container counts: those must be included in the same atomic rewrite.</summary>
    public CompiledResource Rewrite(CompiledRewrite changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (_metadataOnly) throw new InvalidOperationException("Cannot rewrite a partially decoded resource.");
        var owned = Elements.ToHashSet();
        foreach (var (parent, children) in changes.Children)
        {
            if (!owned.Contains(parent) || parent.Event is not (0 or 2))
                throw new ArgumentException("A rewritten parent must be a compound element of this resource.", nameof(changes));
            if (children.Any(child => !owned.Contains(child) || child.Event is 1 or 3))
                throw new ArgumentException("Replacement children must belong to this resource.", nameof(changes));
        }
        foreach (var value in changes.Values.Keys)
            if (!owned.Contains(value) || value.Event != 4)
                throw new ArgumentException("A rewritten value must belong to this resource.", nameof(changes));

        IReadOnlyList<CompiledElement> ChildrenOf(CompiledElement element) =>
            changes.Children.TryGetValue(element, out var children) ? children : element.Children;

        var retained = new HashSet<CompiledElement>();
        void Visit(CompiledElement element, int depth)
        {
            if (depth > 256 || !retained.Add(element))
                throw new InvalidDataException("A rewritten tree contains a cycle or shared element.");
            foreach (var child in ChildrenOf(element)) Visit(child, depth + 1);
        }
        foreach (var root in Roots) Visit(root, 0);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        uint oldCount = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(12));
        var allocations = new Dictionary<uint, int>();
        for (int i = 0; i < oldCount; i++)
            allocations.Add(BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(16 + i * 4)),
                BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(16 + checked((int)oldCount) * 4 + i * 2)));
        foreach (var (type, delta) in changes.AllocationCountDeltas)
            allocations[type] = checked(allocations.GetValueOrDefault(type) + delta);
        if (allocations.Values.Any(count => count is < 0 or > ushort.MaxValue))
            throw new NotSupportedException("Allocation count exceeds the supported 16-bit count encoding.");
        writer.Write(_bytes.AsSpan(0, 12));
        writer.Write(checked((uint)allocations.Count));
        foreach (uint type in allocations.Keys) writer.Write(type);
        foreach (int count in allocations.Values) writer.Write(checked((ushort)count));
        int oldHeader = checked(16 + (int)oldCount * 6);
        writer.Write(_bytes.AsSpan(oldHeader, _directoryOffset - oldHeader));
        long directoryOffset = output.Position;
        output.Position += _streams.Count * 12;
        var lengths = new List<uint>();
        var hoisted = new HashSet<CompiledElement>();
        if (changes.AdditionalSchemas.Count != 0 && (changes.SchemaStreamIndex < 0 || changes.SchemaStreamIndex >= _streams.Count))
            throw new ArgumentException("Invalid schema stream index.", nameof(changes));
        int streamIndex = 0;
        foreach (var (_, roots) in _streams)
        {
            long start = output.Position;
            if (streamIndex++ == changes.SchemaStreamIndex)
                foreach (var schema in changes.AdditionalSchemas.Values) writer.Write(schema);
            // A removed inline object can contain the first declaration of a type used later.
            // Keep that declaration in its original stream, before any remaining values.
            void PreserveSchemas(CompiledElement element, bool detached)
            {
                if (detached && element.Event == 0)
                {
                    if (hoisted.Add(element))
                        writer.Write(_bytes.AsSpan(element.Offset, element.EndOffset - element.Offset));
                    return;
                }
                var current = ChildrenOf(element);
                for (int i = 0; i < element.Children.Count; i++)
                {
                    var child = element.Children[i];
                    PreserveSchemas(child, detached || i >= current.Count || !ReferenceEquals(current[i], child));
                }
            }
            foreach (var root in roots) PreserveSchemas(root, false);
            foreach (var root in roots) WriteElement(root);
            lengths.Add(checked((uint)(output.Position - start)));
        }
        if (output.Length > _bytes.Length && changes.AllocationBudget is null && !changes.AllowNativeAllocatorFallback)
            throw new NotSupportedException("Growing a compiled resource requires a verified native allocation budget.");
        output.Position = directoryOffset;
        for (int i = 0; i < _streams.Count; i++)
        {
            writer.Write(_streams[i].Hash);
            writer.Write(lengths[i]);
        }
        byte[] bytes = output.ToArray();
        if (changes.AllocationBudget is uint budget)
        {
            uint original = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
            if (budget < original) throw new ArgumentOutOfRangeException(nameof(changes), "The native allocation budget cannot shrink.");
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), budget);
        }
        return new CompiledResource(Path, bytes);

        void WriteElement(CompiledElement element)
        {
            if (hoisted.Contains(element)) return;
            switch (element.Event)
            {
                case 0 or 2:
                    if (element.Event == 0) writer.Write(_bytes.AsSpan(element.Offset, 17));
                    else { writer.Write(2u); writer.Write(element.ObjectId); writer.Write(element.TypeId); }
                    foreach (var child in ChildrenOf(element)) WriteElement(child);
                    writer.Write(element.Event == 0 ? 1u : 3u);
                    break;
                case 4:
                    ReadOnlySpan<byte> data = changes.Values.TryGetValue(element, out var value) ? value : Data(element);
                    writer.Write(4u);
                    writer.Write(element.TypeId);
                    writer.Write(checked((uint)data.Length));
                    writer.Write(data);
                    break;
                default:
                    writer.Write(_bytes.AsSpan(element.Offset, element.EndOffset - element.Offset));
                    break;
            }
        }
    }
}
