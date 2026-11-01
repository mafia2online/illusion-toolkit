using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Creates an identity, owning Kit in the selected prepared fine object's resource.
    /// Existing prepared objects can then be parented into it without cross-resource ownership.</summary>
    public IFrameDuplication? CreatePreparedLinkedProxyGroup(IFrameNode source, string name, out string? refusal)
    {
        refusal = null;
        var canonical = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
        if (canonical is not CompiledFrameNode frame || !_linkedProxyStates.Values.Any(s => s.Pairs.ContainsKey(frame))
            || string.IsNullOrWhiteSpace(name) || name.Contains('\0'))
        { refusal = "Choose a prepared fine object and a nonempty group name."; return null; }
        if ((refusal = ValidateLinkedProxyMaterialSlots(frame)) is not null) return null;
        var template = Graph.NodesOfType(0x360b668d).FirstOrDefault(n => Graph.Fields(n).Keys.SequenceEqual(new[] { "m_Children" }));
        if (template is null) { refusal = "No verified native Kit template is loaded."; return null; }
        var resource = Graph.Owner(frame.Element);
        var container = Graph.Nodes.Where(n => ReferenceEquals(Graph.Owner(n), resource)).Select(n => Graph.Field(n, "m_Objects"))
            .OfType<CompiledElement>().FirstOrDefault(c => CurrentItems(c).Any(e => ReferenceEquals(Graph.Resolve(e), frame.Element)));
        var prototype = container is null ? null : CurrentItems(container).FirstOrDefault(e => ReferenceEquals(Graph.Resolve(e), frame.Element));
        var components = Graph.Field(frame.Element, "m_Components");
        var componentPrototype = components is null ? null : CurrentItems(components).FirstOrDefault(e => Graph.Resolve(e) is not null);
        var toc = Graph.Nodes.Where(n => ReferenceEquals(Graph.Owner(n), resource)).Select(n => Graph.Field(n, "m_ToC")).OfType<CompiledElement>().FirstOrDefault();
        var tocValue = toc is null ? null : CurrentItems(toc).FirstOrDefault(e => e is { Event: 4, DataSize: 8 });
        if (prototype is null || componentPrototype is null || tocValue is null) { refusal = "The selected resource lacks owning registration templates."; return null; }
        using var transaction = new ImportTransaction(this, null);
        IEditAction? schemaEdit = null; CompiledDuplication? duplicate = null;
        try
        {
            var used = new HashSet<ulong>(Graph.Objects.Keys);
            ulong Fresh(bool local)
            {
                ulong value;
                do { value = BinaryPrimitives.ReadUInt64LittleEndian(Guid.NewGuid().ToByteArray()) & 0x7fffffffffffffff; if (local) value |= 0x8000000000000000; }
                while (value == 0 || !used.Add(value) || resource.Elements.Any(e => e.Event == 4 && e.DataSize == 8 && Graph.Integer(e) == value));
                return value;
            }
            var prune = new CompiledRewrite();
            void Empty(CompiledElement list)
            {
                var count = list.Values.First();
                prune.Children[list] = list.Children.Where(n => n.Event is not (2 or 4) || ReferenceEquals(n, count)).ToArray();
                prune.Values[count] = new byte[4];
            }
            Empty(components!);
            if (Graph.Field(frame.Element, "m_ComponentsMarkedForDelete") is { } deleted) Empty(deleted);
            var element = resource.CloneSubtree(frame.Element, new Dictionary<ulong, ulong> { [frame.Element.ObjectId] = Fresh(false) }, prune);
            Graph.RegisterSubtree(resource, element);
            var kitPlan = new CompiledRewrite(); var childList = Graph.Field(template, "m_Children")!; var childCount = childList.Values.First();
            kitPlan.Children[childList] = childList.Children.Where(n => n.Event is not (2 or 4) || ReferenceEquals(n, childCount)).ToArray(); kitPlan.Values[childCount] = new byte[4];
            var kitIds = template.ObjectId == 0 ? new Dictionary<ulong, ulong>() : new Dictionary<ulong, ulong> { [template.ObjectId] = Fresh(false) };
            var kit = resource.CopySubtree(Graph.Owner(template), template, kitIds, kitPlan);
            Graph.RegisterSubtree(resource, kit);
            var entry = OwnershipShell(resource, componentPrototype, kit, BitConverter.GetBytes(Fresh(true)));
            var clonedComponents = Graph.Field(element, "m_Components")!;
            clonedComponents.Children.Add(entry); resource.SetClonedValue(clonedComponents.Values.First(), BitConverter.GetBytes(1u));
            resource.SetClonedValue(Graph.Field(element, "m_Name")!, Encoding.UTF8.GetBytes(name + '\0'));
            resource.SetClonedValue(Graph.Field(element, "m_HashName")!, BitConverter.GetBytes(Fnv64.Hash(name, Encoding.UTF8)));
            resource.SetClonedValue(Graph.Field(element, "m_Transform")!, WriteMatrix(Matrix4x4.Identity));
            var registration = OwnershipShell(resource, prototype, element, BitConverter.GetBytes(Fresh(true)));
            var schemas = Graph.ValidateCopySubtree(resource, registration).ToDictionary();
            // An empty Kit still needs its native map-entry schemas for the first reparent.
            foreach (var schema in Graph.ValidateCopySubtree(resource, template)) schemas.TryAdd(schema.Key, schema.Value);
            var definitions = Descendants(registration).Where(n => n.Event == 2 && n.ObjectId != 0).ToHashSet();
            foreach (var pointer in Descendants(registration).Where(n => n.Event == 2 && Graph.Kind(n) == 173 && n.Values.Count() > 1)) definitions.Add(pointer.Values.Last());
            var counts = definitions.GroupBy(n => n.TypeId).ToDictionary(g => g.Key, g => g.Count());
            var tables = definitions.Where(n => n.ObjectId != 0).Select(n => resource.CreateValue(tocValue.TypeId, BitConverter.GetBytes(n.ObjectId))).ToList();
            foreach (var value in tables) Graph.RegisterSubtree(resource, value);
            var group = new CompiledFrameNode(this, element); _frames.Add(element, group);
            var row = Build(group, new CompiledMeshDecoder(Graph), [], 0);
            duplicate = new CompiledDuplication(this, group, row, resource, container!, registration, toc!, tables, counts, [], []);
            int stream = resource.StreamIndex(resource.Roots.Last(n => n.Event == 2));
            schemaEdit = new CopiedDefinitionsEdit(this, resource, stream, schemas, null); schemaEdit.Redo();
            duplicate.Reattach(); transaction.Commit(); return new DependentDuplication(this, resource, duplicate, schemaEdit);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException or IOException or OverflowException)
        { duplicate?.Detach(); schemaEdit?.Undo(); refusal = ex.Message; return null; }
    }
}
