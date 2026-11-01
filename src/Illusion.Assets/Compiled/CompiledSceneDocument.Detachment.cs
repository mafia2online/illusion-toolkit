using System.Buffers.Binary;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument : IFrameDetachmentProvider
{
    private readonly HashSet<CompiledFrameNode> _deleted = [];
    private Dictionary<CompiledResource, CompiledRewrite>? _effectivePlans;
    internal int DetachedObjectCount => _deleted.Count;

    public IFrameDetachment? CaptureDetachment(IReadOnlyList<IFrameNode> frames)
    {
        var set = frames.OfType<CompiledFrameNode>().ToHashSet();
        if (set.Any(frame => ValidateSceneCollectionMutation(frame) is not null)) return null;
        if (set.Count == 0 || set.Count != frames.Distinct().Count() || set.Any(f => !_frames.ContainsValue(f) || _deleted.Contains(f))) return null;
        // Every descendant must go with its parent: keeping a child would leave its placement undefined.
        if (set.Any(f => f.Children.Any(child => !set.Contains(child)))) return null;
        try
        {
            var plans = BuildDeletionPlans(_deleted.Concat(set).ToHashSet());
            foreach (var (resource, plan) in plans) resource.Rewrite(plan);
        }
        catch (NotSupportedException) { return null; }
        catch (InvalidDataException) { return null; }
        return new CompiledDetachment(this, set);
    }

    private Dictionary<CompiledResource, CompiledRewrite> EffectivePlans() =>
        _deleted.Count == 0 ? _structure : _effectivePlans ??= BuildDeletionPlans(_deleted);

    private Dictionary<CompiledResource, CompiledRewrite> BuildDeletionPlans(HashSet<CompiledFrameNode> frames)
    {
        var result = new Dictionary<CompiledResource, CompiledRewrite>();
        foreach (var (resource, source) in _structure)
        {
            var copy = new CompiledRewrite { AllocationBudget = source.AllocationBudget, AllowNativeAllocatorFallback = source.AllowNativeAllocatorFallback, SchemaStreamIndex = source.SchemaStreamIndex };
            foreach (var pair in source.AdditionalSchemas) copy.AdditionalSchemas.Add(pair.Key, pair.Value);
            foreach (var pair in source.AllocationCountDeltas) copy.AllocationCountDeltas.Add(pair.Key, pair.Value);
            foreach (var pair in source.Children) copy.Children.Add(pair.Key, pair.Value);
            foreach (var pair in source.Values) copy.Values.Add(pair.Key, pair.Value);
            result.Add(resource, copy);
        }
        CompiledRewrite For(CompiledElement element)
        {
            var owner = Graph.Owner(element);
            if (!result.TryGetValue(owner, out var plan)) result.Add(owner, plan = new());
            return plan;
        }
        IReadOnlyList<CompiledElement> Items(CompiledElement container) =>
            For(container).Children.TryGetValue(container, out var children)
                ? children.Where(e => e.Event is 2 or 4).Skip(1).ToArray() : Graph.Items(container);
        var sceneElements = frames.Select(f => f.Element).ToHashSet();
        var definitions = new HashSet<CompiledElement>();
        var found = new HashSet<CompiledElement>();
        var roots = Graph.Nodes.SelectMany(n => new[] { Graph.Field(n, "m_Objects"), Graph.Field(n, "m_Children") }).OfType<CompiledElement>().Distinct();
        foreach (var container in roots)
        {
            var items = Items(container);
            CompiledElement? Target(CompiledElement item) => Graph.Resolve(Graph.Field(Graph.Field(item, "Second") ?? item, "m_Object") ?? item);
            var removed = items.Where(e => Target(e) is { } resolved && sceneElements.Contains(resolved)).ToArray();
            foreach (var entry in removed)
            {
                found.Add(Target(entry)!);
                Collect(entry, definitions);
            }
            if (removed.Length > 0) SetItems(container, items.Except(removed).ToArray(), For(container));
        }
        found.UnionWith(sceneElements.Where(definitions.Contains));
        if (!found.SetEquals(sceneElements)) throw new NotSupportedException("Object has no removable scene registration.");
        var ids = definitions.Where(e => e.Event == 2 && e.ObjectId != 0).Select(e => e.ObjectId).ToHashSet();
        var localIds = new Dictionary<CompiledResource, HashSet<ulong>>();
        foreach (var pointer in definitions.Where(e => e.Event == 2 && Graph.Kind(e) == 173))
            if (pointer.Values.FirstOrDefault() is { Event: 4, DataSize: 8 } value && Graph.Integer(value) != 0)
            {
                var owner = Graph.Owner(pointer);
                if (!localIds.TryGetValue(owner, out var set)) localIds.Add(owner, set = []);
                set.Add(Graph.Integer(value));
            }
        bool RemovedId(CompiledElement value) => ids.Contains(Graph.Integer(value)) ||
            localIds.TryGetValue(Graph.Owner(value), out var set) && set.Contains(Graph.Integer(value));
        var frameIds = sceneElements.Select(e => e.ObjectId).ToHashSet();
        var tocValues = new HashSet<CompiledElement>();
        var tocContainers = Graph.Nodes.Select(n => Graph.Field(n, "m_ToC")).OfType<CompiledElement>().ToHashSet();
        foreach (var toc in tocContainers) Collect(toc, tocValues);
        var active = new HashSet<CompiledElement>();
        foreach (var resource in Graph.Resources)
            foreach (var root in resource.Roots) Collect(root, active);
        foreach (var resource in Graph.Resources)
            foreach (var value in resource.Elements.Where(e => active.Contains(e) && e.Event == 4 && e.DataSize is 8 or 12 && !definitions.Contains(e)))
            {
                ulong id = Graph.Integer(value);
                if (!RemovedId(value)) continue;
                // A shared component/visual definition cannot disappear with one placement.
                // ToC entries are registrations, not owners, and are removed below.
                if (!frameIds.Contains(id) && !tocValues.Contains(value))
                    throw new NotSupportedException("A removed object's inline definition is shared outside the subtree.");
                byte[] cleared = Graph.Data(value).ToArray();
                BinaryPrimitives.WriteUInt64LittleEndian(cleared, 0);
                For(value).Values[value] = cleared;
            }
        foreach (var container in Graph.Nodes.Where(n => Graph.Kind(n) == 960 && !definitions.Contains(n)))
        {
            var items = Items(container);
            var keep = items.Where(e =>
                !(Graph.Resolve(e) is { } target && definitions.Contains(target)) &&
                !(e.Event == 4 && e.DataSize is 8 or 12 && RemovedId(e))).ToArray();
            if (keep.Length != items.Count)
            {
                if (tocContainers.Contains(container)) SetGuidTableItems(container, keep, For(container));
                else SetItems(container, keep, For(container));
            }
        }
        return result.Where(p => p.Value.Children.Count != 0 || p.Value.Values.Count != 0).ToDictionary();

        void Collect(CompiledElement element, HashSet<CompiledElement> into)
        {
            if (element.Event == 0) return; // schema declarations survive the rewrite
            if (!into.Add(element)) return;
            var children = For(element).Children.TryGetValue(element, out var staged) ? staged : element.Children;
            foreach (var child in children) Collect(child, into);
        }
    }

    private sealed class CompiledDetachment(CompiledSceneDocument document, HashSet<CompiledFrameNode> frames) : IFrameDetachment
    {
        private readonly List<(CompiledFrameNode Parent, CompiledFrameNode Child, int Index)> _boundary = [];
        public void Detach()
        {
            _boundary.Clear();
            foreach (var frame in frames)
                if (frame.ParentFrame is { } parent && !frames.Contains(parent))
                {
                    _boundary.Add((parent, frame, parent.Children.IndexOf(frame)));
                    parent.Children.Remove(frame);
                }
            document._deleted.UnionWith(frames);
            document._effectivePlans = null;
        }
        public void Reattach()
        {
            document._deleted.ExceptWith(frames);
            document._effectivePlans = null;
            foreach (var (parent, child, index) in _boundary.OrderBy(e => e.Index))
                parent.Children.Insert(Math.Clamp(index, 0, parent.Children.Count), child);
            _boundary.Clear();
        }
    }
}
