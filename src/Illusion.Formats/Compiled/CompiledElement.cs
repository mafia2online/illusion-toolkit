namespace Illusion.Formats.Compiled;

/// <summary>An element of Fusion's binary reflection stream, with offsets into its compiled file.</summary>
public sealed class CompiledElement
{
    public uint Event { get; internal init; }
    public uint TypeId { get; internal init; }
    public ulong ObjectId { get; internal init; }
    public uint TypeKind { get; internal init; }
    public byte TypeFlags { get; internal init; }
    public uint NativeSize { get; internal init; }
    public string? Name { get; internal init; }
    public int Offset { get; internal init; }
    public int DataOffset { get; internal init; }
    public int DataSize { get; internal set; }
    public int EndOffset { get; internal set; }
    public List<CompiledElement> Children { get; } = [];
    public IEnumerable<CompiledElement> Values => Children.Where(e => e.Event is 2 or 4);
}
