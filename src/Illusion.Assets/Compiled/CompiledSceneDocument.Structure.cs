using System.Buffers.Binary;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private readonly Dictionary<CompiledResource, CompiledRewrite> _structure = [];
    private readonly HashSet<CompiledResource> _structurallyWritten = [];
    private readonly Dictionary<CompiledFrameNode, (CompiledElement Entry, CompiledElement Transform, CompiledElement Container, uint Key)> _links = [];
    private readonly Dictionary<CompiledFrameNode, (CompiledElement Container, CompiledElement Registration)> _rootTransfers = [];

    private CompiledRewrite Plan(CompiledResource resource)
    {
        if (!_structure.TryGetValue(resource, out var plan)) _structure.Add(resource, plan = new());
        return plan;
    }

    internal bool HasStructuralChanges(CompiledResource resource) =>
        resource.HasVariableChanges || _structurallyWritten.Contains(resource) ||
        EffectivePlans().TryGetValue(resource, out var plan) && (plan.Children.Count != 0 || plan.Values.Count != 0 || plan.AdditionalSchemas.Count != 0);

    internal bool SaveStructuralResource(CompiledResource resource)
    {
        if (!HasStructuralChanges(resource)) return false;
        var plan = EffectivePlans().GetValueOrDefault(resource) ?? new();
        if (resource.HasVariableChanges) plan.AllowNativeAllocatorFallback = true;
        resource.SaveSnapshot(plan);
        _structurallyWritten.Add(resource);
        return true;
    }

    private IReadOnlyList<CompiledElement> CurrentItems(CompiledElement container)
    {
        var resource = Graph.Owner(container);
        if (_structure.TryGetValue(resource, out var plan) && plan.Children.TryGetValue(container, out var children))
            return children.Where(e => e.Event is 2 or 4).Skip(1).ToArray();
        return Graph.Items(container);
    }

    private void SetItems(CompiledElement container, IReadOnlyList<CompiledElement> items, CompiledRewrite? target = null, bool restoreOrder = true)
    {
        var originalItems = container.Values.Skip(1).ToList();
        if (restoreOrder) items = items.OrderBy(item => originalItems.IndexOf(item) is int index && index >= 0 ? index : int.MaxValue).ToArray();
        var count = container.Values.First();
        if (count.DataSize != 4) throw new NotSupportedException("Unsupported compiled container count.");
        var plan = target ?? Plan(Graph.Owner(container));
        if (target is null) _effectivePlans = null;
        var children = container.Children.Where(e => e.Event is not (2 or 4) || ReferenceEquals(e, count)).ToList();
        children.AddRange(items);
        if (children.SequenceEqual(container.Children)) plan.Children.Remove(container);
        else plan.Children[container] = children;
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)items.Count));
        if (bytes.AsSpan().SequenceEqual(Graph.Data(count))) plan.Values.Remove(count);
        else plan.Values[count] = bytes;
    }

    private void SetGuidTableItems(CompiledElement container, IReadOnlyList<CompiledElement> items, CompiledRewrite? target = null)
    {
        // Sorting one new action must not reorder GUID entries owned by earlier, still-active actions.
        // Once all additions are undone, restore the exact stock order, even if that order was unsorted.
        var original = container.Values.Skip(1).ToHashSet();
        SetItems(container, items, target, restoreOrder: items.All(original.Contains));
    }

    private CompiledElement? Hierarchy(CompiledFrameNode frame) =>
        Components(frame.Element).Select(c => Graph.Field(c, "m_Children")).FirstOrDefault(c => c is not null);

    internal bool ReparentCompiled(IFrameNode childNode, ISceneSource? parentSource)
    {
        if (childNode is not CompiledFrameNode child || !_frames.ContainsValue(child)) return false;
        CompiledFrameNode? parent;
        if (parentSource is null || ReferenceEquals(parentSource, this)) parent = null;
        else if (parentSource is CompiledFrameNode frame && _frames.ContainsValue(frame)) parent = frame;
        else return false;
        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.ParentFrame)
            if (ReferenceEquals(ancestor, child)) return false;
        if (ReferenceEquals(child.ParentFrame, parent)) return true;
        if (ValidateSceneCollectionMutation(child) is not null || parent is not null && ValidateSceneCollectionMutation(parent) is not null) return false;

        var oldContainer = child.ParentFrame is { } oldParent ? Hierarchy(oldParent) : null;
        var newContainer = parent is not null ? Hierarchy(parent) : null;
        if (parent is not null && newContainer is null) return false;
        if (oldContainer is not null && newContainer is not null &&
            (!ReferenceEquals(Graph.Owner(oldContainer), Graph.Owner(newContainer)) || oldContainer.TypeId != newContainer.TypeId)) return false;
        if (!_links.TryGetValue(child, out var saved))
        {
            if (oldContainer is null)
            {
                if (newContainer is null || !TryHierarchyRegistration(child, newContainer, out saved)) return false;
                _links.Add(child, saved);
            }
            else
            {
                var entry = CurrentItems(oldContainer).FirstOrDefault(e =>
                    ReferenceEquals(Graph.Resolve(Graph.Field(Graph.Field(e, "Second") ?? e, "m_Object")), child.Element));
                var transform = entry is null ? null : Graph.Field(Graph.Field(entry, "Second") ?? entry, "m_LocalTransform");
                if (entry is null || transform is not { DataSize: 48 }) return false;
                var first = Graph.Field(entry, "First");
                if (first is not { DataSize: 4 }) return false;
                saved = (entry, transform, oldContainer, checked((uint)Graph.Integer(first)));
                _links.Add(child, saved);
            }
        }
        if (newContainer is not null && !ReferenceEquals(Graph.Owner(newContainer), Graph.Owner(saved.Entry))) return false;

        // C_KitComponent::AddObject uses a random unique integer key in [1024, INT_MAX].
        var key = Graph.Field(saved.Entry, "First");
        if (newContainer is null)
        {
            if (!TryRootRegistration(child, saved.Entry, out var transfer)) return false;
            var localToRoot = child.LocalTransform;
            if (oldContainer is not null) SetItems(oldContainer, CurrentItems(oldContainer).Where(e => !ReferenceEquals(e, saved.Entry)).ToArray());
            SetItems(transfer.Container, CurrentItems(transfer.Container).Append(transfer.Registration).ToArray());
            Plan(Graph.Owner(transfer.Container)).AllowNativeAllocatorFallback = true;
            child.ParentFrame?.Children.Remove(child);
            child.ParentFrame = null;
            child.LinkTransform = null;
            child.LocalTransform = localToRoot;
            return true;
        }
        uint desiredKey = ReferenceEquals(newContainer, saved.Container) ? saved.Key : checked((uint)Graph.Integer(key!));
        var usedKeys = CurrentItems(newContainer).Select(e => Graph.Field(e, "First")).OfType<CompiledElement>().Select(Graph.Integer).ToHashSet();
        while (usedKeys.Contains(desiredKey)) desiredKey = checked((uint)Random.Shared.Next(1024, int.MaxValue));
        byte[] keyBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(keyBytes, desiredKey);
        Graph.Owner(key!).Replace(key!, keyBytes);

        var local = child.LocalTransform;
        if (_rootTransfers.TryGetValue(child, out var rootTransfer))
            SetItems(rootTransfer.Container, CurrentItems(rootTransfer.Container).Where(e => !ReferenceEquals(e, rootTransfer.Registration)).ToArray());
        if (oldContainer is not null) SetItems(oldContainer, CurrentItems(oldContainer).Where(e => !ReferenceEquals(e, saved.Entry)).ToArray());
        if (newContainer is not null) SetItems(newContainer, CurrentItems(newContainer).Append(saved.Entry).ToArray());
        child.ParentFrame?.Children.Remove(child);
        child.ParentFrame = parent;
        parent?.Children.Add(child);
        child.LinkTransform = parent is not null ? saved.Transform : null;
        child.LocalTransform = local;
        return true;
    }

    private bool TryRootRegistration(CompiledFrameNode frame, CompiledElement entry,
        out (CompiledElement Container, CompiledElement Registration) transfer)
    {
        if (_rootTransfers.TryGetValue(frame, out transfer)) return true;
        var resource = Graph.Owner(frame.Element);
        var container = Graph.Nodes.Select(n => Graph.Field(n, "m_Objects")).OfType<CompiledElement>()
            .FirstOrDefault(c => ReferenceEquals(Graph.Owner(c), resource) && CurrentItems(c).Count > 0);
        if (container is null) return false;
        var prototype = CurrentItems(container).FirstOrDefault(e => Descendants(e).Any(p => Graph.Kind(p) == 173 && p.Values.Count() > 1));
        var pointer = Descendants(entry).FirstOrDefault(p => Graph.Kind(p) == 173 && p.Values.ToArray() is { Length: > 1 } values && ReferenceEquals(values[1], frame.Element));
        if (prototype is null || pointer is null) return false;
        byte[] id = Graph.Data(pointer.Values.First()).ToArray();
        var registration = OwnershipShell(resource, prototype, frame.Element, id);
        transfer = (container, registration);
        _rootTransfers.Add(frame, transfer);
        return true;
    }

    private bool TryHierarchyRegistration(CompiledFrameNode frame, CompiledElement target,
        out (CompiledElement Entry, CompiledElement Transform, CompiledElement Container, uint Key) link)
    {
        link = default;
        var resource = Graph.Owner(frame.Element);
        if (!ReferenceEquals(Graph.Owner(target), resource)) return false;
        var rootContainer = Graph.Nodes.Select(n => Graph.Field(n, "m_Objects")).OfType<CompiledElement>()
            .FirstOrDefault(c => ReferenceEquals(Graph.Owner(c), resource) && CurrentItems(c).Any(e => ReferenceEquals(Graph.Resolve(e), frame.Element)));
        if (rootContainer is null) return false;
        var rootRegistration = CurrentItems(rootContainer).First(e => ReferenceEquals(Graph.Resolve(e), frame.Element));
        var rootPointer = Descendants(rootRegistration).FirstOrDefault(p => Graph.Kind(p) == 173 &&
            p.Values.ToArray() is { Length: > 1 } values && ReferenceEquals(values[1], frame.Element));
        var prototype = Graph.Nodes.Where(n => n.TypeId == target.TypeId && ReferenceEquals(Graph.Owner(n), resource))
            .SelectMany(CurrentItems).FirstOrDefault(e => Graph.Field(e, "First") is { DataSize: 4 });
        if (rootPointer is null || prototype is null) return false;
        var entry = OwnershipShell(resource, prototype, frame.Element, Graph.Data(rootPointer.Values.First()).ToArray());
        var transform = Graph.Field(Graph.Field(entry, "Second") ?? entry, "m_LocalTransform");
        var key = Graph.Field(entry, "First");
        if (transform is not { DataSize: 48 } || key is not { DataSize: 4 }) return false;
        resource.Replace(transform, WriteMatrix(frame.LocalTransform));
        if (Graph.Field(Graph.Field(entry, "Second"), "m_BoneName") is { } bone)
            resource.Replace(bone, new byte[bone.DataSize]);
        uint fresh = checked((uint)Random.Shared.Next(1024, int.MaxValue));
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, fresh);
        resource.Replace(key, bytes);
        _rootTransfers[frame] = (rootContainer, rootRegistration);
        Plan(resource).AllowNativeAllocatorFallback = true;
        link = (entry, transform, target, fresh);
        return true;
    }

    private CompiledElement OwnershipShell(CompiledResource resource, CompiledElement prototype, CompiledElement target, byte[] id)
    {
        CompiledElement Shell(CompiledElement source)
        {
            var clone = resource.CloneElement(source);
            var values = source.Values.ToArray();
            foreach (var child in source.Children.Where(c => c.Event != 0))
                clone.Children.Add(Graph.Kind(source) == 173 && values.Length > 1 && ReferenceEquals(child, values[1]) ? target : Shell(child));
            if (Graph.Kind(source) == 173 && values.Length > 1) resource.SetClonedValue(clone.Values.First(), id);
            return clone;
        }
        var registration = Shell(prototype);
        Graph.RegisterSubtree(resource, registration);
        return registration;
    }
}
