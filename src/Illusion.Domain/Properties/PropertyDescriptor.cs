namespace Illusion.Domain.Properties;

/// <summary>
/// One editable (or read-only) field of a scene object, described so a UI can render and commit it without knowing
/// the concrete backend type. The <see cref="Get"/> / <see cref="Set"/> delegates close over the underlying object,
/// so a held descriptor keeps working across rebuilds (undo/redo re-applies through the same <see cref="Set"/>).
/// </summary>
/// <remarks>
/// <see cref="Get"/> returns, and <see cref="Set"/> expects, the CLR type fixed by <see cref="Kind"/> (see
/// <see cref="PropertyKind"/>). <see cref="Set"/> is null exactly when <see cref="IsReadOnly"/> is true.
/// </remarks>
public sealed class PropertyDescriptor
{
    /// <summary>Stable identity within its object (e.g. "Mesh.MeshIndex") — the key undo/redo and probes key on.
    /// Must not change across rebuilds for the same logical field.</summary>
    public required string Id { get; init; }

    /// <summary>Human label shown beside the editor.</summary>
    public required string Label { get; init; }

    public required PropertyKind Kind { get; init; }

    /// <summary>When true, the field is shown but not editable and <see cref="Set"/> is null.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>Optional hover text — a read-only reason, a persistence caveat, or a reverse-engineering hint.</summary>
    public string? Tooltip { get; init; }

    /// <summary>Inclusive lower bound for <see cref="PropertyKind.Int"/> (byte/short/int range, or 0 for an index).</summary>
    public long Min { get; init; } = long.MinValue;

    /// <summary>Inclusive upper bound for <see cref="PropertyKind.Int"/> (byte/short/int range, or count-1 for an index).</summary>
    public long Max { get; init; } = long.MaxValue;

    /// <summary>The individual bits a <see cref="PropertyKind.Flags"/> editor offers; null for every other kind.</summary>
    public IReadOnlyList<PropertyFlagItem>? FlagItems { get; init; }

    /// <summary>Reads the current value (boxed per <see cref="Kind"/>).</summary>
    public required Func<object?> Get { get; init; }

    /// <summary>Writes a new value (boxed per <see cref="Kind"/>). Null iff <see cref="IsReadOnly"/>.</summary>
    public Action<object?>? Set { get; init; }

    /// <summary>Optional compound setter. Applies the value and returns its already-applied reversible
    /// mutation, for fields whose changes also recook buffers or alter resource registrations.</summary>
    public Func<object?, IEditAction>? ApplyEdit { get; init; }
}

/// <summary>One named bit of a <see cref="PropertyKind.Flags"/> value.</summary>
public readonly record struct PropertyFlagItem(string Name, long Value);
