using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledTriangleCollision(Action<string,bool> check)
    {
        var archive=new FileInfo(Path.Combine(MafiaEnvironment.CityFolder,"bc_bayou_block_a_lod0.sds"));
        if(!archive.Exists)return;
        var document=new CompiledSceneDocument(archive);
        var snapshot=document.Graph.Resources.ToDictionary(r=>r.Path,r=>File.ReadAllBytes(r.Path));
        string manifest=Path.Combine(SdsMeshLoader.EnsureExtracted(archive),"SDSContent.xml");snapshot.TryAdd(manifest,File.ReadAllBytes(manifest));
        byte[] archiveBytes=File.ReadAllBytes(archive.FullName);
        const string name="probe_native_triangle_collision";
        try
        {
            var group=document.CreateGroup(name,null,out var why)??throw new InvalidOperationException(why);
            var owner=(IFrameNode)group.Root.Source!;
            Vector3[] points=[new(0,0,0),new(2,0,0),new(2,1,0),new(1,1,0),new(1,2,0),new(0,2,0)];
            uint[] indices=[0,1,3,1,2,3,0,3,5,3,4,5];
            var volume=document.AddCollision(owner,"triangle",Vector3.One,Matrix4x4.Identity,points,103,out why,triangleIndices:indices)
                ??throw new InvalidOperationException(why);
            foreach(var resource in document.Graph.Resources)snapshot.TryAdd(resource.Path,File.ReadAllBytes(resource.Path));
            var geometry=document.CollisionMesh((IFrameNode)volume.Root.Source!)??throw new InvalidDataException("Authored triangle collision is unavailable.");
            check("native concave L triangle collision retains four triangles and notch",geometry.TriangleCount==4&&!TriangleProbeContains(geometry,new Vector2(1.5f,1.5f)));
            document.SaveWorkingCopy();
            var reload=new CompiledSceneDocument(archive);
            var reloaded=TriangleProbeMesh(reload,name);
            check("native triangle collision save/reload preserves geometry",reloaded.Positions.SequenceEqual(geometry.Positions)&&reloaded.Indices.SequenceEqual(geometry.Indices));
            ValidateTriangleProbeMaterial(reload,name,check);
            var packed=SdsArchive.Pack(MafiaEnvironment.ExtractedDir(archive),MafiaEnvironment.Profile);
            using(var stream=new MemoryStream())
            {
                packed.Save(stream,new SdsWriteOptions{Compress=true});stream.Position=0;SdsArchive.Load(stream).Extract(MafiaEnvironment.ExtractedDir(archive));
            }
            var fresh=TriangleProbeMesh(new CompiledSceneDocument(archive),name);
            check("native triangle collision compressed build/fresh extraction/reload",fresh.Positions.SequenceEqual(geometry.Positions)&&fresh.Indices.SequenceEqual(geometry.Indices));
            volume.Detach();group.Detach();document.SaveWorkingCopy();
            check("native triangle collision undo restores every GENR",snapshot.Where(p=>p.Key!=manifest).All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
            group.Reattach();volume.Reattach();document.SaveWorkingCopy();
            check("native triangle collision redo survives reload",TriangleProbeMesh(new CompiledSceneDocument(archive),name).TriangleCount==4);
            volume.Detach();group.Detach();document.SaveWorkingCopy();
            check("native triangle collision redo/undo restores exact resources",snapshot.Where(p=>p.Key!=manifest).All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
            check("native triangle collision source archive untouched",File.ReadAllBytes(archive.FullName).SequenceEqual(archiveBytes));
        }
        finally{foreach(var(path,bytes)in snapshot)File.WriteAllBytes(path,bytes);}
    }

    private static MeshData TriangleProbeMesh(CompiledSceneDocument document,string name)
    {
        static IEnumerable<ISceneHierarchyNode> Walk(IEnumerable<ISceneHierarchyNode> roots)
        {foreach(var root in roots){yield return root;foreach(var child in Walk(root.Children))yield return child;}}
        var frame=Walk(document.Roots).Where(row=>row.Name==name).Select(row=>row.Source).OfType<IFrameNode>().Single();
        return document.CollisionMesh(document.CollisionVolumes(frame).Single())??throw new InvalidDataException("Reloaded native triangle volume is unavailable.");
    }

    private static bool TriangleProbeContains(MeshData mesh,Vector2 point)
    {
        static float Cross(Vector2 a,Vector2 b)=>a.X*b.Y-a.Y*b.X;
        for(int i=0;i<mesh.Indices.Length;i+=3)
        {
            var p=mesh.Positions[mesh.Indices[i]];var q=mesh.Positions[mesh.Indices[i+1]];var r=mesh.Positions[mesh.Indices[i+2]];
            Vector2 a=new(p.X,p.Y),b=new(q.X,q.Y),c=new(r.X,r.Y);
            float x=Cross(b-a,point-a),y=Cross(c-b,point-b),z=Cross(a-c,point-c);
            if(x>=0&&y>=0&&z>=0||x<=0&&y<=0&&z<=0)return true;
        }
        return false;
    }

    private static void ValidateTriangleProbeMaterial(CompiledSceneDocument document,string name,Action<string,bool> check)
    {
        var graph=document.Graph;
        var scene=graph.Nodes.Single(n=>graph.Field(n,"m_Name")is{} field&&graph.Text(field)==name);
        var component=graph.Items(graph.Field(scene,"m_Components")).Select(graph.Resolve).OfType<CompiledElement>().Single(n=>n.TypeId==0x2b7b4775);
        var entry=graph.Items(graph.Field(component,"m_ElementDefs")).Single();
        var shape=graph.Resolve(graph.Field(entry,"m_ItemDesc"))??throw new InvalidDataException("Native triangle descriptor is unresolved.");
        check("authored triangle descriptor uses native type and physics material ID",shape.TypeId==0x7a0b5196&&graph.Integer(graph.Field(shape,"m_MaterialId")!)==103);
        var table=graph.Data(graph.Field(graph.Field(shape,"m_MatTable")!,"m_Materials")!);
        check("authored triangle native material table has one four-face material103 record",table.Length==32&&BinaryPrimitives.ReadUInt32LittleEndian(table)==1&&BinaryPrimitives.ReadUInt32LittleEndian(table[8..])==4&&BinaryPrimitives.ReadUInt32LittleEndian(table[24..])==103);
        ulong hash=graph.Integer(graph.Field(shape,"m_DataHash")!);int index=checked((int)graph.Integer(graph.Field(shape,"m_DataIndex")!));
        var physics=graph.Nodes.Single(n=>n.TypeId==0xaf6549f7&&graph.Integer(graph.Field(n,"m_Platform")!)==0&&graph.Integer(graph.Field(n,"m_Id")!)==hash);
        var cooked=HavokMeshData.ReadVariant(graph.Data(graph.Field(physics,"m_Data")!),index)??throw new InvalidDataException("Native triangle physics variant is unavailable.");
        check("authored triangle cooked material tags select valid palette zero",cooked.Materials.Length==4&&cooked.Materials.All(tag=>(tag&0x1ff)==0));
        HavokMeshTopologyWriter.ValidateTrees(graph.Data(graph.Field(physics,"m_Data")!),index);
        check("authored triangle native scalar BVHs validate",true);
    }

    private static void TestCompiledWorldCollision(Action<string,bool> check)
    {
        var worldArchive=new FileInfo(Path.Combine(MafiaEnvironment.CityFolder,"bc_bayou_collisions.sds"));
        var sceneArchive=new FileInfo(Path.Combine(MafiaEnvironment.CityFolder,"bc_bayou_block_a_lod0.sds"));
        if(!worldArchive.Exists||!sceneArchive.Exists)return;
        var document=new CompiledSceneDocument(sceneArchive);var meshes=document.WorldCollisions;
        string folder=SdsMeshLoader.EnsureExtracted(worldArchive);
        var snapshot=Directory.GetFiles(folder,"*.compiled").Append(Path.Combine(folder,"SDSContent.xml")).ToDictionary(p=>p,File.ReadAllBytes);
        byte[] archiveBytes=File.ReadAllBytes(worldArchive.FullName);IEditAction? action=null;bool applied=false;
        try
        {
            check("world collision native concave/primitive geometries and hardware instances",meshes.Count>=1800&&meshes.Sum(m=>m.Instances?.Length??1)>=70000);
            check("world collision meshes have valid instance matrices and triangle indices",meshes.All(m=>m.Instances is{Length:>0}&&m.Indices.Length%3==0&&m.Indices.All(i=>i<m.VertexCount)));
            var cached=new CompiledSceneDocument(sceneArchive);
            check("world collision cache shares canonical CPU geometry across documents",meshes.Count==cached.WorldCollisions.Count&&meshes.Zip(cached.WorldCollisions).All(p=>ReferenceEquals(p.First,p.Second)));
            int selected=-1;long revision=document.WorldCollisionRevision;
            for(int i=0;i<meshes.Count;i++)
            {
                if(!meshes[i].Name.StartsWith("World collision",StringComparison.Ordinal))continue;
                var positions=meshes[i].Positions;var min=positions.Aggregate(new Vector3(float.PositiveInfinity),Vector3.Min);var max=positions.Aggregate(new Vector3(float.NegativeInfinity),Vector3.Max);var center=(min+max)/2;
                action=document.ReplaceWorldCollisionPositions(i,positions.Select(p=>Vector3.Lerp(p,center,.02f)).ToArray(),out _);
                if(action is null)continue;selected=i;applied=true;break;
            }
            if(action is null)throw new InvalidOperationException("No triangle-only world collision variant is editable.");
            var changed=meshes[selected];check("world collision replacement increments shared revision",document.WorldCollisionRevision==revision+1&&cached.WorldCollisionRevision==revision+1);
            document.SaveWorkingCopy();
            check("world collision save queues dedicated companion archive",document.CompanionArchives.Any(a=>a.FullName.Equals(worldArchive.FullName,StringComparison.OrdinalIgnoreCase)));
            var reload=new CompiledWorldCollisionArchive(worldArchive);
            check("world collision save independently reloads exact recooked positions",reload.Meshes[selected].Positions.SequenceEqual(changed.Positions));
            var packed=SdsArchive.Pack(folder,MafiaEnvironment.Profile);
            using(var stream=new MemoryStream())
            {packed.Save(stream,new SdsWriteOptions{Compress=true});stream.Position=0;SdsArchive.Load(stream).Extract(folder);}
            var fresh=new CompiledWorldCollisionArchive(worldArchive);
            check("world collision compressed build/fresh extraction/reload",fresh.Meshes[selected].Positions.SequenceEqual(changed.Positions));
            action.Undo();applied=false;document.SaveWorkingCopy();
            check("world collision undo restores every native resource exactly",snapshot.Where(p=>Path.GetFileName(p.Key)!="SDSContent.xml").All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
            action.Redo();applied=true;document.SaveWorkingCopy();
            check("world collision redo updates revision and independently reloads",document.WorldCollisionRevision==revision+3&&new CompiledWorldCollisionArchive(worldArchive).Meshes[selected].Positions.SequenceEqual(changed.Positions));
            action.Undo();applied=false;document.SaveWorkingCopy();
            check("world collision redo/undo restores exact GENR bytes",snapshot.Where(p=>Path.GetFileName(p.Key)!="SDSContent.xml").All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
            check("world collision input SDS remains untouched",File.ReadAllBytes(worldArchive.FullName).SequenceEqual(archiveBytes));
        }
        finally
        {
            try{if(applied)action?.Undo();document.SaveWorkingCopy();}
            finally{foreach(var(path,bytes)in snapshot)File.WriteAllBytes(path,bytes);}
        }
    }
}
