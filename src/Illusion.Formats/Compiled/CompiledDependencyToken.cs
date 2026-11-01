using System.Buffers.Binary;
using System.Text;

namespace Illusion.Formats.Compiled;

/// <summary>GENR explicit dependency tokens, whose keys are local to a resource header.</summary>
public static class CompiledDependencyToken
{
    public static ulong Key(CompiledGraph graph, CompiledElement token)
    {
        if (token.Event != 4 || token.DataSize != 8 || graph.Kind(token) != 442)
            throw new NotSupportedException("The field is not a native explicit dependency token.");
        var bytes = graph.Data(token);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != token.TypeId)
            throw new NotSupportedException("The custom scalar is not a resource-local explicit dependency token.");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0 ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    public static ulong EntryKey(CompiledGraph graph, CompiledElement entry)
    {
        if (entry.Event != 4 || entry.DataSize < 12) throw new InvalidDataException("Truncated native explicit dependency entry.");
        return BinaryPrimitives.ReadUInt64LittleEndian(graph.Data(entry));
    }

    public static IEnumerable<CompiledElement> Entries(CompiledGraph graph, CompiledResource resource)
        => graph.NodesOfType(0x4b7e79a0).Where(n => ReferenceEquals(graph.Owner(n), resource))
            .Select(n => graph.Field(n, "m_ExplicitDependencies")).OfType<CompiledElement>().SelectMany(graph.Items);

    /// <summary>Resolves a C_String dependency through its own GENR header, never through another resource's token table.</summary>
    public static string? ResolveName(CompiledGraph graph, CompiledElement token, IEnumerable<CompiledElement>? entries = null)
    {
        ulong key = Key(graph, token); if (key == 0) return null;
        var matches = (entries ?? Entries(graph, graph.Owner(token))).Where(e => EntryKey(graph, e) == key).ToArray();
        if (matches.Length == 0) throw new InvalidDataException($"Native dependency token {key:X16} is unresolved in its resource header.");
        var data = graph.Data(matches[0]);
        foreach (var entry in matches)
            if (!graph.Data(entry).SequenceEqual(data)) throw new InvalidDataException("A native dependency token has conflicting values.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != 0x153e26b2 || data.Length < 14 || data[^1] != 0
            || data[12..^1].Contains((byte)0)) throw new NotSupportedException("The native explicit dependency does not contain a terminated C_String.");
        return new UTF8Encoding(false, true).GetString(data[12..^1]);
    }
}
