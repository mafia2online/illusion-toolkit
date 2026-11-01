using System.Buffers.Binary;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Owned stock car-model hierarchy, preserving its registered model and built vehicle initialization.</summary>
internal static class CompiledVehiclePlacementSupport
{
    private static readonly HashSet<uint> Components = [0x360b668d, 0x9eb8db8d, 0xb01ef4c1, 0xdeb11995,
        0x0fa73185, 0x1b794a16, 0x2a6b13a2, 0x2a6b13a3, 0x2b7b4775, 0x2da22400,
        0xde47edf6, 0xf65309a2, 0x30501f8d, 0x44cd3c59];

    internal static bool Supported(CompiledGraph graph, CompiledElement root)
    {
        var owned = CompiledActorCatalogSupport.Tree(root).ToHashSet();
        var scenes = owned.Where(n => n.Event == 2 && n.TypeId == 0x4403597e).ToArray();
        if (root.TypeId != 0x4403597e || scenes.Length is < 2 or > 128) return false;
        var components = new List<CompiledElement>();
        foreach (var scene in scenes)
        {
            var entries = graph.Items(graph.Field(scene, "m_Components"));
            var resolved = entries.Select(graph.Resolve).OfType<CompiledElement>().ToArray();
            if (entries.Count != resolved.Length || resolved.Any(c => !owned.Contains(c) || !Components.Contains(c.TypeId))
                || graph.Items(graph.Field(scene, "m_ComponentsMarkedForDelete")).Count != 0) return false;
            components.AddRange(resolved);
        }
        var wrappers = components.Where(c => c.TypeId == 0xdeb11995).ToArray();
        if (wrappers.Length != 1 || !graph.Items(graph.Field(root, "m_Components")).Any(e => ReferenceEquals(graph.Resolve(e), wrappers[0]))) return false;
        var entity = wrappers[0];
        if (graph.Field(entity, "m_EntityType") is not { DataSize: 4 } type || graph.Integer(type) != 10
            || graph.Field(entity, "m_ActivateOnInit") is not { DataSize: 1 } activate || graph.Integer(activate) != 1
            || graph.Field(entity, "m_ModelName") is not { } name || string.IsNullOrWhiteSpace(graph.Text(name))
            || graph.Field(graph.Field(entity, "m_InitProps"), "m_Array") is not { DataSize: 3960 } init
            || BinaryPrimitives.ReadUInt32LittleEndian(graph.Data(init)) != 3956
            || graph.Items(graph.Field(entity, "m_RawInitProps")).Count != 0
            || graph.Resolve(graph.Field(entity, "m_BuiltPrefabsCPtr")) is not { TypeId: 0xcc1524eb } prefab
            || graph.Items(graph.Field(prefab, "m_MorphInitData")).Count != 0
            || graph.Resolve(graph.Field(entity, "m_InstanceSpecificData")) is not { } instance || !owned.Contains(instance)) return false;
        foreach (var value in owned.Where(n => n.Event == 4 && n.DataSize is 8 or 12))
            if (graph.Resolve(value) is { TypeId: 0x4403597e } target && !owned.Contains(target)) return false;
        foreach (var component in components)
        {
            // A populated bone hierarchy has an independent registration layout, not a Kit map.
            if (component.TypeId == 0x1b794a16 && graph.Items(graph.Field(component, "m_Children")).Count != 0) return false;
            if (component.TypeId == 0xde47edf6 && graph.Fields(component).Count != 0) return false;
            if (component.TypeId == 0x30501f8d && graph.Resolve(graph.Field(component, "m_InitData")) is not { TypeId: 0x180a6b58 }) return false;
        }
        return components.Any(c => c.TypeId == 0x360b668d) && components.Any(c => c.TypeId == 0x2da22400);
    }
}
