using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Read-only catalog/preview checks for the actor families supported by the authoring backend.</summary>
internal static class CompiledActorCatalogSupport
{
    internal static IEnumerable<CompiledElement> Tree(CompiledElement node)
    {
        if (node.Event == 0) yield break;
        yield return node;
        foreach (var child in node.Children) foreach (var nested in Tree(child)) yield return nested;
    }
    internal static bool Supported(CompiledGraph graph, CompiledElement root, bool actor)
    {
        var owned = Tree(root).ToHashSet();
        var scenes = owned.Where(n => n.Event == 2 && n.TypeId == 0x4403597e).ToArray();
        if (scenes.Length is < 1 or > 128) return false;
        if (scenes.Any(s => graph.Items(graph.Field(s, "m_Components")).Any(e => graph.Resolve(e) is null))) return false;
        var components = scenes.SelectMany(s => graph.Items(graph.Field(s, "m_Components"))).Select(graph.Resolve).OfType<CompiledElement>().ToArray();
        uint[] known = actor ? [0xdeb11995,0x0fa73185,0x2a6b13a2,0x2a6b13a3,0x2b7b4775,0x2da22400,0x360b668d,0xb01ef4c1,0x9eb8db8d,0xf65309a2,0x44cd3c59]
            : [0x0fa73185,0x2da22400,0x360b668d,0x2b7b4775];
        if (components.Any(c => !known.Contains(c.TypeId) || !owned.Contains(c))) return false;
        var entities = components.Where(c => c.TypeId == 0xdeb11995).ToArray();
        if (actor && entities.Length < 1 || !actor && entities.Length != 0) return false;
        // Stock nested actors are independent type-12 children under an owned Kit, not a new
        // multi-entity activation contract on an actor root. Keep that latter case refused.
        var rootComponents=graph.Items(graph.Field(root,"m_Components")).Select(graph.Resolve).OfType<CompiledElement>().ToArray();
        if(entities.Length>1&&(!rootComponents.Any(c=>c.TypeId==0x360b668d)||rootComponents.Any(c=>c.TypeId==0xdeb11995)))return false;
        foreach (var entity in entities)
        {
            if (graph.Field(entity, "m_EntityType") is not { } type || graph.Integer(type) != 12 ||
                graph.Resolve(graph.Field(entity, "m_BuiltPrefabsCPtr")) is not { TypeId: 0xeab55c09 } prefab ||
                graph.Items(graph.Field(prefab, "m_MorphInitData")).Count != 0) return false;
            var owner=scenes.SingleOrDefault(s=>graph.Items(graph.Field(s,"m_Components")).Any(c=>ReferenceEquals(graph.Resolve(c),entity)));
            if(owner is null)return false;
            var actorScope=Tree(owner).ToHashSet();
            if(actorScope.Count(n=>n.Event==2&&n.TypeId==0xdeb11995)!=1)return false;
            if(graph.Resolve(graph.Field(entity,"m_InstanceSpecificData"))is{} instance&&!actorScope.Contains(instance))return false;
            foreach(var value in actorScope.Where(n=>n.Event==4&&n.DataSize is 8 or 12))
                if(graph.Resolve(value)is{TypeId:0x4403597e} target&&!actorScope.Contains(target))return false;
        }
        foreach (var value in owned.Where(n => n.Event == 4 && n.DataSize is 8 or 12))
            if (graph.Resolve(value) is { TypeId: 0x4403597e } target && !owned.Contains(target)) return false;
        foreach (var entry in components.SelectMany(c => graph.Items(graph.Field(c, "m_ElementDefs"))))
        {
            if (graph.Field(entry, "m_ItemDesc") is not { DataSize: 8 or 12 } field || !graph.Objects.TryGetValue(graph.Integer(field), out var shape) ||
                shape.TypeId is not (0x48430eeb or 0x63ee7798 or 0x4fd32ee0)) return false;
            ulong hash = graph.Field(shape, "m_DataHash") is { } h ? graph.Integer(h) : 0;
            if (!graph.Nodes.Any(n => n.TypeId == 0xaf6549f7 && graph.Field(n, "m_Platform") is { } platform && graph.Integer(platform) == 0 &&
                (hash == 0 ? ReferenceEquals(graph.Owner(n), graph.Owner(shape)) : graph.Field(n, "m_Id") is { } id && graph.Integer(id) == hash))) return false;
        }
        return components.Any(c => graph.Items(graph.Field(c, "m_Visuals")).Count != 0);
    }
}
