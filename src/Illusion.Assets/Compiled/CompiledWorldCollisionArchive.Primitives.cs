using System.Buffers.Binary;
using System.Numerics;
using Illusion.Assets.Collisions;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledWorldCollisionArchive
{
    private static void AppendPrimitives(CompiledGraph graph,CompiledElement root,IReadOnlyList<CompiledElement> triangleRecords,List<MeshData> output,List<string> warnings)
    {
        var groups=new Dictionary<CompiledElement,List<Matrix4x4>>();
        void Add(CompiledElement? shape,Matrix4x4 world)
        {
            if(shape is null){warnings.Add("Unresolved district primitive descriptor.");return;}
            if(!groups.TryGetValue(shape,out var list))groups.Add(shape,list=[]);
            list.Add(world);
        }
        foreach(var record in graph.Items(graph.Resolve(graph.Field(root,"m_PrimitiveStreamData"))))
        {
            var shape=graph.Resolve(graph.Field(record,"m_ElementDesc"));
            foreach(var placement in graph.Items(graph.Field(record,"m_TransformData")))
                if(graph.Field(placement,"m_WorldTransform")is{DataSize:48} matrix)
                    Add(shape,CompiledSceneDocument.ReadMatrix(graph.Data(matrix)));
                else throw new NotSupportedException("Unknown standalone primitive transform.");
        }
        foreach(var record in triangleRecords)
            foreach(var placement in graph.Items(graph.Field(record,"m_TransformData")))
            {
                var world=CompiledSceneDocument.ReadMatrix(graph.Data(graph.Field(placement,"m_Matrix")!));
                foreach(var primitive in graph.Items(graph.Field(placement,"m_BakedPrimitives")))
                {
                    if(graph.Field(primitive,"m_Data")is not{DataSize:64} data)throw new NotSupportedException("Unknown baked primitive placement.");
                    var raw=graph.Data(data);float scale=BinaryPrimitives.ReadSingleLittleEndian(raw[48..]);
                    if(!float.IsFinite(scale)||scale<=0)throw new InvalidDataException("Invalid baked primitive scale.");
                    Add(graph.Resolve(graph.Field(primitive,"m_ElementDesc")),Matrix4x4.CreateScale(scale)*CompiledSceneDocument.ReadMatrix(raw[..48])*world);
                }
            }
        foreach(var(shape,instances)in groups)
        {
            var geometry=PrimitiveGeometry(graph,shape);
            if(geometry is null){warnings.Add($"Unsupported district primitive {shape.TypeId:X8}/{shape.ObjectId:X16}.");continue;}
            var (points,indices)=geometry.Value;
            output.Add(new MeshData{Name=$"World primitive {shape.TypeId:X8}/{shape.ObjectId:X16}",World=Matrix4x4.Identity,Positions=points,Indices=indices,
                Normals=Enumerable.Repeat(Vector3.UnitZ,points.Length).ToArray(),Instances=instances.ToArray(),
                Parts=[new MeshPart(0,indices.Length,null,tint:new Vector4(.1f,.85f,1,.35f),blended:true)]});
        }
    }

    private static (Vector3[] Points,uint[] Indices)? PrimitiveGeometry(CompiledGraph graph,CompiledElement shape)
    {
        float Float(string name)=>graph.Field(shape,name)is{DataSize:4} value?BinaryPrimitives.ReadSingleLittleEndian(graph.Data(value)):float.NaN;
        if(shape.TypeId==0x63ee7798&&graph.Field(shape,"m_Size")is{DataSize:12} size)
        {
            var v=CompiledMeshDecoder.Vector(graph.Data(size));
            if(!float.IsFinite(v.X)||!float.IsFinite(v.Y)||!float.IsFinite(v.Z)||v.X<=0||v.Y<=0||v.Z<=0)throw new InvalidDataException("Invalid district box half-extents.");
            return ([new(-v.X,-v.Y,-v.Z),new(v.X,-v.Y,-v.Z),new(v.X,v.Y,-v.Z),new(-v.X,v.Y,-v.Z),new(-v.X,-v.Y,v.Z),new(v.X,-v.Y,v.Z),new(v.X,v.Y,v.Z),new(-v.X,v.Y,v.Z)],
                [0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7]);
        }
        if(shape.TypeId==0x48430eeb&&graph.Field(shape,"m_VertexBuffer")is{} buffer&&graph.Field(shape,"m_NumVertices")is{} countField)
        {
            int count=checked((int)graph.Integer(countField));var raw=graph.Data(buffer);
            if(count<4||raw.Length!=count*12)throw new NotSupportedException("Unknown district convex vertex layout.");
            var points=new Vector3[count];for(int i=0;i<count;i++)points[i]=CompiledMeshDecoder.Vector(raw.Slice(i*12,12));
            var hull=ConvexHull.Build(points,simplify:false);
            return hull is{} h?(h.Vertices,h.Triangles.Select(i=>(uint)i).ToArray()):null;
        }
        if(shape.TypeId is 0x4fd32ee0 or 0x7450434e)
        {
            float radius=Float("m_Radius"),height=shape.TypeId==0x4fd32ee0?Float("m_Height"):0;
            if(!float.IsFinite(radius)||!float.IsFinite(height)||radius<=0||height<0)throw new InvalidDataException("Invalid district sphere/capsule dimensions.");
            const int sides=12,rings=4;var points=new List<Vector3>();var indices=new List<uint>();
            for(int half=0;half<2;half++)for(int ring=0;ring<=rings;ring++)
            {
                float latitude=(half==0?-MathF.PI/2:0)+ring*MathF.PI/(2*rings),z=(half==0?-height/2:height/2)+radius*MathF.Sin(latitude),r=radius*MathF.Cos(latitude);
                for(int side=0;side<sides;side++){float a=side*2*MathF.PI/sides;points.Add(new(r*MathF.Cos(a),r*MathF.Sin(a),z));}
            }
            for(int ring=0;ring<2*(rings+1)-1;ring++)for(int side=0;side<sides;side++)
            {uint a=(uint)(ring*sides+side),b=(uint)(ring*sides+(side+1)%sides),c=a+sides,d=b+sides;indices.AddRange([a,b,d,a,d,c]);}
            return(points.ToArray(),indices.ToArray());
        }
        return null;
    }
}
