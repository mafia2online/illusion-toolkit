using System.Buffers.Binary;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private static readonly HashSet<uint> ActorComponents =
        [0xdeb11995, 0x0fa73185, 0x2a6b13a2, 0x2a6b13a3, 0x2b7b4775, 0x2da22400, 0x360b668d, 0xb01ef4c1, 0x9eb8db8d, 0xf65309a2, 0x44cd3c59];

    private bool PhysicalGroupContract(CompiledElement scene)
    {
        var components = Components(scene).ToArray();
        if (Graph.Field(scene, "m_Components") is not { } componentList || components.Length != CurrentItems(componentList).Count) return false;
        if (components.Count(c => c.TypeId == 0x360b668d) != 1 || components.Count(c => c.TypeId == 0x2b7b4775) != 1 ||
            components.Any(c => c.TypeId is not (0x360b668d or 0x2b7b4775 or 0xb01ef4c1 or 0x9eb8db8d))) return false;
        var owned = CurrentDescendants(scene).ToHashSet();
        return components.All(owned.Contains);
    }

    private List<GeometryCopy> CopyHierarchyAssets(CompiledElement root, IReadOnlyDictionary<ulong, ulong> ids)
    {
        var copies = new List<GeometryCopy>();
        var actorParts = new HashSet<CompiledElement>();
        foreach (var scene in Descendants(root).Where(n => n.Event == 2 && n.TypeId == 0x4403597e).ToArray())
        {
            if (!actorParts.Contains(scene))
            {
                copies.AddRange(CopyActorAssets(scene, ids));
                if (ActorContract(scene)) actorParts.UnionWith(Descendants(scene).Where(n => n.TypeId == 0x4403597e));
            }
            copies.AddRange(CopyGeometry(scene));
        }
        return copies;
    }

    private bool ActorContract(CompiledElement scene)
    {
        var components = Components(scene).ToArray();
        if (Graph.Field(scene, "m_Components") is not { } componentList || components.Length != CurrentItems(componentList).Count) return false;
        if (components.Count(c => c.TypeId == 0xdeb11995) != 1) return false;
        var entity = components.First(c => c.TypeId == 0xdeb11995);
        if (entity is null || components.Any(c => !ActorComponents.Contains(c.TypeId)) ||
            Graph.Field(entity, "m_EntityType") is not { } type || Graph.Integer(type) != 12 ||
            Graph.Resolve(Graph.Field(entity, "m_BuiltPrefabsCPtr")) is not { TypeId: 0xeab55c09 } prefab ||
            Graph.Items(Graph.Field(prefab, "m_MorphInitData")).Count != 0) return false;
        var owned = CurrentDescendants(scene).ToHashSet();
        if (components.Any(c => !owned.Contains(c)) || owned.Where(n => n.Event == 2 && n.TypeId == 0x4403597e).SelectMany(Components).Count(c => c.TypeId == 0xdeb11995) != 1) return false;
        foreach (var value in owned.Where(n => n.Event == 4 && n.DataSize is 8 or 12))
            if (Graph.Resolve(value) is { TypeId: 0x4403597e } target && !owned.Contains(target)) return false;
        var instance = Graph.Resolve(Graph.Field(entity, "m_InstanceSpecificData"));
        if (instance is not null && !owned.Contains(instance)) return false;
        return components.Any(c => Graph.Items(Graph.Field(c, "m_Visuals")).Count != 0 || Graph.Items(Graph.Field(c, "m_Children")).Count != 0);
    }

    private bool ActorPartContract(CompiledElement scene, HashSet<CompiledElement> actorScope)
    {
        var owned = CurrentDescendants(scene).ToHashSet();
        var components = Components(scene).ToArray();
        if (Graph.Field(scene, "m_Components") is not { } componentList || components.Length != CurrentItems(componentList).Count) return false;
        if (components.Length == 0 || components.Any(c => c.TypeId == 0xdeb11995 || !ActorComponents.Contains(c.TypeId) || !owned.Contains(c))) return false;
        foreach (var value in owned.Where(n => n.Event == 4 && n.DataSize is 8 or 12))
            if (Graph.Resolve(value) is { TypeId: 0x4403597e } target && !actorScope.Contains(target)) return false;
        return true;
    }

    private List<GeometryCopy> CopyActorAssets(CompiledElement scene, IReadOnlyDictionary<ulong, ulong> sceneIds)
    {
        if(CompiledTranslocatedCarSupport.Supported(Graph,scene))return [];
        var entity = Components(scene).FirstOrDefault(c => c.TypeId == 0xdeb11995);
        bool actor = entity is not null;
        if (!actor && !Components(scene).Any(c => c.TypeId == 0x2b7b4775)) return [];
        if (actor && !ActorContract(scene)) throw new NotSupportedException("Only verified type-12 actor/prefab ownership contracts can be copied.");
        var globalSceneIds = sceneIds.Where(p => Graph.Objects.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        var copy = new GeometryCopy(this, globalSceneIds);
        void Guard(CompiledElement source)
        {
            if (Descendants(source).Any(n => n.Event == 2 && n.TypeId == 0x4403597e))
                throw new NotSupportedException("Actor resource embeds an independently owned scene definition.");
            foreach (var value in Descendants(source).Where(n => n.Event == 4 && n.DataSize is 8 or 12))
                if (Graph.Resolve(value) is { TypeId: 0x4403597e } target && !globalSceneIds.ContainsKey(target.ObjectId))
                    throw new NotSupportedException("Actor prefab references an external scene placement.");
        }
        if (actor)
        {
            var prefab = Graph.Resolve(Graph.Field(entity, "m_BuiltPrefabsCPtr"))!;
            Guard(prefab);
            copy.CloneDefinition(prefab, followDependencies: false);
        }
        var subtreeComponents = actor ? Descendants(scene).Where(n => n.Event == 2 && n.TypeId == 0x4403597e).SelectMany(Components).ToArray() : Components(scene).ToArray();
        foreach (var component in subtreeComponents)
            if (Graph.Resolve(Graph.Field(component, "m_HierarchyDataPtr")) is { } hierarchy)
            {
                if (hierarchy.TypeId != 0xc772e406) throw new NotSupportedException("Unknown actor deformation hierarchy schema.");
                Guard(hierarchy);
                copy.CloneDefinition(hierarchy, followDependencies: false);
            }
        var physicsShapes = new List<(CompiledElement Shape, CompiledElement Physics)>();
        foreach (var component in subtreeComponents)
            foreach (var entry in Graph.Items(Graph.Field(component, "m_ElementDefs")))
                {
                    if (Graph.Field(entry, "m_ItemDesc") is not { DataSize: 8 or 12 } field ||
                        !Graph.Objects.TryGetValue(Graph.Integer(field), out var shape))
                        throw new NotSupportedException("Actor collision descriptor is unresolved or has an unknown reference layout.");
                    if (shape.TypeId is not (0x48430eeb or 0x63ee7798 or 0x4fd32ee0)) throw new NotSupportedException("Unknown actor collision descriptor.");
                    var physics = CollisionPhysics(shape) ?? throw new NotSupportedException("Actor collision has no resolved PC physics data.");
                    Guard(shape); Guard(physics);
                    var clonedShape = copy.CloneDefinition(shape, followDependencies: false);
                    var clonedPhysics = copy.CloneDefinition(physics, followDependencies: false);
                    physicsShapes.Add((clonedShape, clonedPhysics));
                }
        copy.Finish();
        void Number(CompiledElement node, string name, ulong value)
        {
            if (Graph.Field(node, name) is not { DataSize: 8 or 12 } field) throw new NotSupportedException("Actor asset has an unsupported identity field: " + name);
            byte[] bytes = Graph.Data(field).ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            Graph.Owner(field).Replace(field, bytes);
        }
        foreach (var (shape, physics) in physicsShapes)
        {
            Number(physics, "m_Id", physics.ObjectId);
            Number(shape, "m_DataHash", physics.ObjectId);
            Number(shape, "m_Hash", shape.ObjectId);
        }
        foreach (var field in Descendants(scene).Where(n => n.Event == 4 && n.DataSize is 8 or 12))
            if (copy.GlobalIds.TryGetValue(Graph.Integer(field), out ulong id))
            {
                byte[] bytes = Graph.Data(field).ToArray();
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, id);
                Graph.Owner(field).Replace(field, bytes);
            }
        return [copy];
    }
}
