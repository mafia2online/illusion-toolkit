using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Exact stock type-33 traffic-spawner marker contract, distinct from a type-10 car asset.</summary>
internal static class CompiledTranslocatedCarSupport
{
    private static readonly string[] ExpectedFields=new[]{"m_EntityType","m_ActivateOnInit","m_ModelName","m_InitProps","m_RawInitProps","m_BuiltPrefabsCPtr","m_InstanceSpecificData"}.Order(StringComparer.Ordinal).ToArray();
    private static readonly HashSet<string> Models=new(StringComparer.Ordinal)
    {
        "translocator_common_car","translocator_pickup","translocator_luxury","translocator_truck",
        "translocator_boat_big","translocator_boat_medium","translocator_hotrod","translocator_trailer",
        "translocator_boat_small","translocator_police_car","translocator_taxi"
    };
    internal static bool Supported(CompiledGraph graph,CompiledElement scene)
    {
        if(scene.TypeId!=0x4403597e||graph.Field(scene,"m_Components")is not{} list)return false;
        var entries=graph.Items(list);if(entries.Count!=1)return false;
        var entity=graph.Resolve(entries[0]);
        if(entity?.TypeId!=0xdeb11995||graph.Field(entity,"m_EntityType")is not{DataSize:4} type||graph.Integer(type)!=33||
            !CompiledActorCatalogSupport.Tree(scene).Contains(entity)||
            !graph.Fields(entity).Keys.Order(StringComparer.Ordinal).SequenceEqual(ExpectedFields)||
            graph.Field(scene,"m_DataFlags")is not{DataSize:4} flags||graph.Integer(flags)!=1||
            graph.Field(entity,"m_ActivateOnInit")is not{DataSize:1} activate||graph.Integer(activate)!=1||
            graph.Field(entity,"m_ModelName")is not{} model||!Models.Contains(graph.Text(model))||
            graph.Field(entity,"m_BuiltPrefabsCPtr")is not{Event:4,DataSize:12} prefab||graph.Integer(prefab)!=0||
            graph.Resolve(graph.Field(entity,"m_InstanceSpecificData"))is not null||
            graph.Items(graph.Field(entity,"m_RawInitProps")).Count!=0||
            graph.Items(graph.Field(scene,"m_ComponentsMarkedForDelete")).Count!=0)return false;
        var properties=graph.Field(graph.Field(entity,"m_InitProps"),"m_Array");
        return properties is{Event:4,DataSize:5}&&graph.Data(properties).SequenceEqual(new byte[]{1,0,0,0,0})&&
            CompiledActorCatalogSupport.Tree(scene).Count(n=>n.Event==2&&n.TypeId==0x4403597e)==1;
    }

    internal static CompiledPropPreviewData Preview(CompiledPropEntry entry)
        =>new([HelperMesh("Traffic spawn marker helper (no vehicle geometry)",Matrix4x4.Identity)],[Path.GetDirectoryName(entry.SourceResourcePath)!],1);

    internal static MeshData HelperMesh(string name,Matrix4x4 world)
    {
        // This is an editor arrow, not a fabricated vehicle model or physical collision volume.
        Vector3[] positions=[new(-.3f,-.6f,0),new(.3f,-.6f,0),new(.3f,.3f,0),new(.6f,.3f,0),new(0,1.1f,0),new(-.6f,.3f,0),new(-.3f,.3f,0)];
        uint[] indices=[0,1,2,0,2,6,6,2,4,2,3,4,6,4,5];
        return new MeshData{Name=name,World=world,Positions=positions,Normals=Enumerable.Repeat(Vector3.UnitZ,positions.Length).ToArray(),Indices=indices,
            Parts=[new MeshPart(0,indices.Length,null,tint:new Vector4(1,.65f,.1f,1))]};
    }
}
