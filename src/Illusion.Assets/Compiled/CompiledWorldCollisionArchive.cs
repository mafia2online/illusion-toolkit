using System.Buffers.Binary;
using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Read-only collision placements from the game's dedicated district _collisions archives.</summary>
public sealed partial class CompiledWorldCollisionArchive
{
    public FileInfo Archive { get; }
    public IReadOnlyList<MeshData> Meshes { get; }
    public IReadOnlyList<string> Warnings { get; }
    /// <summary>Extracted source resources owned by Archive; reload these into an isolated editing session.</summary>
    public IReadOnlyList<string> ResourcePaths { get; }

    public CompiledWorldCollisionArchive(FileInfo archive)
    {
        Archive=archive;var meshes=new List<MeshData>();var warnings=new List<string>();
        string folder=SdsMeshLoader.EnsureExtracted(archive);
        ResourcePaths=Directory.GetFiles(folder,"*.compiled");
        foreach(string path in ResourcePaths)
        {
            var graph=new CompiledGraph([CompiledResource.Load(path)]);
            foreach(var root in graph.Nodes.Where(n=>n.TypeId==0x68985aab))
            {
                var stream=graph.Resolve(graph.Field(root,"m_StreamData"));
                var physics=graph.Resolve(graph.Field(root,"m_PhysicsDataContainer"));
                if(stream?.TypeId!=0x45333dfd||physics?.TypeId!=0xaf6549f7||graph.Field(physics,"m_Data")is not{} data||graph.Field(physics,"m_Platform")is not{} platform||graph.Integer(platform)!=0)
                    throw new NotSupportedException("District collision stream has an unsupported data container.");
                var shapes=HavokMeshData.ReadSupported(graph.Data(data),warnings);
                int first=checked((int)graph.Integer(graph.Field(root,"m_TriMeshStartDataIndex")!));
                var records=graph.Items(graph.Field(stream,"m_TriangleMeshData"));
                if(first<0||first>shapes.Count-records.Count)throw new InvalidDataException("District triangle stream exceeds its cooked variants.");
                var instances=new Dictionary<int,List<Matrix4x4>>();
                void Add(int index,Matrix4x4 matrix)
                {
                    if((uint)index>=records.Count)throw new InvalidDataException("Baked collision references an invalid triangle stream index.");
                    if(!instances.TryGetValue(index,out var list))instances.Add(index,list=[]);
                    list.Add(matrix);
                }
                for(int i=0;i<records.Count;i++)
                {
                    var record=records[i];if(record.TypeId!=0x11247aba)throw new NotSupportedException("Unknown district triangle stream record.");
                    foreach(var placement in graph.Items(graph.Field(record,"m_TransformData")))
                    {
                        if(placement.TypeId!=0x33048ccb||graph.Field(placement,"m_Matrix")is not{DataSize:48} matrixField)
                            throw new NotSupportedException("Unknown district triangle placement layout.");
                        var world=CompiledSceneDocument.ReadMatrix(graph.Data(matrixField));Add(i,world);
                        if(graph.Field(placement,"m_BakedTriangleMeshes")is not{} bakedField)continue;
                        var baked=graph.Data(bakedField);
                        if(baked.Length<4)throw new InvalidDataException("Truncated baked collision array.");
                        uint count=BinaryPrimitives.ReadUInt32LittleEndian(baked);
                        if(count>(baked.Length-4)/56||baked.Length!=4+(long)count*56)throw new InvalidDataException("Invalid baked collision array extent.");
                        for(int j=0;j<count;j++)
                        {
                            var entry=baked.Slice(4+j*56,56);int index=BinaryPrimitives.ReadInt32LittleEndian(entry[52..]);
                            Add(index,CompiledSceneDocument.ReadMatrix(entry[..48])*world);
                        }
                    }
                }
                foreach(var(index,placements)in instances)
                {
                    if(shapes[first+index]is not{} shape)continue;
                    var normals=new Vector3[shape.Vertices.Length];
                    for(int t=0;t<shape.Indices.Length;t+=3)
                    {
                        int a=(int)shape.Indices[t],b=(int)shape.Indices[t+1],c=(int)shape.Indices[t+2];
                        var normal=Vector3.Cross(shape.Vertices[b]-shape.Vertices[a],shape.Vertices[c]-shape.Vertices[a]);
                        normals[a]+=normal;normals[b]+=normal;normals[c]+=normal;
                    }
                    for(int n=0;n<normals.Length;n++)normals[n]=normals[n].LengthSquared()>1e-12f?Vector3.Normalize(normals[n]):Vector3.UnitZ;
                    var mesh=new MeshData
                    {
                        Name=$"World collision {archive.Name}:{index}",World=Matrix4x4.Identity,
                        Positions=shape.Vertices,Indices=shape.Indices,Normals=normals,Instances=placements.ToArray(),
                        Parts=[new MeshPart(0,shape.Indices.Length,null,tint:new Vector4(.1f,.85f,1,.35f),blended:true)]
                    };
                    meshes.Add(mesh);RegisterEditableMesh(mesh,graph.Owner(physics),data,first+index);
                }
                AppendPrimitives(graph,root,records,meshes,warnings);
            }
        }
        Meshes=meshes;Warnings=warnings;
    }
}
