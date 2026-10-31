using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Refits original native scalar/SIMD hierarchies without changing their leaf or child maps.</summary>
internal static class HavokMeshBvhRefit
{
    private readonly record struct Box(Vector3 Min,Vector3 Max)
    {
        public bool Contains(Box other)=>Min.X<=other.Min.X&&Min.Y<=other.Min.Y&&Min.Z<=other.Min.Z&&Max.X>=other.Max.X&&Max.Y>=other.Max.Y&&Max.Z>=other.Max.Z;
        public static Box Union(Box a,Box b)=>new(Vector3.Min(a.Min,b.Min),Vector3.Max(a.Max,b.Max));
    }
    private sealed record Node(int Index,Box Actual,Node? Left=null,Node? Right=null);
    public static void Refit(byte[] data,int variant)
    {
        var file=new HavokMeshPackfile(data);var geometry=HavokMeshData.ReadVariant(data,variant)??throw new InvalidDataException("Refit needs native triangle geometry.");
        int tree=file.Pointer(file.Variants[variant].Address+96)+16;
        Vector3 V(int p)=>new(file.F(p),file.F(p+4),file.F(p+8));
        Box Header(int p)=>new(V(p+16),V(p+32));
        void F(int p,float value)=>BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(p),value);
        void Store(int p,Box box){F(p,box.Min.X);F(p+4,box.Min.Y);F(p+8,box.Min.Z);F(p+16,box.Max.X);F(p+20,box.Max.Y);F(p+24,box.Max.Z);}
        var primitives=new Dictionary<uint,Box>();
        for(int i=0;i<geometry.ShapeKeys.Length;i++)
        {
            uint key=geometry.ShapeKeys[i]&~1u;Vector3 a=geometry.Vertices[geometry.Indices[i*3]],b=geometry.Vertices[geometry.Indices[i*3+1]],c=geometry.Vertices[geometry.Indices[i*3+2]];
            var box=new Box(Vector3.Min(a,Vector3.Min(b,c)),Vector3.Max(a,Vector3.Max(b,c)));
            primitives[key]=primitives.TryGetValue(key,out var old)?Box.Union(old,box):box;
        }
        var sections=file.Array(tree+64,96);var sectionBounds=new Dictionary<int,Box>();
        var scalarTrees=new List<(int Header,int Address,int Stride,Node Root,Box Parent)>();
        Node Build(int address,int count,int stride,Func<int,Box> leaf)
        {
            var seen=new HashSet<int>();
            Node Visit(int index)
            {
                if((uint)index>=count||!seen.Add(index))throw new InvalidDataException("Native collision hierarchy contains invalid child links or cycles.");
                int p=address+index*stride;int value=stride==4?file.Byte(p+3):((file.Byte(p+3)&127)<<8)|file.Byte(p+4);
                bool branch=stride==4?(value&1)!=0:(file.Byte(p+3)&128)!=0;
                if(!branch)return new(index,leaf(stride==4?value>>1:value));
                int jump=stride==4?value&254:value*2;
                var left=Visit(index+1);var right=Visit(index+jump);return new(index,Box.Union(left.Actual,right.Actual),left,right);
            }
            var root=Visit(0);if(seen.Count!=count)throw new NotSupportedException("Native collision hierarchy has unreachable serialized nodes.");return root;
        }
        var global=Header(tree);
        for(int s=0;s<sections.Count;s++)
        {
            int section=sections.Address+s*96;var nodes=file.Array(section,4);
            if(nodes.Count==0)throw new NotSupportedException("An empty native subtree cannot be tightly refitted.");
            int sectionIndex=s;
            var root=Build(nodes.Address,nodes.Count,4,primitive=>primitives.TryGetValue(((uint)sectionIndex<<8)|(uint)(primitive*2),out var box)?box:throw new NotSupportedException("Native subtree leaf is not a decoded triangle primitive."));
            var bounds=root.Actual;
            // Padding remains inside the shared quantizer's global bounds.
            bounds=new(Vector3.Max(global.Min,new Vector3(MathF.BitDecrement(bounds.Min.X),MathF.BitDecrement(bounds.Min.Y),MathF.BitDecrement(bounds.Min.Z))),
                Vector3.Min(global.Max,new Vector3(MathF.BitIncrement(bounds.Max.X),MathF.BitIncrement(bounds.Max.Y),MathF.BitIncrement(bounds.Max.Z))));
            if(!bounds.Contains(root.Actual))throw new InvalidDataException("Global collision quantizer does not enclose its subtree.");
            Store(section+16,bounds);sectionBounds.Add(s,bounds);scalarTrees.Add((section,nodes.Address,4,root,bounds));
        }
        var top=file.Array(tree,5);
        var topRoot=Build(top.Address,top.Count,5,index=>sectionBounds.TryGetValue(index,out var box)?box:throw new InvalidDataException("Native top leaf references an absent subtree."));
        scalarTrees.Add((tree,top.Address,5,topRoot,global));
        foreach(var hierarchy in scalarTrees)
        {
            void Emit(Node node,Box parent,bool root)
            {
                int at=hierarchy.Address+node.Index*hierarchy.Stride;
                var codes=root?new byte[3]:Pack(parent,node.Actual);
                codes.CopyTo(data,at);var decoded=root?parent:Unpack(parent,codes);
                if(!decoded.Contains(node.Actual))throw new InvalidDataException("Refitted scalar node misses descendant geometry.");
                if(node.Left is{} left){Emit(left,decoded,false);Emit(node.Right!,decoded,false);}
            }
            Emit(hierarchy.Root,hierarchy.Parent,true);
        }
        var simd=file.Array(tree+168,112);var done=new Dictionary<int,Box>();var active=new HashSet<int>();
        Box Simd(int index)
        {
            if(done.TryGetValue(index,out var prior))return prior;
            if((uint)index>=simd.Count||!active.Add(index))throw new InvalidDataException("Native SIMD collision hierarchy contains invalid child links or cycles.");
            Box? combined=null;
            for(int lane=0;lane<4;lane++)
            {
                int p=simd.Address+index*112+lane*4;
                var old=new Box(new(file.F(p),file.F(p+32),file.F(p+64)),new(file.F(p+16),file.F(p+48),file.F(p+80)));
                if(old.Min.X>old.Max.X||old.Min.Y>old.Max.Y||old.Min.Z>old.Max.Z)continue;
                uint child=file.U(simd.Address+index*112+96+lane*4);
                var bounds=(child&1)!=0?primitives.TryGetValue((child>>1)&~1u,out var box)?box:throw new NotSupportedException("SIMD leaf is not a decoded collision primitive."):Simd(checked((int)(child>>1)));
                F(p,bounds.Min.X);F(p+16,bounds.Max.X);F(p+32,bounds.Min.Y);F(p+48,bounds.Max.Y);F(p+64,bounds.Min.Z);F(p+80,bounds.Max.Z);
                combined=combined.HasValue?Box.Union(combined.Value,bounds):bounds;
            }
            active.Remove(index);var result=combined??throw new NotSupportedException("Referenced SIMD node has no occupied bounds lanes.");done.Add(index,result);return result;
        }
        for(int index=0;index<simd.Count;index++)
            if(Enumerable.Range(0,4).Any(l=>file.F(simd.Address+index*112+l*4)<=file.F(simd.Address+index*112+16+l*4)))Simd(index);
        // Decode freshly written bytes independently, preserving the original child/leaf topology.
        var fresh=new HavokMeshPackfile(data);
        foreach(var hierarchy in scalarTrees)
        {
            void Validate(Node node,Box parent,bool root)
            {
                int p=hierarchy.Address+node.Index*hierarchy.Stride;byte[] codes=[fresh.Byte(p),fresh.Byte(p+1),fresh.Byte(p+2)];
                var bounds=root?parent:Unpack(parent,codes);
                if(!bounds.Contains(node.Actual))throw new InvalidDataException("Serialized collision bounds miss descendant geometry.");
                if(node.Left is{} left){Validate(left,bounds,false);Validate(node.Right!,bounds,false);}
            }
            Validate(hierarchy.Root,hierarchy.Parent,true);
        }
    }

    private static Box Unpack(Box parent,ReadOnlySpan<byte> codes)
    {
        var step=(parent.Max-parent.Min)*(1f/226f);
        return new(parent.Min+new Vector3((codes[0]>>4)*(codes[0]>>4),(codes[1]>>4)*(codes[1]>>4),(codes[2]>>4)*(codes[2]>>4))*step,
            parent.Max-new Vector3((codes[0]&15)*(codes[0]&15),(codes[1]&15)*(codes[1]&15),(codes[2]&15)*(codes[2]&15))*step);
    }
    private static byte[] Pack(Box parent,Box actual)
    {
        if(!parent.Contains(actual))throw new InvalidDataException("Collision child geometry exceeds its decoded parent.");
        byte[] result=new byte[3];
        for(int axis=0;axis<3;axis++)
        {
            for(int nibble=1;nibble<=15;nibble++){result[axis]=(byte)(nibble<<4);if(!Unpack(parent,result).Contains(actual)){result[axis]=(byte)((nibble-1)<<4);break;}}
            int high=result[axis]&240;
            for(int nibble=1;nibble<=15;nibble++){result[axis]=(byte)(high|nibble);if(!Unpack(parent,result).Contains(actual)){result[axis]=(byte)(high|(nibble-1));break;}}
        }
        return result;
    }
}
