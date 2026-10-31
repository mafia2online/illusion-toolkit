using System.Numerics;

namespace Illusion.Formats.Compiled;

public sealed record HavokTriangleMesh(Vector3[] Vertices, uint[] Indices, ushort[] Materials, uint[] ShapeKeys);

/// <summary>Native compressed collision triangles, without a convex approximation.</summary>
public static class HavokMeshData
{
    public static IReadOnlyList<HavokTriangleMesh?> Read(ReadOnlySpan<byte> input) => ReadCore(input,null);

    /// <summary>Reads one editable triangle-only variant while other variants may contain custom primitives.</summary>
    public static HavokTriangleMesh? ReadVariant(ReadOnlySpan<byte> input,int variantIndex)
    {
        var warnings=new List<string>();var meshes=ReadSupported(input,warnings);
        if((uint)variantIndex>=meshes.Count)throw new ArgumentOutOfRangeException(nameof(variantIndex));
        if(warnings.Any(w=>w.StartsWith($"Compressed collision variant {variantIndex} ",StringComparison.Ordinal)))
            throw new NotSupportedException("Selected collision variant contains custom primitives and cannot be recooked.");
        return meshes[variantIndex];
    }

    /// <summary>Decodes available triangles, reporting custom primitives instead of approximating them.</summary>
    public static IReadOnlyList<HavokTriangleMesh?> ReadSupported(ReadOnlySpan<byte> input, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        return ReadCore(input,warnings);
    }

    private static IReadOnlyList<HavokTriangleMesh?> ReadCore(ReadOnlySpan<byte> input, ICollection<string>? warnings)
    {
        var file = new HavokMeshPackfile(input); var result = new List<HavokTriangleMesh?>();
        Vector3 V(int p) => new(file.F(p), file.F(p + 4), file.F(p + 8));
        static void Finite(Vector3 p)
        { if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) throw new InvalidDataException("Non-finite compressed collision vertex."); }
        foreach (var variant in file.Variants)
        {
            if (variant.Type != "hknpCompressedMeshShape") { result.Add(null); continue; }
            // Win64 hknpShape base has eight additional bytes versus the symbolized Mac build.
            file.CheckObject(variant.Address, 104); int tree = checked(file.Pointer(variant.Address + 96) + 16);
            file.CheckObject(tree, 160);
            var subtrees = file.Array(tree + 64, 96); var primitives = file.Array(tree + 80, 4);
            var sharedIndices = file.Array(tree + 96, 2); var packed = file.Array(tree + 112, 4);
            var shared = file.Array(tree + 128, 8); var runs = file.Array(tree + 144, 4);
            if(Enumerable.Range(0,primitives.Count).Any(i=>file.Byte(primitives.Address+i*4+1)==file.Byte(primitives.Address+i*4+2)&&file.Byte(primitives.Address+i*4+2)==file.Byte(primitives.Address+i*4+3)))
            {
                string warning=$"Compressed collision variant {result.Count} contains unsupported custom primitives.";
                if(warnings is null)throw new NotSupportedException(warning);
                warnings.Add(warning);
                bool triangles=Enumerable.Range(0,primitives.Count).Any(i=>
                {
                    int p=primitives.Address+i*4;byte a=file.Byte(p),b=file.Byte(p+1),c=file.Byte(p+2),d=file.Byte(p+3);
                    return !(b==c&&c==d)&&!(a==222&&b==173&&c==222&&d==173);
                });
                if(!triangles){result.Add(null);continue;}
            }
            Vector3 min = V(tree + 16), max = V(tree + 32); Finite(min); Finite(max);
            if (min.X > max.X || min.Y > max.Y || min.Z > max.Z) throw new InvalidDataException("Invalid compressed collision bounds.");
            var sharedScale = (max - min) * new Vector3(1f / 2097151, 1f / 2097151, 1f / 4194303);
            var vertices = new List<Vector3>(); var indices = new List<uint>(); var materials = new List<ushort>(); var keys = new List<uint>();
            for (int s = 0; s < subtrees.Count; s++)
            {
                if (s > 0xffffff) throw new InvalidDataException("Compressed collision subtree key overflow.");
                int p = subtrees.Address + s * 96; uint localStart = file.U(p + 72), vertexData = file.U(p + 76), primitiveData = file.U(p + 80), runData = file.U(p + 84);
                int localCount = (byte)vertexData, sharedStart = (int)(vertexData >> 8), first = (int)(primitiveData >> 8), count = (byte)primitiveData;
                int runStart = (int)(runData >> 8), runCount = (byte)runData, bank = file.Byte(p + 92) << 16;
                if ((ulong)localStart + (uint)localCount > (uint)packed.Count || first > primitives.Count - count || runStart > runs.Count - runCount || sharedStart > sharedIndices.Count)
                    throw new InvalidDataException("Compressed collision subtree array range is invalid.");
                Vector3 origin = V(p + 48), scale = V(p + 60);
                Vector3 Decode(byte id)
                {
                    Vector3 v;
                    if (id < localCount)
                    {
                        Finite(origin);Finite(scale);
                        uint value = file.U(packed.Address + checked((int)(localStart + id) * 4));
                        v = origin + new Vector3(value & 2047, (value >> 11) & 2047, value >> 22) * scale;
                    }
                    else
                    {
                        int slot = checked(sharedStart + id - localCount);
                        if ((uint)slot >= sharedIndices.Count) throw new InvalidDataException("Compressed collision shared vertex slot is invalid.");
                        int index = bank | file.U16(sharedIndices.Address + slot * 2);
                        if ((uint)index >= shared.Count) throw new InvalidDataException("Compressed collision shared vertex index is invalid.");
                        ulong value = file.U64(shared.Address + index * 8);
                        v = min + new Vector3(value & 2097151, (value >> 21) & 2097151, value >> 42) * sharedScale;
                    }
                    Finite(v); return v;
                }
                for (int i = 0; i < count; i++)
                {
                    int q = primitives.Address + (first + i) * 4; byte a = file.Byte(q), b = file.Byte(q + 1), c = file.Byte(q + 2), d = file.Byte(q + 3);
                    // Repeated B/C/D invokes the engine's custom-primitive converter, not triangle decoding.
                    if (b == c && c == d)
                    {
                        if(warnings is null)throw new NotSupportedException("Compressed mesh contains custom collision primitives.");
                        continue;
                    }
                    if (a == 222 && b == 173 && c == 222 && d == 173) continue;
                    ushort? material = null;
                    for (int r = 0; r < runCount; r++)
                    {
                        int run = runs.Address + (runStart + r) * 4, start = file.Byte(run + 2), n = file.Byte(run + 3);
                        if (i >= start && i - start < n)
                        { if (material.HasValue) throw new InvalidDataException("Overlapping collision material runs."); material = file.U16(run); }
                    }
                    if (!material.HasValue) throw new InvalidDataException("Missing collision material run.");
                    uint index = checked((uint)vertices.Count); vertices.Add(Decode(a)); vertices.Add(Decode(b)); vertices.Add(Decode(c));
                    indices.Add(index); indices.Add(index + 1); indices.Add(index + 2); materials.Add(material.Value); keys.Add(((uint)s << 8) | (uint)(i * 2));
                    if (c != d)
                    { vertices.Add(Decode(d)); indices.Add(index); indices.Add(index + 2); indices.Add(index + 3); materials.Add(material.Value); keys.Add(((uint)s << 8) | (uint)(i * 2 + 1)); }
                }
            }
            result.Add(indices.Count==0?null:new(vertices.ToArray(), indices.ToArray(), materials.ToArray(), keys.ToArray()));
        }
        return result;
    }
}
