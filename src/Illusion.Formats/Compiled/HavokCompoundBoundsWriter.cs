using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Refits Win64 Havok dynamic-compound bounds after a referenced compressed mesh changes.
/// Child instance IDs, transforms, tree links and shape-key maps remain exact.</summary>
public static class HavokCompoundBoundsWriter
{
    public readonly record struct Bounds(Vector3 Min, Vector3 Max)
    {
        public static Bounds Union(Bounds a, Bounds b) => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));
        public bool Contains(Bounds b) => Min.X <= b.Min.X && Min.Y <= b.Min.Y && Min.Z <= b.Min.Z && Max.X >= b.Max.X && Max.Y >= b.Max.Y && Max.Z >= b.Max.Z;
    }
    public sealed record Result(byte[] Bytes, int Compounds, int Instances);

    /// <summary>Native hkAabbUtil::calcAabb ordering, including hknpShapeInstance's separate scale.</summary>
    internal static Bounds Transform(Bounds box, ReadOnlySpan<byte> instance)
    {
        var scale = ReadVector(instance[64..]); var x = ReadVector(instance) * scale.X;
        var y = ReadVector(instance[16..]) * scale.Y; var z = ReadVector(instance[32..]) * scale.Z;
        var center = (box.Min + box.Max) * .5f; var half = (box.Max - box.Min) * .5f;
        var extent = (Vector3.Abs(x * half.X) + Vector3.Abs(y * half.Y)) + Vector3.Abs(z * half.Z);
        var translated = ((x * center.X + y * center.Y) + z * center.Z) + ReadVector(instance[48..]);
        var result = new Bounds(translated - extent, translated + extent);
        if (!Finite(result.Min) || !Finite(result.Max)) throw new InvalidDataException("Non-finite compound instance bounds.");
        return result;
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static Vector3 ReadVector(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));

    public static Result Refit(ReadOnlySpan<byte> original, ReadOnlySpan<byte> recooked, int variantIndex)
    {
        if (original.Length != recooked.Length) throw new NotSupportedException("Compound refitting requires unchanged packfile storage extents.");
        var before = new HavokMeshPackfile(original); var file = new HavokMeshPackfile(recooked);
        if ((uint)variantIndex >= file.Variants.Count || file.Variants[variantIndex].Type != "hknpCompressedMeshShape")
            throw new ArgumentOutOfRangeException(nameof(variantIndex));
        if (!before.Variants.SequenceEqual(file.Variants) || file.Variants.Any(v => v.Type is not
            ("hknpCompressedMeshShape" or "hknpDynamicCompoundShape" or "hknpConvexPolytopeShape")))
            throw new NotSupportedException("Unknown compound ownership or changed root variants.");
        byte[] output = recooked.ToArray();
        Vector3 Vector(int address) => new(file.F(address), file.F(address + 4), file.F(address + 8));
        Bounds Box(int address) => new(Vector(address), Vector(address + 16));
        void Store(int address, Bounds box)
        {
            if (!Finite(box.Min) || !Finite(box.Max) || Vector3.Min(box.Min, box.Max) != box.Min) throw new InvalidDataException("Invalid compound bounds.");
            void Write(int at, Vector3 v)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(at), v.X); BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(at + 4), v.Y);
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(at + 8), v.Z);
            }
            Write(address, box.Min); Write(address + 16, box.Max);
        }
        int shape = file.Variants[variantIndex].Address;
        int tree = file.Pointer(shape + 96) + 16;
        var changed = new Dictionary<int, Bounds> { [shape] = Box(tree + 16) };
        var processed = new HashSet<int>(); var active = new HashSet<int>(); int compounds = 0, instances = 0;
        var compoundAddresses = file.Variants.Where(v => v.Type == "hknpDynamicCompoundShape").Select(v => v.Address).ToHashSet();
        var knownShapes = file.Variants.Select(v => v.Address).ToHashSet();
        bool Visit(int compound)
        {
            if (processed.Contains(compound)) return changed.ContainsKey(compound);
            if (!active.Add(compound)) throw new InvalidDataException("Compound ownership cycle.");
            if (active.Count > 256) throw new NotSupportedException("Compound ownership exceeds the supported nesting depth.");
            file.CheckObject(compound, 200);
            var children = file.Array(compound + 96, 128);
            if (children.Count == 0) { active.Remove(compound); processed.Add(compound); return false; }
            int treeObject = file.Pointer(compound + 192); file.CheckObject(treeObject, 56);
            var nodes = file.Array(treeObject + 16, 32); int root = file.U16(treeObject + 48);
            if (nodes.Count > 65536) throw new NotSupportedException("Compound tree exceeds its 16-bit node links.");
            if (root == 0 || root >= nodes.Count) throw new InvalidDataException("Invalid compound BVH root.");
            var updates = new Dictionary<int, Bounds>(); var enabled = new Dictionary<int, bool>();
            for (int i = 0; i < children.Count; i++)
            {
                int instance = children.Address + i * 128, child = file.Pointer(instance + 80);
                if (!knownShapes.Contains(child)) throw new NotSupportedException("An unnamed compound child needs explicit ownership validation before geometry expansion.");
                if (compoundAddresses.Contains(child)) Visit(child);
                if (!changed.TryGetValue(child, out var childBounds)) continue;
                int leaf = file.U16(instance + 60);
                if (leaf <= 0 || leaf >= nodes.Count) throw new InvalidDataException("Compound instance has an invalid BVH leaf.");
                int at = nodes.Address + leaf * 32;
                if (file.U16(at + 28) != 0 || file.U16(at + 30) != i) throw new InvalidDataException("Compound leaf does not identify its instance.");
                // Composite shapes use hknpShapeInstance's direct affine scale path. Convex-only
                // radius/scale correction is intentionally outside this compressed-mesh edit mode.
                if (file.Byte(child + 19) == 1) throw new NotSupportedException("A changed convex instance needs native scaling-parameter reconstruction.");
                var transformed = Transform(childBounds, output.AsSpan(instance, 128));
                updates.Add(leaf, Bounds.Union(Box(at), transformed)); enabled.Add(leaf, (file.U(instance + 12) & 0x40) != 0);
                instances++;
            }
            if (updates.Count > 0)
            {
                var seen = new HashSet<int>();
                Bounds RefitNode(int index, int parent)
                {
                    if (index <= 0 || index >= nodes.Count || !seen.Add(index)) throw new InvalidDataException("Invalid compound BVH links.");
                    int at = nodes.Address + index * 32;
                    if (file.U16(at + 12) != parent) throw new InvalidDataException("Compound BVH parent mismatch.");
                    int left = file.U16(at + 28), right = file.U16(at + 30); Bounds result;
                    if (left == 0) result = updates.GetValueOrDefault(index, Box(at));
                    else result = Bounds.Union(RefitNode(left, index), RefitNode(right, index));
                    Store(at, result); return result;
                }
                RefitNode(root, 0);
                var bounds = Box(compound + 128);
                foreach (var (leaf, box) in updates) if (enabled[leaf]) bounds = Bounds.Union(bounds, box);
                Store(compound + 128, bounds); changed.Add(compound, bounds); compounds++;
            }
            active.Remove(compound); processed.Add(compound); return updates.Count > 0;
        }
        foreach (int compound in compoundAddresses) Visit(compound);
        return new(output, compounds, instances);
    }
}
