using System.Buffers.Binary;
using System.Numerics;
using System.Text;

using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument : IFrameGroupProvider
{
    public IFrameDuplication? CreateGroup(string name, IFrameNode? parent, out string? refusal)
    {
        refusal = null;
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0')) { refusal = "A group needs a nonempty name without null characters."; return null; }
        if (parent is not null && (parent is not CompiledFrameNode p || !_frames.ContainsValue(p) || _deleted.Contains(p) || Hierarchy(p) is null))
        { refusal = "The parent must be an existing Kit in this document."; return null; }
        var target = parent as CompiledFrameNode;
        IEnumerable<CompiledFrameNode> VisibleFrames(IEnumerable<Illusion.Assets.Sds.SdsFrameNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Source is CompiledFrameNode frame) yield return frame;
                foreach (var child in VisibleFrames(node.Children)) yield return child;
            }
        }
        var template = VisibleFrames(Roots).Distinct().FirstOrDefault(f => !_deleted.Contains(f) && Hierarchy(f) is not null &&
            (target is null || ReferenceEquals(Graph.Owner(f.Element), Graph.Owner(target.Element))));
        if (template is null) { refusal = "This scene has no compatible Kit template."; return null; }
        var resource = Graph.Owner(template.Element);
        var container = target is null ? Graph.Nodes.Select(n => Graph.Field(n, "m_Objects")).OfType<CompiledElement>()
            .FirstOrDefault(c => ReferenceEquals(Graph.Owner(c), resource) && CurrentItems(c).Any(i => Graph.Resolve(i)?.TypeId == 0x4403597e)) : Hierarchy(target);
        CompiledElement? prototype = target is null ? container is null ? null : CurrentItems(container).FirstOrDefault(i => Graph.Resolve(i)?.TypeId == 0x4403597e) :
            Graph.Nodes.Where(n => n.TypeId == container!.TypeId && ReferenceEquals(Graph.Owner(n), resource)).SelectMany(CurrentItems).FirstOrDefault();
        var toc = Graph.Nodes.Select(n => Graph.Field(n, "m_ToC")).OfType<CompiledElement>().FirstOrDefault(c => ReferenceEquals(Graph.Owner(c), resource));
        var tocValue = toc is null ? null : CurrentItems(toc).FirstOrDefault(v => v.Event == 4 && v.DataSize == 8);
        if (container is null || prototype is null || tocValue is null) { refusal = "The destination lacks an owning registration or GUID-table template."; return null; }
        using var transaction = new ImportTransaction(this, target);
        try
        {
            var used = resource.Elements.Where(e => e.Event == 2).Select(e => e.ObjectId).ToHashSet();
            foreach (var pointer in resource.Elements.Where(e => e.Event == 2 && Graph.Kind(e) == 173))
                if (pointer.Values.FirstOrDefault() is { DataSize: 8 } value) used.Add(Graph.Integer(value));
            ulong Fresh(bool local)
            {
                ulong value;
                do { value = BinaryPrimitives.ReadUInt64LittleEndian(Guid.NewGuid().ToByteArray()) & 0x7fffffffffffffff; if (local) value |= 0x8000000000000000; }
                while (value == 0 || Graph.Objects.ContainsKey(value) || !used.Add(value));
                return value;
            }
            var ids = new Dictionary<ulong, ulong>();
            var originals = CurrentDescendants(template.Element).ToArray();
            foreach (var node in originals.Where(n => n.Event == 2 && n.ObjectId != 0)) ids.TryAdd(node.ObjectId, Fresh(false));
            foreach (var pointer in originals.Where(n => n.Event == 2 && Graph.Kind(n) == 173 && n.Values.Count() > 1))
                if (pointer.Values.First() is { DataSize: 8 } value && Graph.Integer(value) != 0) ids.TryAdd(Graph.Integer(value), Fresh(true));
            var element = resource.CloneSubtree(template.Element, ids, EffectivePlans().GetValueOrDefault(resource));
            Graph.RegisterSubtree(resource, element);
            var components = Graph.Field(element, "m_Components")!;
            var kitEntry = Graph.Items(components).Single(e => Graph.Resolve(e)?.TypeId == 0x360b668d);
            void Keep(CompiledElement list, IReadOnlyList<CompiledElement> items)
            {
                var count = list.Values.First();
                list.Children.Clear();
                list.Children.Add(count);
                list.Children.AddRange(items);
                byte[] bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)items.Count));
                resource.SetClonedValue(count, bytes);
            }
            var children = Graph.Field(Graph.Resolve(kitEntry), "m_Children")!;
            // Deactivate before pruning so discarded inline definitions cannot stay in the resolver.
            Graph.SetSubtreeActive(resource, element, false);
            Keep(components, [kitEntry]);
            Keep(children, []);
            if (Graph.Field(element, "m_ComponentsMarkedForDelete") is { } removed) Keep(removed, []);
            Graph.RegisterSubtree(resource, element);
            resource.SetClonedValue(Graph.Field(element, "m_Name")!, Encoding.UTF8.GetBytes(name + '\0'));
            if (Graph.Field(element, "m_HashName") is { DataSize: 8 } hash)
            {
                byte[] bytes = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, Fnv64.Hash(name, Encoding.UTF8));
                resource.SetClonedValue(hash, bytes);
            }
            byte[] pointerId = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(pointerId, Fresh(true));
            var entry = OwnershipShell(resource, prototype, element, pointerId);
            CompiledElement? transform = null;
            if (target is not null)
            {
                transform = Graph.Field(Graph.Field(entry, "Second"), "m_LocalTransform")!;
                var key = Graph.Field(entry, "First")!;
                var keys = CurrentItems(container).Select(e => Graph.Integer(Graph.Field(e, "First")!)).ToHashSet();
                uint fresh;
                do { fresh = checked((uint)Random.Shared.Next(1024, int.MaxValue)); } while (keys.Contains(fresh));
                byte[] bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, fresh);
                resource.SetClonedValue(key, bytes);
                if (Graph.Field(Graph.Field(entry, "Second"), "m_BoneName") is { } bone) resource.SetClonedValue(bone, new byte[bone.DataSize]);
            }
            var frame = new CompiledFrameNode(this, element) { ParentFrame = target, LinkTransform = transform };
            frame.LocalTransform = Matrix4x4.Identity;
            _frames.Add(element, frame);
            var tables = Descendants(entry).Where(n => n.Event == 2 && n.ObjectId != 0).Select(n => n.ObjectId).Distinct().Select(id =>
            {
                byte[] bytes = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, id);
                var value = resource.CreateValue(tocValue.TypeId, bytes);
                Graph.RegisterSubtree(resource, value);
                return value;
            }).ToList();
            var allocated = Descendants(entry).Where(n => n.Event == 2 && n.ObjectId != 0).ToHashSet();
            foreach (var pointer in Descendants(entry).Where(n => n.Event == 2 && Graph.Kind(n) == 173))
                if (pointer.Values.ToArray() is { Length: > 1 } values && values[1].Event == 2) allocated.Add(values[1]);
            var counts = allocated.GroupBy(n => n.TypeId).ToDictionary(g => g.Key, g => g.Count());
            int firstMesh = Meshes.Count;
            var root = Build(frame, new CompiledMeshDecoder(Graph, Illusion.Assets.Library.StageCompanions.For(SourceArchive)), [], 0);
            var result = new CompiledDuplication(this, frame, root, resource, container, entry, toc!, tables, counts, Meshes.Skip(firstMesh).ToArray(), []);
            result.Reattach();
            transaction.Commit();
            return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException)
        { refusal = ex.Message; return null; }
    }
}
