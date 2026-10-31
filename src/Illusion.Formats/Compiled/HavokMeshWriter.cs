using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Recooks compressed mesh positions and all native bounds while preserving topology and fixups.</summary>
public static class HavokMeshWriter
{
    /// <summary>Replaces the reader's vertex array while preserving primitive layout, materials and shape keys.</summary>
    public static byte[] ReplacePositions(ReadOnlySpan<byte> input, int variantIndex, IReadOnlyList<Vector3> vertices)
    {
        var decoded = HavokMeshData.ReadSupported(input,[]); var file = new HavokMeshPackfile(input);
        _=HavokMeshData.ReadVariant(input,variantIndex);
        if ((uint)variantIndex >= decoded.Count || decoded[variantIndex] is not { } mesh || vertices.Count != mesh.Vertices.Length)
            throw new ArgumentException("Replacement must match a compressed mesh's decoded vertex array.", nameof(vertices));
        if (vertices.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))) throw new ArgumentException("Replacement vertices must be finite.", nameof(vertices));
        if (Enumerable.Range(0,vertices.Count).All(i => vertices[i] == mesh.Vertices[i])) return input.ToArray();
        int tree = checked(file.Pointer(file.Variants[variantIndex].Address + 96) + 16);
        var sections = file.Array(tree+64,96);var primitives=file.Array(tree+80,4);var sharedIndices=file.Array(tree+96,2);var packed=file.Array(tree+112,4);var shared=file.Array(tree+128,8);
        var targets=new Dictionary<int,Vector3>();int cursor=0;
        for(int s=0;s<sections.Count;s++)
        {
            int p=sections.Address+s*96;uint info=file.U(p+76),primitiveInfo=file.U(p+80);int localCount=(byte)info,localStart=checked((int)file.U(p+72)),sharedStart=(int)(info>>8),bank=file.Byte(p+92)<<16;
            for(int i=0;i<(byte)primitiveInfo;i++)
            {
                int q=primitives.Address+((int)(primitiveInfo>>8)+i)*4;byte a=file.Byte(q),b=file.Byte(q+1),c=file.Byte(q+2),d=file.Byte(q+3);
                if(a==222&&b==173&&c==222&&d==173)continue;
                foreach(byte id in c==d?new[]{a,b,c}:new[]{a,b,c,d})
                {
                    int address=id<localCount?packed.Address+(localStart+id)*4:shared.Address+(bank|file.U16(sharedIndices.Address+(sharedStart+id-localCount)*2))*8;
                    Vector3 value=vertices[cursor++];
                    if(targets.TryGetValue(address,out var old)&&old!=value)throw new ArgumentException("Replacement splits a shared native collision vertex; fixed-topology recooking cannot represent that split.",nameof(vertices));
                    targets[address]=value;
                }
            }
        }
        if(cursor!=vertices.Count)throw new InvalidDataException("Compressed replacement mapping disagrees with the independent reader.");
        return Recook(input,variantIndex,Matrix4x4.Identity,targets);
    }

    /// <summary>Applies a proper affine transform to one compressed shape. Returns independent packfile bytes.</summary>
    public static byte[] Transform(ReadOnlySpan<byte> input, int variantIndex, Matrix4x4 transform) => Recook(input,variantIndex,transform,null);

    private static byte[] Recook(ReadOnlySpan<byte> input, int variantIndex, Matrix4x4 transform, Dictionary<int,Vector3>? targets)
    {
        var decoded = HavokMeshData.ReadSupported(input,[]); var file = new HavokMeshPackfile(input);
        _=HavokMeshData.ReadVariant(input,variantIndex);
        if ((uint)variantIndex >= decoded.Count || decoded[variantIndex] is null) throw new ArgumentException("Variant is not a supported compressed triangle mesh.", nameof(variantIndex));
        float[] matrix = [transform.M11,transform.M12,transform.M13,transform.M14,transform.M21,transform.M22,transform.M23,transform.M24,transform.M31,transform.M32,transform.M33,transform.M34,transform.M41,transform.M42,transform.M43,transform.M44];
        if (matrix.Any(v => !float.IsFinite(v)) || transform.M14 != 0 || transform.M24 != 0 || transform.M34 != 0 || transform.M44 != 1 || transform.GetDeterminant() <= 0)
            throw new ArgumentException("Collision transform must be finite, affine, and preserve orientation.", nameof(transform));
        if (transform == Matrix4x4.Identity && targets is null) return input.ToArray();
        int tree = checked(file.Pointer(file.Variants[variantIndex].Address + 96) + 16);
        var sections = file.Array(tree + 64, 96); var packed = file.Array(tree + 112, 4); var shared = file.Array(tree + 128, 8);
        List<(int Start,int Length)> Ranges(int root)
        {
            var ranges=new List<(int,int)>{(root,160)};
            foreach(var(offset,stride)in new[]{(0,5),(64,96),(80,4),(96,2),(112,4),(128,8),(144,4),(168,112)})
            {var array=file.Array(root+offset,stride);if(array.Count!=0)ranges.Add((array.Address,checked(array.Count*stride)));}
            var parts=file.Array(root+64,96);for(int i=0;i<parts.Count;i++){var nodes=file.Array(parts.Address+i*96,4);if(nodes.Count!=0)ranges.Add((nodes.Address,checked(nodes.Count*4)));}
            return ranges;
        }
        var ownedRanges=Ranges(tree);
        foreach (var (type, address) in file.Variants)
        {
            if (type != "hknpCompressedMeshShape" || address == file.Variants[variantIndex].Address) continue;
            int other = checked(file.Pointer(address + 96) + 16);
            if (other == tree || Ranges(other).Any(b=>ownedRanges.Any(a=>(long)a.Start+a.Length>b.Start&&(long)b.Start+b.Length>a.Start)))
                throw new NotSupportedException("Collision shape geometry is shared by another variant; isolate it before recooking.");
        }
        byte[] result = input.ToArray();
        Vector3 V(int p) => new(file.F(p),file.F(p+4),file.F(p+8));
        void F(int p,float value) { if (!float.IsFinite(value)) throw new ArgumentException("Transformed collision exceeds finite bounds."); BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(p),value); }
        void Store(int p,Vector3 value) { F(p,value.X);F(p+4,value.Y);F(p+8,value.Z); }
        static (Vector3 Min,Vector3 Max) Bounds(Vector3 min,Vector3 max,Matrix4x4 m)
        {
            Vector3 a=new(float.PositiveInfinity),b=new(float.NegativeInfinity);
            for(int i=0;i<8;i++){var v=Vector3.Transform(new Vector3((i&1)==0?min.X:max.X,(i&2)==0?min.Y:max.Y,(i&4)==0?min.Z:max.Z),m);a=Vector3.Min(a,v);b=Vector3.Max(b,v);}
            return (a,b);
        }
        var oldMin=V(tree+16);var oldMax=V(tree+32);var global=Bounds(oldMin,oldMax,transform);
        if(targets is not null)foreach(var point in targets.Values)global=(Vector3.Min(global.Min,point),Vector3.Max(global.Max,point));
        // Inflate one float ULP to enclose arithmetic and quantization endpoints conservatively.
        global=(new(MathF.BitDecrement(global.Min.X),MathF.BitDecrement(global.Min.Y),MathF.BitDecrement(global.Min.Z)),new(MathF.BitIncrement(global.Max.X),MathF.BitIncrement(global.Max.Y),MathF.BitIncrement(global.Max.Z)));
        Store(tree+16,global.Min);Store(tree+32,global.Max);
        var oldSharedScale=(oldMax-oldMin)*new Vector3(1f/2097151,1f/2097151,1f/4194303);
        static uint Q(float value,float min,float max,uint limit)
        { if (!(max>min)) return 0;return (uint)Math.Clamp(Math.Round(((double)value-min)/(max-min)*limit),0,limit); }
        for(int i=0;i<shared.Count;i++)
        {
            int address=shared.Address+i*8;ulong value=file.U64(address);var v=targets?.GetValueOrDefault(address,oldMin+new Vector3(value&2097151,(value>>21)&2097151,value>>42)*oldSharedScale)??Vector3.Transform(oldMin+new Vector3(value&2097151,(value>>21)&2097151,value>>42)*oldSharedScale,transform);
            ulong encoded=Q(v.X,global.Min.X,global.Max.X,2097151)|((ulong)Q(v.Y,global.Min.Y,global.Max.Y,2097151)<<21)|((ulong)Q(v.Z,global.Min.Z,global.Max.Z,4194303)<<42);
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(shared.Address+i*8),encoded);
        }
        void FullBounds(int header,int stride)
        {
            var nodes=file.Array(header,stride);
            for(int i=0;i<nodes.Count;i++) result.AsSpan(nodes.Address+i*stride,3).Clear();
            Store(header+16,global.Min);Store(header+32,global.Max);
        }
        // Codec3Axis nibbles encode inward squared-distance offsets. Zero keeps the entire parent box.
        // Preserve all child/leaf bytes, obtaining a conservative, less selective hierarchy.
        FullBounds(tree,5);
        var written=new Dictionary<int,uint>();
        for(int s=0;s<sections.Count;s++)
        {
            int p=sections.Address+s*96;FullBounds(p,4);
            int start=checked((int)file.U(p+72)),count=(byte)file.U(p+76);var origin=V(p+48);var scale=V(p+60);
            var points=new Vector3[count];Vector3 min=new(float.PositiveInfinity),max=new(float.NegativeInfinity);
            for(int i=0;i<count;i++){int address=packed.Address+(start+i)*4;uint v=file.U(address);var old=origin+new Vector3(v&2047,(v>>11)&2047,v>>22)*scale;points[i]=targets?.GetValueOrDefault(address,old)??Vector3.Transform(old,transform);min=Vector3.Min(min,points[i]);max=Vector3.Max(max,points[i]);}
            if(count==0)continue;
            var step=(max-min)/new Vector3(2047,2047,1023);Store(p+48,min);Store(p+60,step);
            for(int i=0;i<count;i++)
            {
                var v=points[i];uint value=Q(v.X,min.X,max.X,2047)|(Q(v.Y,min.Y,max.Y,2047)<<11)|(Q(v.Z,min.Z,max.Z,1023)<<22);int address=packed.Address+(start+i)*4;
                if(written.TryGetValue(address,out uint prior)&&prior!=value)throw new NotSupportedException("Packed collision vertex is aliased across incompatible subtree quantizers.");
                written[address]=value;BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(address),value);
            }
        }
        // hkcdSimdTree::Node is six four-lane bounds vectors plus four child/leaf words (112 bytes).
        // Refit only occupied lanes; empty lanes retain native inverted sentinel bounds.
        var simd=file.Array(tree+168,112);
        for(int i=0;i<simd.Count;i++)for(int lane=0;lane<4;lane++)
        {
            int p=simd.Address+i*112+lane*4;var min=new Vector3(file.F(p),file.F(p+32),file.F(p+64));var max=new Vector3(file.F(p+16),file.F(p+48),file.F(p+80));
            if(min.X>max.X||min.Y>max.Y||min.Z>max.Z)continue;
            F(p,global.Min.X);F(p+16,global.Max.X);F(p+32,global.Min.Y);F(p+48,global.Max.Y);F(p+64,global.Min.Z);F(p+80,global.Max.Z);
        }
        HavokMeshBvhRefit.Refit(result,variantIndex);
        var after=HavokMeshData.ReadSupported(result,[]);
        if(after[variantIndex]!.Vertices.Any(v=>v.X<global.Min.X||v.Y<global.Min.Y||v.Z<global.Min.Z||v.X>global.Max.X||v.Y>global.Max.Y||v.Z>global.Max.Z))
            throw new InvalidDataException("Recooked collision vertices exceed conservative traversal bounds.");
        if(!decoded[variantIndex]!.Indices.AsSpan().SequenceEqual(after[variantIndex]!.Indices)||!decoded[variantIndex]!.Materials.AsSpan().SequenceEqual(after[variantIndex]!.Materials)||!decoded[variantIndex]!.ShapeKeys.AsSpan().SequenceEqual(after[variantIndex]!.ShapeKeys))
            throw new InvalidDataException("Recooked collision topology changed unexpectedly.");
        return result;
    }
}
