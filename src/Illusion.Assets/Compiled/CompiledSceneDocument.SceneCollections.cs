using Illusion.Formats.Compiled;
using Illusion.Domain;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private readonly HashSet<CompiledElement> _sceneCollectionAuthoringOrigins = [];
    private readonly HashSet<CompiledElement> _sceneCollectionLodProxyGroups = [];
    private const string SceneCollectionLodRefusal = "This object belongs to a linked native coarse/fine LOD group. Prepare a verified family for supported paired authoring; direct changes remain read-only.";

    private void RegisterSceneCollectionProxyGroups(IEnumerable<CompiledElement> scenes)
    {
        foreach (var scene in scenes)
            foreach (var component in Components(scene).Where(component => component.TypeId == 0x2da22400))
            {
                var group = Graph.Resolve(Graph.Field(component, "m_LODGroup"));
                if (group?.TypeId == 0x23001d00 && Graph.Items(Graph.Field(group, "m_Children")).Count > 0
                    && Graph.Items(Graph.Field(component, "m_Visuals")).Select(Graph.Resolve).OfType<CompiledElement>()
                        .Any(visual => Graph.Resolve(Graph.Field(visual, "m_MeshSet")) is not null))
                    _sceneCollectionLodProxyGroups.Add(group);
            }
    }

    private bool HasLinkedSceneCollectionProxy(CompiledFrameNode frame)
    {
        foreach (var component in Components(frame.Element).Where(component => component.TypeId == 0x2da22400))
        {
            var visited = new HashSet<CompiledElement>();
            for (var group = Graph.Resolve(Graph.Field(component, "m_LODGroup")); group?.TypeId == 0x23001d00 && visited.Add(group);
                 group = Graph.Resolve(Graph.Field(group, "m_Parent")))
                if (_sceneCollectionLodProxyGroups.Contains(group)) return true;
        }
        return false;
    }

    /// <summary>Preflight for native proxy-linked frames and newly exposed grouped scene-collection owners.</summary>
    public string? ValidateSceneCollectionMutation(IFrameNode source)
    {
        if (CompiledVehicleRig.BoneMutationRefusal(source) is { } boneRefusal) return boneRefusal;
        var canonical = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
        if (canonical is not CompiledFrameNode frame || !_frames.TryGetValue(frame.Element, out var stored)
            || !ReferenceEquals(frame, stored)) return null;
        if (_proxyStructuralAuthorization.Contains(frame)) return null;
        var visited = new HashSet<CompiledFrameNode>();
        bool Blocked(CompiledFrameNode current)
        {
            var ancestors = new List<CompiledFrameNode>();
            var seen = new HashSet<CompiledFrameNode>();
            for (var item = current; item is not null && seen.Add(item); item = item.ParentFrame) ancestors.Add(item);
            if (ancestors.Any(HasLinkedSceneCollectionProxy)) return true;
            int origin = ancestors.FindLastIndex(item => _sceneCollectionAuthoringOrigins.Contains(item.Element));
            return origin >= 0 && ancestors.Take(origin + 1).Any(item => Components(item.Element).Any(component =>
                component.TypeId == 0x2da22400 && Graph.Field(component, "m_LODGroup") is { } group
                && (Graph.Resolve(group) is not null || group.Event == 4 && group.DataSize is 8 or 12 && Graph.Integer(group) != 0)));
        }
        bool Visit(CompiledFrameNode current) => visited.Add(current) && (Blocked(current) || current.Children.Any(Visit));
        return Visit(frame) ? SceneCollectionLodRefusal : null;
    }

    public sealed partial class CompiledFrameNode
    {
        internal string? SceneCollectionMutationRefusal => _document.ValidateSceneCollectionMutation(this);
    }

    // C_SceneCollectionActivator forwards AddToScene only through its actual resource root's
    // m_Objects table. C_ModelCollection deliberately has no AddToScene implementation.
    private HashSet<CompiledElement> PlacedSceneCollectionObjects(IEnumerable<CompiledResource> resources)
    {
        var placed = new HashSet<CompiledElement>();
        var visited = new HashSet<CompiledElement>();
        void Visit(CompiledElement? collection)
        {
            if (collection?.TypeId != 0x8fa874c4 || !visited.Add(collection)) return;
            foreach (var item in Graph.Items(Graph.Field(collection, "m_Objects")))
            {
                var value = Graph.Resolve(item);
                if (value?.TypeId == 0x4403597e) placed.Add(value);
                else if (value?.TypeId == 0x8fa874c4) Visit(value);
                // Never walk model registries or arbitrary object dependencies as placed scenes.
            }
        }
        foreach (var resource in resources)
            foreach (var root in resource.Roots.Where(root => root.Event == 2))
                Visit(Graph.Resolve(Graph.Field(root, "m_Object")));
        return placed;
    }
}
