using System.Buffers.Binary;
using System.Text;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument : IFrameDuplicationProvider
{
    public bool CanDuplicate(IFrameNode source) => source is CompiledFrameNode frame &&
        _frames.ContainsValue(frame) && SafeHierarchy(frame, []);

    private bool SafeHierarchy(CompiledFrameNode frame, HashSet<CompiledFrameNode> visited, HashSet<CompiledElement>? actorScope = null)
    {
        if (!visited.Add(frame) || _deleted.Contains(frame)) return false;
        if (CompiledVehiclePlacementSupport.Supported(Graph, frame.Element))
        {
            var owned = CurrentDescendants(frame.Element).Where(n => n.TypeId == 0x4403597e).ToHashSet();
            bool Children(CompiledFrameNode parent) => parent.Children.All(child => owned.Contains(child.Element)
                && ReferenceEquals(Graph.Owner(child.Element), Graph.Owner(frame.Element)) && visited.Add(child)
                && !_deleted.Contains(child) && Children(child));
            return Children(frame) && visited.Count(f => owned.Contains(f.Element)) == owned.Count;
        }
        var components = Components(frame.Element).ToArray();
        // These audio components serialize only event-resource addresses (or no fields). Their
        // native constructors clear owner/delegate pointers and create audio runtime state lazily.
        bool pureKit = components.Count(c => c.TypeId == 0x360b668d) == 1 && components.All(c =>
            c.TypeId == 0x360b668d || c.TypeId == 0xb01ef4c1 && Graph.Fields(c).Count == 0 ||
            c.TypeId == 0x9eb8db8d && Graph.Fields(c).Keys.Order().SequenceEqual(new[] { "m_StartEvent", "m_StopEvent" }));
        bool actor = ActorContract(frame.Element);
        bool vehicleMarker=CompiledTranslocatedCarSupport.Supported(Graph,frame.Element);
        bool staticEntity = CompiledStaticEntitySupport.Supported(Graph, frame.Element)
            || CompiledTreeEntitySupport.Supported(Graph, frame.Element, explicitEntries: CurrentExplicitDependencyEntries)
            || CompiledFrameWrapperSupport.Supported(Graph, frame.Element);
        if (actor) actorScope = CurrentDescendants(frame.Element).ToHashSet();
        if (!pureKit && !PhysicalGroupContract(frame.Element) && !StaticGeometry(frame) && !actor && !vehicleMarker && !staticEntity && (actorScope is null || !ActorPartContract(frame.Element, actorScope))) return false;
        return frame.Children.All(child => ReferenceEquals(Graph.Owner(child.Element), Graph.Owner(frame.Element)) && SafeHierarchy(child, visited, actorScope));
    }

    public IFrameDuplication? Duplicate(IFrameNode source, out string? refusal)
    {
        try { return DuplicateCore(source, out refusal); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException)
        { refusal = ex.Message; return null; }
    }

    private IFrameDuplication? DuplicateCore(IFrameNode source, out string? refusal)
    {
        refusal = null;
        if (!CanDuplicate(source)) { refusal = "This hierarchy contains an unsupported entity, deformation or ownership boundary."; return null; }
        var frame = (CompiledFrameNode)source;
        var resource = Graph.Owner(frame.Element);
        var parentContainer = frame.ParentFrame is { } parent ? Hierarchy(parent) : null;
        CompiledElement? ResolveRegistration(CompiledElement item) => parentContainer is null ? Graph.Resolve(item) :
            Graph.Resolve(Graph.Field(Graph.Field(item, "Second") ?? item, "m_Object"));
        var container = parentContainer ?? Graph.Nodes.Select(n => Graph.Field(n, "m_Objects")).OfType<CompiledElement>()
            .FirstOrDefault(c => ReferenceEquals(Graph.Owner(c), resource) && CurrentItems(c).Any(e => ReferenceEquals(ResolveRegistration(e), frame.Element)));
        if (container is null) { refusal = "The object has no independent scene registration."; return null; }
        var entry = CurrentItems(container).First(e => ReferenceEquals(ResolveRegistration(e), frame.Element));
        var toc = Graph.Nodes.Select(n => Graph.Field(n, "m_ToC")).OfType<CompiledElement>()
            .FirstOrDefault(c => ReferenceEquals(Graph.Owner(c), resource));
        if (toc is null) { refusal = "The resource has no object GUID table."; return null; }
        var nodes = CurrentDescendants(entry).ToArray();
        var ids = new Dictionary<ulong, ulong>();
        var used = resource.Elements.Where(e => e.Event == 2).Select(e => e.ObjectId).ToHashSet();
        foreach (var pointer in resource.Elements.Where(e => e.Event == 2 && Graph.Kind(e) == 173))
            if (pointer.Values.FirstOrDefault() is { Event: 4, DataSize: 8 } value) used.Add(resource.Data(value).Length == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(resource.Data(value)) : 0);
        ulong Fresh(bool local)
        {
            ulong value;
            do { value = BinaryPrimitives.ReadUInt64LittleEndian(Guid.NewGuid().ToByteArray()) & 0x7fffffffffffffff; if (local) value |= 0x8000000000000000; }
            while (value == 0 || !used.Add(value) || Graph.Objects.ContainsKey(value));
            return value;
        }
        foreach (var node in nodes.Where(n => n.Event == 2 && n.ObjectId != 0)) ids.TryAdd(node.ObjectId, Fresh(false));
        foreach (var pointer in nodes.Where(n => n.Event == 2 && Graph.Kind(n) == 173 && n.Values.Count() > 1))
            if (pointer.Values.First() is { Event: 4, DataSize: 8 } value && Graph.Integer(value) != 0)
                ids.TryAdd(Graph.Integer(value), Fresh(true));
        if (!ids.ContainsKey(frame.Element.ObjectId)) { refusal = "The object has no independent GUID definition."; return null; }
        using var transaction = new ImportTransaction(this, frame.ParentFrame);
        var clone = resource.CloneSubtree(entry, ids, EffectivePlans().GetValueOrDefault(resource));
        Graph.RegisterSubtree(resource, clone);
        if (parentContainer is not null && Graph.Field(clone, "First") is { DataSize: 4 } key)
        {
            var usedKeys = CurrentItems(parentContainer).Select(e => Graph.Field(e, "First")).OfType<CompiledElement>().Select(Graph.Integer).ToHashSet();
            uint freshKey;
            do { freshKey = checked((uint)Random.Shared.Next(1024, int.MaxValue)); } while (usedKeys.Contains(freshKey));
            byte[] keyBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(keyBytes, freshKey);
            resource.Replace(key, keyBytes);
        }
        var element = Graph.Objects[ids[frame.Element.ObjectId]];
        var name = Graph.Field(element, "m_Name")!;
        string newName = frame.Name + "_copy";
        for (int index = 2; _frames.Values.Any(f => f.Name == newName); index++) newName = frame.Name + "_copy" + index;
        resource.SetClonedValue(name, Encoding.UTF8.GetBytes(newName + '\0'));
        if (Graph.Field(element, "m_HashName") is { DataSize: 8 } hash)
        {
            byte[] bytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, Fnv64.Hash(newName, Encoding.UTF8));
            resource.Replace(hash, bytes);
        }
        var geometryCopies = new List<GeometryCopy>();
        try
        {
            geometryCopies = CopyHierarchyAssets(clone, ids);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
            refusal = "Independent geometry copy is unavailable: " + ex.Message;
            return null;
        }
        var activeIds = Descendants(clone).Where(n => n.Event == 2 && n.ObjectId != 0).Select(n => n.ObjectId).ToHashSet();
        var tables = new List<CompiledElement>();
        foreach (var value in CurrentItems(toc))
            if (value.Event == 4 && value.DataSize == 8 && ids.TryGetValue(Graph.Integer(value), out ulong newId) && activeIds.Contains(newId))
            {
                var copied = resource.CloneSubtree(value, ids);
                Graph.RegisterSubtree(resource, copied);
                tables.Add(copied);
            }
        var copiedNodes = Descendants(clone).ToArray();
        var allocated = copiedNodes.Where(n => n.Event == 2 && n.ObjectId != 0).ToHashSet();
        foreach (var pointer in copiedNodes.Where(n => n.Event == 2 && Graph.Kind(n) == 173))
            if (pointer.Values.ToArray() is { Length: > 1 } values && values[1].Event == 2) allocated.Add(values[1]);
        var counts = allocated.GroupBy(n => n.TypeId).ToDictionary(g => g.Key, g => g.Count());
        var copiedFrames = new List<CompiledFrameNode>();
        var copiedFrame = RegisterCopiedHierarchy(element, frame.ParentFrame,
            parentContainer is not null ? Graph.Field(Graph.Field(clone, "Second") ?? clone, "m_LocalTransform") : null, copiedFrames);
        int firstMesh = Meshes.Count;
        var root = Build(copiedFrame, new CompiledMeshDecoder(Graph, Illusion.Assets.Library.StageCompanions.For(SourceArchive)), [], 0);
        var result = new CompiledDuplication(this, copiedFrame, root, resource, container, clone, toc, tables, counts, Meshes.Skip(firstMesh).ToArray(), geometryCopies, copiedFrames.ToArray());
        result.Reattach();
        IEditAction? definitions = null;
        try
        {
            definitions = RetainCopiedDefinitions(resource, frame.Element, resource.StreamIndex(container));
            IFrameDuplication retained = definitions is null ? result : new DependentDuplication(this, resource, result, definitions);
            transaction.Commit(); return retained;
        }
        catch { definitions?.Undo(); throw; }
    }

    private CompiledFrameNode RegisterCopiedHierarchy(CompiledElement element, CompiledFrameNode? parent,
        CompiledElement? link, List<CompiledFrameNode> copies, bool register = true)
    {
        var owned = Descendants(element).Where(n => n.Event == 2 && n.TypeId == 0x4403597e).ToHashSet();
        var visited = new HashSet<CompiledElement>();
        CompiledFrameNode MakeFrame(CompiledElement scene, CompiledFrameNode? frameParent, CompiledElement? transform)
        {
            if (!owned.Contains(scene) || !visited.Add(scene)) throw new NotSupportedException("Kit children must have unique inline owned scene definitions.");
            var copy = new CompiledFrameNode(this, scene) { ParentFrame = frameParent, LinkTransform = transform };
            copies.Add(copy);
            if (register) _frames.Add(scene, copy);
            if (Hierarchy(copy) is { } children)
                foreach (var entry in Graph.Items(children))
                {
                    var second = Graph.Field(entry, "Second");
                    var childScene = Graph.Resolve(Graph.Field(second, "m_Object"));
                    var childTransform = Graph.Field(second, "m_LocalTransform");
                    if (childScene is null || childTransform is not { DataSize: 48 } || Graph.Field(entry, "First") is not { } key)
                        throw new InvalidDataException("Invalid copied Kit entry.");
                    var child = MakeFrame(childScene, copy, childTransform);
                    copy.Children.Add(child);
                    if (register) _links[child] = (entry, childTransform, children, checked((uint)Graph.Integer(key)));
                }
            return copy;
        }
        return MakeFrame(element, parent, link);
    }

    private IEnumerable<CompiledElement> CurrentDescendants(CompiledElement node)
    {
        if (node.Event == 0) yield break;
        yield return node;
        var plan = EffectivePlans().GetValueOrDefault(Graph.Owner(node));
        var children = plan is not null && plan.Children.TryGetValue(node, out var staged) ? staged : node.Children;
        foreach (var child in children) foreach (var nested in CurrentDescendants(child)) yield return nested;
    }

    private static IEnumerable<CompiledElement> Descendants(CompiledElement node)
    {
        if (node.Event == 0) yield break;
        yield return node;
        foreach (var child in node.Children) foreach (var nested in Descendants(child)) yield return nested;
    }

    private sealed class CompiledDuplication(CompiledSceneDocument document, CompiledFrameNode frame, SdsFrameNode root,
        CompiledResource resource, CompiledElement container, CompiledElement registration, CompiledElement toc,
        List<CompiledElement> tables, Dictionary<uint, int> counts, MeshData[] meshes, List<GeometryCopy> geometryCopies, CompiledFrameNode[]? frames = null) : IFrameDuplication
    {
        private readonly CompiledFrameNode[] _copiedFrames = frames ?? [frame];
        public ISceneHierarchyNode Root => root;
        private bool _attached;
        public void RebuildRoot()
        {
            document.Roots.Remove(root);
            foreach (var mesh in meshes) document.Meshes.Remove(mesh);
            int start = document.Meshes.Count;
            root = document.Build(frame, new CompiledMeshDecoder(document.Graph, Illusion.Assets.Library.StageCompanions.For(document.SourceArchive)), [], 0);
            meshes = document.Meshes.Skip(start).ToArray();
            if (frame.ParentFrame is null) document.Roots.Add(root);
        }
        public void Reattach()
        {
            if (_attached) return;
            foreach (var geometry in geometryCopies) geometry.Reattach();
            document.Graph.SetSubtreeActive(resource, registration, true);
            foreach (var copied in _copiedFrames) document._frames[copied.Element] = copied;
            frame.ParentFrame?.Children.Add(frame);
            foreach (var mesh in meshes) if (!document.Meshes.Contains(mesh)) document.Meshes.Add(mesh);
            document.SetItems(container, document.CurrentItems(container).Append(registration).ToArray());
            document.SetItems(toc, document.CurrentItems(toc).Concat(tables).OrderBy(document.Graph.Integer).ToArray(), restoreOrder: false);
            var plan = document.Plan(resource);
            plan.AllowNativeAllocatorFallback = true;
            foreach (var (type, count) in counts) plan.AllocationCountDeltas[type] = plan.AllocationCountDeltas.GetValueOrDefault(type) + count;
            if (frame.ParentFrame is null) document.Roots.Add(root);
            _attached = true;
        }
        public void Detach()
        {
            if (!_attached) return;
            document.SetItems(container, document.CurrentItems(container).Where(e => !ReferenceEquals(e, registration)).ToArray());
            document.SetGuidTableItems(toc, document.CurrentItems(toc).Except(tables).ToArray());
            var plan = document.Plan(resource);
            foreach (var (type, count) in counts)
            {
                int delta = plan.AllocationCountDeltas[type] - count;
                if (delta == 0) plan.AllocationCountDeltas.Remove(type); else plan.AllocationCountDeltas[type] = delta;
            }
            foreach (var copied in _copiedFrames) document._frames.Remove(copied.Element);
            frame.ParentFrame?.Children.Remove(frame);
            document.Graph.SetSubtreeActive(resource, registration, false);
            foreach (var mesh in meshes) document.Meshes.Remove(mesh);
            document.Roots.Remove(root);
            foreach (var geometry in geometryCopies.AsEnumerable().Reverse()) geometry.Detach();
            _attached = false;
        }
    }
}
