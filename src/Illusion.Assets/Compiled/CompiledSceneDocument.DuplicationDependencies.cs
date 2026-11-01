using System.Buffers.Binary;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private IEditAction? RetainCopiedDefinitions(CompiledResource resource, CompiledElement source, int schemaStream)
    {
        var schemas = Graph.ValidateCopySubtree(resource, source);
        var managedSchemas = new Dictionary<uint, byte[]>();
        foreach (uint type in schemas.Keys)
        {
            if (_schemaCopies.GetValueOrDefault((resource, type)) <= 0
                || !Plan(resource).AdditionalSchemas.ContainsKey(type))
                throw new NotSupportedException("The copied scene requires a native schema without an active owner.");
        }
        // The source may traverse schemas in a different order from the import that introduced
        // them. Retain their existing declaration order so redo reproduces the same class table.
        foreach (var (type, bytes) in Plan(resource).AdditionalSchemas)
            if (schemas.ContainsKey(type)) managedSchemas.Add(type, bytes);
        var tokens = CurrentDescendants(source).Where(node => node.Event == 4 && node.DataSize == 8 && Graph.Kind(node) == 442
                && BinaryPrimitives.ReadUInt32LittleEndian(Graph.Data(node)[4..]) == node.TypeId)
            .Select(node => CompiledDependencyToken.Key(Graph, node)).Where(key => key != 0).ToHashSet();
        var managedTokens = tokens.Where(key => _vehicleExplicitEntries.ContainsKey((resource, key))).ToHashSet();
        if (managedSchemas.Count == 0 && managedTokens.Count == 0) return null;
        IEditAction? dependencies = null;
        try
        {
            if (managedTokens.Count > 0) dependencies = CarryExplicitDependencies(resource, [resource], managedTokens);
            var result = new CopiedDefinitionsEdit(this, resource, schemaStream, managedSchemas, dependencies);
            result.Redo(); return result;
        }
        catch { dependencies?.Undo(); throw; }
    }

    private sealed class CopiedDefinitionsEdit(CompiledSceneDocument document, CompiledResource resource, int stream,
        IReadOnlyDictionary<uint, byte[]> schemas, IEditAction? dependencies) : IEditAction
    {
        private bool _applied;
        public void Redo()
        {
            if (_applied) return;
            foreach (var (type, bytes) in schemas)
            {
                document._schemaCopies[(resource, type)] = document._schemaCopies.GetValueOrDefault((resource, type)) + 1;
                document.Plan(resource).SchemaStreamIndex = stream; document.Plan(resource).AdditionalSchemas[type] = bytes;
            }
            dependencies?.Redo(); _applied = true;
        }
        public void Undo()
        {
            if (!_applied) return;
            dependencies?.Undo();
            foreach (uint type in schemas.Keys)
                if (--document._schemaCopies[(resource, type)] == 0) document.Plan(resource).AdditionalSchemas.Remove(type);
            _applied = false;
        }
    }

    private sealed class DependentDuplication(CompiledSceneDocument document, CompiledResource resource,
        IFrameDuplication copy, IEditAction definitions) : IFrameDuplication, ICompiledDuplicationMap
    {
        private bool _attached = true;
        private uint[]? _schemas, _allocations;
        private int _stream;
        public ISceneHierarchyNode Root => copy.Root;
        public IReadOnlyDictionary<ulong, ulong> SceneIds => ((ICompiledDuplicationMap)copy).SceneIds;
        public void ValidateDetach() => copy.ValidateDetach();
        public void ValidateReattach() => copy.ValidateReattach();
        public void Detach()
        {
            if (!_attached) return;
            ValidateDetach();
            var plan = document.Plan(resource);
            _schemas = plan.AdditionalSchemas.Keys.ToArray(); _allocations = plan.AllocationCountDeltas.Keys.ToArray();
            _stream = plan.SchemaStreamIndex;
            copy.Detach(); definitions.Undo(); _attached = false;
        }
        public void Reattach()
        {
            if (_attached) return;
            ValidateReattach();
            definitions.Redo(); copy.Reattach();
            var plan = document.Plan(resource);
            // Dictionary reuses removed slots in reverse order. Preserve the actual pre-undo
            // schema/class-table order rather than depending on reinsertion enumeration order.
            RestoreOrder(plan.AdditionalSchemas, _schemas); RestoreOrder(plan.AllocationCountDeltas, _allocations);
            plan.SchemaStreamIndex = _stream; document._effectivePlans = null; _attached = true;
        }
        private static void RestoreOrder<T>(Dictionary<uint, T> values, uint[]? order)
        {
            if (order is null) return;
            var ranks = order.Select((type, index) => (type, index)).ToDictionary(pair => pair.type, pair => pair.index);
            var entries = values.OrderBy(pair => ranks.GetValueOrDefault(pair.Key, int.MaxValue)).ToArray();
            values.Clear(); foreach (var pair in entries) values.Add(pair.Key, pair.Value);
        }
    }
}
