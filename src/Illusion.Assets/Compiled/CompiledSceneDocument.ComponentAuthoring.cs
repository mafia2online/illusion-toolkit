using System.Buffers.Binary;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Attaches a fresh empty native collision component. The returned edit is applied;
    /// callers may compose it with shape authoring in the same undo action.</summary>
    public (CompiledElement Component, IEditAction Edit)? AddCollisionComponent(CompiledFrameNode frame, out string? refusal)
    {
        refusal = null;
        if (!_frames.ContainsValue(frame) || _deleted.Contains(frame)) { refusal = "The target frame is not active."; return null; }
        if (Components(frame.Element).Any(c => c.TypeId == 0x2b7b4775)) { refusal = "The frame already has a collision component."; return null; }
        var template = Graph.Nodes.FirstOrDefault(n => n.TypeId == 0x2b7b4775);
        if (template is null) { refusal = "No collision-component template is loaded."; return null; }
        var resource = Graph.Owner(frame.Element);
        var container = Graph.Field(frame.Element, "m_Components");
        var prototype = container is null ? null : Graph.Nodes.Where(n => n.TypeId == container.TypeId && ReferenceEquals(Graph.Owner(n), resource))
            .SelectMany(CurrentItems).FirstOrDefault(i => Graph.Resolve(i) is not null);
        var toc = Graph.Nodes.Select(n => Graph.Field(n, "m_ToC")).OfType<CompiledElement>().FirstOrDefault(n => ReferenceEquals(Graph.Owner(n), resource));
        var tocValue = toc is null ? null : CurrentItems(toc).FirstOrDefault(v => v.Event == 4 && v.DataSize == 8);
        if (container is null || prototype is null || tocValue is null) { refusal = "The target lacks compatible component/GUID registration templates."; return null; }
        using var transaction = new ImportTransaction(this, null);
        try
        {
            var used = resource.Elements.Where(e => e.Event == 2).Select(e => e.ObjectId).ToHashSet();
            foreach (var pointer in resource.Elements.Where(n => n.Event == 2 && Graph.Kind(n) == 173))
                if (pointer.Values.FirstOrDefault() is { DataSize: 8 } value) used.Add(Graph.Integer(value));
            ulong Fresh(bool local)
            {
                ulong value;
                do { value = BinaryPrimitives.ReadUInt64LittleEndian(Guid.NewGuid().ToByteArray()) & 0x7fffffffffffffff; if (local) value |= 0x8000000000000000; }
                while (value == 0 || Graph.Objects.ContainsKey(value) || !used.Add(value));
                return value;
            }
            var ids = new Dictionary<ulong, ulong>();
            foreach (var node in Descendants(template).Where(n => n.Event == 2 && n.ObjectId != 0)) ids.TryAdd(node.ObjectId, Fresh(false));
            foreach (var pointer in Descendants(template).Where(n => n.Event == 2 && Graph.Kind(n) == 173 && n.Values.Count() > 1))
                if (pointer.Values.First() is { DataSize: 8 } value && Graph.Integer(value) != 0) ids.TryAdd(Graph.Integer(value), Fresh(true));
            var component = resource.CopySubtree(Graph.Owner(template), template, ids);
            Graph.RegisterSubtree(resource, component);
            Graph.SetSubtreeActive(resource, component, false);
            foreach (string field in new[] { "m_ElementDefs", "m_CustomItems", "m_CustomMaterials", "m_CustomItemDescs", "m_BoneCollisionHelperMap", "m_DataDependencies" })
            {
                var list = Graph.Field(component, field) ?? throw new NotSupportedException("Missing collision field " + field);
                if (Graph.Kind(list) == 960)
                {
                    var count = list.Values.First();
                    list.Children.Clear();
                    list.Children.Add(count);
                    resource.SetClonedValue(count, new byte[4]);
                }
                else if (list.Event == 4 && list.DataSize >= 4)
                    resource.SetClonedValue(list, new byte[4]);
                else throw new NotSupportedException("Unknown collision container layout: " + field);
            }
            Graph.RegisterSubtree(resource, component);
            var schemas = Graph.ValidateCopySubtree(resource, component);
            byte[] pointerId = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(pointerId, Fresh(true));
            var entry = OwnershipShell(resource, prototype, component, pointerId);
            var tables = Descendants(entry).Where(n => n.Event == 2 && n.ObjectId != 0).Select(n => n.ObjectId).Distinct().Select(id =>
            {
                byte[] bytes = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, id);
                var value = resource.CreateValue(tocValue.TypeId, bytes);
                Graph.RegisterSubtree(resource, value);
                return value;
            }).ToArray();
            var allocated = Descendants(entry).Where(n => n.Event == 2 && n.ObjectId != 0).ToHashSet();
            foreach (var pointer in Descendants(entry).Where(n => n.Event == 2 && Graph.Kind(n) == 173))
                if (pointer.Values.ToArray() is { Length: > 1 } values && values[1].Event == 2) allocated.Add(values[1]);
            var counts = allocated.GroupBy(n => n.TypeId).ToDictionary(g => g.Key, g => g.Count());
            int stream = resource.StreamIndex(resource.Roots.Last(r => r.Event == 2));
            var edit = new ComponentAttachment(this, resource, container, entry, toc!, tables, counts, stream, schemas);
            edit.Redo();
            transaction.Commit();
            return (component, edit);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException)
        { refusal = ex.Message; return null; }
    }

    private sealed class ComponentAttachment(CompiledSceneDocument document, CompiledResource resource,
        CompiledElement container, CompiledElement entry, CompiledElement toc, CompiledElement[] tables,
        Dictionary<uint, int> counts, int schemaStream, IReadOnlyDictionary<uint, byte[]> schemas) : IEditAction
    {
        private bool _applied;
        public void Redo()
        {
            if (_applied) return;
            document.Graph.SetSubtreeActive(resource, entry, true);
            document.SetItems(container, document.CurrentItems(container).Append(entry).ToArray());
            document.SetItems(toc, document.CurrentItems(toc).Concat(tables).OrderBy(document.Graph.Integer).ToArray(), restoreOrder: false);
            var plan = document.Plan(resource);
            plan.AllowNativeAllocatorFallback = true;
            foreach (var (type, count) in counts) plan.AllocationCountDeltas[type] = plan.AllocationCountDeltas.GetValueOrDefault(type) + count;
            foreach (var (type, bytes) in schemas)
            {
                document._schemaCopies[(resource, type)] = document._schemaCopies.GetValueOrDefault((resource, type)) + 1;
                plan.SchemaStreamIndex = schemaStream;
                plan.AdditionalSchemas[type] = bytes;
            }
            _applied = true;
        }
        public void Undo()
        {
            if (!_applied) return;
            document.SetItems(container, document.CurrentItems(container).Where(i => !ReferenceEquals(i, entry)).ToArray());
            document.SetItems(toc, document.CurrentItems(toc).Except(tables).ToArray());
            var plan = document.Plan(resource);
            foreach (var (type, count) in counts)
            {
                int remaining = plan.AllocationCountDeltas[type] - count;
                if (remaining == 0) plan.AllocationCountDeltas.Remove(type); else plan.AllocationCountDeltas[type] = remaining;
            }
            foreach (uint type in schemas.Keys)
                if (--document._schemaCopies[(resource, type)] == 0) plan.AdditionalSchemas.Remove(type);
            document.Graph.SetSubtreeActive(resource, entry, false);
            _applied = false;
        }
    }
}
