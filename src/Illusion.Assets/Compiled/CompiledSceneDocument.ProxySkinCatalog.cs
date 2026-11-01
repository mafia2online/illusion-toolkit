namespace Illusion.Assets.Compiled;

public sealed record CompiledPreparedSkinSource(string Name,string ResourcePath,ulong ObjectId,int Bones,int Levels)
{
    public override string ToString()=>$"{Name} ({Bones} bones, {Levels} level{(Levels==1?"":"s")})";
}

public sealed partial class CompiledSceneDocument
{
    /// <summary>Read-only inventory of shared-library sources passing the full native skin,
    /// palette and all-level joint-envelope contract. Import revalidates the selected source.</summary>
    public static IReadOnlyList<CompiledPreparedSkinSource> ReadPreparedSkinSources(FileInfo archive)
    {
        if(!archive.Exists||archive.Directory?.Name.Equals("city_crash",StringComparison.OrdinalIgnoreCase)!=true)
            throw new NotSupportedException("Choose this city's shared prop archive.");
        var document=new CompiledSceneDocument(archive);var result=new List<CompiledPreparedSkinSource>();
        foreach(var scene in document.Graph.NodesOfType(0x4403597e))
        {
            if(scene.ObjectId==0)continue;
            var frame=new CompiledFrameNode(document,scene);
            if(!document.PreparedSkinContract(frame,out _))continue;
            int levels=document.ProxyVisuals(frame).Select(visual=>document.Graph.Items(document.Graph.Field(document.Graph.Resolve(document.Graph.Field(visual,"m_MeshSet")),"m_LODs")).Count).Max();
            result.Add(new(frame.Name,document.Graph.Owner(scene).Path,scene.ObjectId,document.VehicleRig(frame)!.Count,levels));
        }
        return result.OrderBy(source=>source.Name,StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
