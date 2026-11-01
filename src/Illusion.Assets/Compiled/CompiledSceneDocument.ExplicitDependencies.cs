using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private sealed record ExplicitEntry(CompiledElement Row) { public int References { get; set; } }
    private readonly Dictionary<(CompiledResource Resource, ulong Key), ExplicitEntry> _vehicleExplicitEntries = [];

    private IEnumerable<CompiledElement> CurrentExplicitDependencyEntries(CompiledResource resource)
        => Graph.NodesOfType(0x4b7e79a0).Where(node => ReferenceEquals(Graph.Owner(node), resource))
            .Select(node => Graph.Field(node, "m_ExplicitDependencies")).OfType<CompiledElement>().SelectMany(CurrentItems);

    private IEditAction CarryVehicleExplicitDependencies(CompiledResource destination, IReadOnlyList<CompiledResource> sources)
        => CarryExplicitDependencies(destination, sources);

    private IEditAction CarryExplicitDependencies(CompiledResource destination, IReadOnlyList<CompiledResource> sources,
        IReadOnlySet<ulong>? required = null)
    {
        var container = Graph.Nodes.Where(n => ReferenceEquals(Graph.Owner(n), destination))
            .Select(n => Graph.Field(n, "m_ExplicitDependencies")).OfType<CompiledElement>().Single();
        var existing = CurrentItems(container).ToDictionary(e => CompiledDependencyToken.EntryKey(Graph, e));
        var incoming = new Dictionary<ulong, CompiledElement>();
        foreach (var source in sources)
            foreach (var row in ReferenceEquals(source, destination) ? CurrentExplicitDependencyEntries(source) : CompiledDependencyToken.Entries(Graph, source))
            {
                ulong key = CompiledDependencyToken.EntryKey(Graph, row);
                if (required is not null && !required.Contains(key)) continue;
                if (incoming.TryGetValue(key, out var previous) && !Graph.Data(previous).SequenceEqual(Graph.Data(row)))
                    throw new NotSupportedException($"Native dependency token {key:X16} has different source values.");
                if (existing.TryGetValue(key, out var current) && !Graph.Data(current).SequenceEqual(Graph.Data(row)))
                    throw new NotSupportedException($"Native dependency token {key:X16} conflicts with the destination header.");
                incoming.TryAdd(key, row);
            }
        if (required is not null && required.Any(key => !incoming.ContainsKey(key)))
            throw new NotSupportedException("An imported native explicit dependency is missing from its source resource header.");
        var managed = new List<ExplicitEntry>();
        foreach (var (key, row) in incoming)
        {
            if (_vehicleExplicitEntries.TryGetValue((destination, key), out var cached))
            {
                if (!Graph.Data(cached.Row).SequenceEqual(Graph.Data(row))) throw new NotSupportedException("An imported native dependency changed; reload before importing it again.");
                managed.Add(cached); continue;
            }
            if (existing.ContainsKey(key)) continue;
            var clone = destination.CopySubtree(Graph.Owner(row), row, new Dictionary<ulong, ulong>());
            Graph.RegisterSubtree(destination, clone); Graph.SetSubtreeActive(destination, clone, false);
            if (Graph.ValidateCopySubtree(destination, clone).Count != 0)
                throw new NotSupportedException("The destination lacks the native explicit-dependency entry schema.");
            var entry = new ExplicitEntry(clone); _vehicleExplicitEntries.Add((destination, key), entry); managed.Add(entry);
        }
        var result = new ExplicitDependenciesEdit(this, destination, container, managed);
        result.Redo(); return result;
    }

    private sealed class ExplicitDependenciesEdit(CompiledSceneDocument document, CompiledResource resource,
        CompiledElement container, IReadOnlyList<ExplicitEntry> entries) : IEditAction
    {
        private bool _applied;
        public void Redo()
        {
            if (_applied) return;
            foreach (var entry in entries)
                if (entry.References++ == 0)
                {
                    document.Graph.SetSubtreeActive(resource, entry.Row, true);
                    document.SetItems(container, document.CurrentItems(container).Append(entry.Row).ToArray());
                }
            _applied = true;
        }
        public void Undo()
        {
            if (!_applied) return;
            foreach (var entry in entries.Reverse())
                if (--entry.References == 0)
                {
                    document.SetItems(container, document.CurrentItems(container).Where(row => !ReferenceEquals(row, entry.Row)).ToArray());
                    document.Graph.SetSubtreeActive(resource, entry.Row, false);
                }
            _applied = false;
        }
    }
}
