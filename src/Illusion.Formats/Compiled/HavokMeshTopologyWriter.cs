using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Native triangle cooker with at most 128 primitives and 255 local vertices per subtree.
/// Rebuilds scalar BVHs, material runs, quantization, bitfields and fixups; preserves the native palette.</summary>
public static class HavokMeshTopologyWriter
{
    private sealed record Part(int First, uint[] Vertices, byte[] Indices, Vector3 Min, Vector3 Max, Vector3 Step,
        uint[] Packed, Vector3[] Decoded, ushort[] Shared, byte Bank);
    private readonly record struct Bounds(Vector3 Min, Vector3 Max);
    private static Bounds Union(Bounds a, Bounds b) => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));
    private static Bounds PointBounds(IEnumerable<Vector3> points)
        => new(points.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min), points.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max));

    public static byte[] Replace(ReadOnlySpan<byte> template, int variantIndex, IReadOnlyList<Vector3> vertices,
        IReadOnlyList<uint> indices, IReadOnlyList<ushort>? nativeMaterials = null)
    {
        var file = new HavokMeshPackfile(template);
        var old = HavokMeshData.ReadVariant(template, variantIndex) ?? throw new ArgumentException("A triangle-only mesh template is required.", nameof(variantIndex));
        int triangles = indices.Count / 3;
        if (vertices.Count is < 3 or > 100000 || indices.Count % 3 != 0 || triangles is < 1 or > 100000 ||
            vertices.Any(v => !Finite(v)) || indices.Any(i => i >= vertices.Count))
            throw new ArgumentException("The cooker supports 3–100000 finite vertices and 1–100000 indexed triangles.");
        if (nativeMaterials is not null && nativeMaterials.Count != triangles) throw new ArgumentException("One native material tag is required per triangle.", nameof(nativeMaterials));
        for (int i = 0; i < indices.Count; i += 3)
        {
            float area = Vector3.Cross(vertices[(int)indices[i + 1]] - vertices[(int)indices[i]], vertices[(int)indices[i + 2]] - vertices[(int)indices[i]]).LengthSquared();
            if (!(area > 0) || !float.IsFinite(area)) throw new ArgumentException("An input triangle is degenerate or exceeds the finite area range.");
        }
        var welded = new List<Vector3>(); var positions = new Dictionary<Vector3, uint>(); var remap = new uint[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            if (!positions.TryGetValue(vertices[i], out uint id)) { id = (uint)welded.Count; positions.Add(vertices[i], id); welded.Add(vertices[i]); }
            remap[i] = id;
        }
        vertices = welded; indices = indices.Select(i => remap[i]).ToArray();
        var materials = nativeMaterials?.ToArray() ?? Enumerable.Repeat(old.Materials[0], triangles).ToArray();
        var allowed = old.Materials.ToHashSet();
        if (materials.Any(m => !allowed.Contains(m))) throw new NotSupportedException("New native material tags require authoring a matching material palette.");
        int shape = file.Variants[variantIndex].Address;
        if (file.Variants.Where((_, i) => i != variantIndex).Any(v => v.Address == shape)) throw new NotSupportedException("The selected shape is shared by multiple root variants.");
        file.CheckObject(shape, 160);
        if (materials.Any(m => m != ushort.MaxValue))
        {
            var entries = file.Array(file.Pointer(shape + 32), 16);
            var palettes = Enumerable.Range(0, entries.Count).Select(i => entries.Address + i * 16).Where(at => file.U(at + 8) == 0xf101).ToArray();
            if (palettes.Length != 1) throw new NotSupportedException("A unique native contact material palette is required.");
            var contacts = file.Array(file.Pointer(palettes[0]) + 16, 24);
            if (materials.Any(m => m != ushort.MaxValue && (m & 0x1ff) >= contacts.Count)) throw new InvalidDataException("A native material run references an absent palette entry.");
        }
        int originalTree = file.Pointer(shape + 96), tree = originalTree + 16;
        var simd = file.Array(tree + 168, 112);
        if (simd.Count != 2 || Enumerable.Range(0, 2).Any(n => Enumerable.Range(0, 4).Any(l =>
            file.F(simd.Address + n * 112 + l * 4) <= file.F(simd.Address + n * 112 + 16 + l * 4) || file.U(simd.Address + n * 112 + 96 + l * 4) != 0)))
            throw new NotSupportedException("Topology cooking currently requires the stock optional-SIMD empty-tree mode.");
        var parts = new List<Part>();
        for (int first = 0; first < triangles;)
        {
            var map = new Dictionary<uint, byte>(); var ids = new List<uint>(); var local = new List<byte>();
            int end = first;
            while (end < triangles && end - first < 128)
            {
                var next = new[] { indices[end * 3], indices[end * 3 + 1], indices[end * 3 + 2] };
                if (map.Count + next.Distinct().Count(id => !map.ContainsKey(id)) > 255) break;
                foreach (uint id in next)
                {
                    if (!map.TryGetValue(id, out byte index)) { index = checked((byte)ids.Count); map.Add(id, index); ids.Add(id); }
                    local.Add(index);
                }
                end++;
            }
            var points = ids.Select(id => vertices[(int)id]).ToArray();
            var min = points.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min);
            var max = points.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max);
            min = new(MathF.BitDecrement(min.X), MathF.BitDecrement(min.Y), MathF.BitDecrement(min.Z));
            max = new(MathF.BitIncrement(max.X), MathF.BitIncrement(max.Y), MathF.BitIncrement(max.Z));
            var step = (max - min) / new Vector3(2047, 2047, 1023);
            if (!Finite(min) || !Finite(max) || !Finite(step)) throw new ArgumentException("Collision quantization range is too large.");
            static uint Q(float value, float origin, float scale, uint limit) => scale == 0 ? 0 : (uint)Math.Clamp(Math.Round(((double)value - origin) / scale), 0, limit);
            var packed = points.Select(v => Q(v.X, min.X, step.X, 2047) | (Q(v.Y, min.Y, step.Y, 2047) << 11) | (Q(v.Z, min.Z, step.Z, 1023) << 22)).ToArray();
            var decoded = packed.Select(v => min + new Vector3(v & 2047, (v >> 11) & 2047, v >> 22) * step).ToArray();
            parts.Add(new(first, ids.ToArray(), local.ToArray(), min, max, step, packed, decoded, [], 0)); first = end;
        }
        if (parts.Count > 32768) throw new NotSupportedException("The native top tree has only 15-bit subtree IDs.");
        int keyBits = 8; for (int i = parts.Count - 1; i != 0; i >>= 1) keyBits++;
        var minimum = parts.Select(p => p.Min).Aggregate(Vector3.Min); var maximum = parts.Select(p => p.Max).Aggregate(Vector3.Max);
        // Include local arithmetic endpoints before fixing the global shared quantizer. Cross-section
        // vertices use exactly the same global packed bits, avoiding independently quantized seams.
        foreach (var point in parts.SelectMany(p => p.Decoded)) { minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point); }
        var shared = ShareBoundaryVertices(parts, vertices, minimum, maximum);
        foreach (var part in parts)
            for (int i = 0; i < part.Indices.Length; i += 3)
            {
                float area = Vector3.Cross(part.Decoded[part.Indices[i + 1]] - part.Decoded[part.Indices[i]], part.Decoded[part.Indices[i + 2]] - part.Decoded[part.Indices[i]]).LengthSquared();
                if (!(area > 0) || !float.IsFinite(area)) throw new ArgumentException("A triangle is degenerate after native quantization.");
            }
        var partBounds = parts.Select(p => PointBounds(p.Decoded)).ToArray();
        var append = new PackAppend(template);
        append.RequireClass(shape, "hknpCompressedMeshShape", 0x2df8beec); append.RequireClass(originalTree, "hknpCompressedMeshShapeData", 0x54fd8d57);
        int referenced = append.Allocate(256);
        foreach (int offset in new[] { 16, 80, 96, 112, 128, 144, 160, 184, 200, 216, 232 }) append.Array(referenced + offset, 0, 0);
        append.VirtualLike(referenced, originalTree);
        int data = referenced + 16;
        append.Vector(data + 16, minimum); append.Vector(data + 32, maximum);
        append.UInt(data + 48, (uint)triangles); append.UInt(data + 52, (uint)keyBits);
        append.UInt(data + 56, ((uint)(parts.Count - 1) << 8) | (uint)(parts[^1].Indices.Length / 3 * 2 - 1));
        int topNodes = append.Allocate((parts.Count * 2 - 1) * 5); append.Array(data, topNodes, parts.Count * 2 - 1);
        var topBounds = new Bounds[parts.Count * 2 - 1];
        var topChildren = new (int Left, int Right)[topBounds.Length];
        var topOrder = Enumerable.Range(0, parts.Count).ToArray();
        int topCursor = 0;
        int Top(int first, int count)
        {
            int current = topCursor++;
            if (count == 1)
            {
                int leaf = topOrder[first];
                append.Byte(topNodes + current * 5 + 3, (byte)(leaf >> 8)); append.Byte(topNodes + current * 5 + 4, (byte)leaf);
                topBounds[current] = partBounds[leaf]; topChildren[current] = (-1, -1); return current;
            }
            SortLeaves(topOrder, first, count, partBounds);
            int left = count / 2, leftNode = Top(first, left), jump = topCursor - current;
            if ((jump & 1) != 0 || jump / 2 > 32767) throw new InvalidDataException("Native top-tree jump exceeds its encoding.");
            int payload = jump / 2; append.Byte(topNodes + current * 5 + 3, (byte)(0x80 | (payload >> 8))); append.Byte(topNodes + current * 5 + 4, (byte)payload);
            int rightNode = Top(first + left, count - left);
            topBounds[current] = Union(topBounds[leftNode], topBounds[rightNode]); topChildren[current] = (leftNode, rightNode); return current;
        }
        Top(0, parts.Count);
        EmitBounds(append, topNodes, 5, topBounds, topChildren, new(minimum, maximum));
        int subtrees = append.Allocate(parts.Count * 96); append.Array(data + 64, subtrees, parts.Count);
        int primitives = append.Allocate(triangles * 4); append.Array(data + 80, primitives, triangles);
        int packedCount = parts.Sum(p => p.Packed.Length), localData = append.Allocate(packedCount * 4); append.Array(data + 112, localData, packedCount);
        int sharedCount = parts.Sum(p => p.Shared.Length), sharedIndices = append.Allocate(sharedCount * 2);
        append.Array(data + 96, sharedIndices, sharedCount);
        int sharedData = append.Allocate(shared.Count * 8); append.Array(data + 128, sharedData, shared.Count);
        for (int i = 0; i < shared.Count; i++) append.ULong(sharedData + i * 8, shared[i]);
        var runs = new List<(ushort Material, byte First, byte Count)>();
        int localStart = 0, sharedStart = 0;
        for (int s = 0; s < parts.Count; s++)
        {
            var part = parts[s]; int subtree = subtrees + s * 96, count = part.Indices.Length / 3;
            var boundMin = partBounds[s].Min; var boundMax = partBounds[s].Max;
            append.Vector(subtree + 16, boundMin); append.Vector(subtree + 32, boundMax); append.Vector(subtree + 48, part.Min); append.Vector(subtree + 60, part.Step);
            append.UInt(subtree + 72, (uint)localStart); append.UInt(subtree + 76, ((uint)sharedStart << 8) | (uint)part.Packed.Length); append.UInt(subtree + 80, ((uint)part.First << 8) | (uint)count);
            append.UInt(subtree + 88, (uint)part.Vertices.Length);
            append.Byte(subtree + 92, part.Bank);
            foreach (ushort index in part.Shared) append.UShort(sharedIndices + sharedStart++ * 2, index);
            int nodes = append.Allocate((count * 2 - 1) * 4); append.Array(subtree, nodes, count * 2 - 1); int cursor = 0;
            var nodeBounds = new Bounds[count * 2 - 1]; var children = new (int Left, int Right)[nodeBounds.Length];
            var triangleBounds = Enumerable.Range(0, count).Select(i => PointBounds(part.Indices.Skip(i * 3).Take(3).Select(v => part.Decoded[v]))).ToArray();
            var order = Enumerable.Range(0, count).ToArray();
            int Build(int first, int n)
            {
                int current = cursor++;
                if (n == 1)
                {
                    int leaf = order[first]; append.Byte(nodes + current * 4 + 3, checked((byte)(leaf << 1)));
                    nodeBounds[current] = triangleBounds[leaf];
                    children[current] = (-1, -1); return current;
                }
                SortLeaves(order, first, n, triangleBounds);
                int left = n / 2, leftNode = Build(first, left), jump = cursor - current;
                if (jump > 254 || (jump & 1) != 0) throw new InvalidDataException("Native subtree jump exceeds its encoding.");
                append.Byte(nodes + current * 4 + 3, (byte)(jump | 1)); int rightNode = Build(first + left, n - left);
                nodeBounds[current] = Union(nodeBounds[leftNode], nodeBounds[rightNode]); children[current] = (leftNode, rightNode); return current;
            }
            Build(0, count);
            EmitBounds(append, nodes, 4, nodeBounds, children, partBounds[s]);
            for (int i = 0; i < count; i++)
            {
                int at = primitives + (part.First + i) * 4;
                append.Byte(at, part.Indices[i * 3]); append.Byte(at + 1, part.Indices[i * 3 + 1]); append.Byte(at + 2, part.Indices[i * 3 + 2]); append.Byte(at + 3, part.Indices[i * 3 + 2]);
            }
            for (int i = 0; i < part.Packed.Length; i++) append.UInt(localData + (localStart + i) * 4, part.Packed[i]);
            localStart += part.Packed.Length;
            int runFirst = runs.Count;
            for (int first = 0; first < count;)
            {
                int end = first + 1; while (end < count && materials[part.First + end] == materials[part.First + first]) end++;
                runs.Add((materials[part.First + first], (byte)first, (byte)(end - first))); first = end;
            }
            append.UInt(subtree + 84, ((uint)runFirst << 8) | (uint)(runs.Count - runFirst));
        }
        int runData = append.Allocate(runs.Count * 4); append.Array(data + 144, runData, runs.Count);
        for (int i = 0; i < runs.Count; i++) { append.UShort(runData + i * 4, runs[i].Material); append.Byte(runData + i * 4 + 2, runs[i].First); append.Byte(runData + i * 4 + 3, runs[i].Count); }
        int simdData = append.Allocate(224); append.Copy(simdData, template.Slice(simd.Address, 224)); append.Array(data + 168, simdData, 2);
        append.Pointer(shape + 96, referenced); append.Byte(shape + 18, (byte)keyBits);
        foreach (var (offset, bits) in new[] { (104, triangles), (128, triangles * 2) }) { int words = (bits + 31) / 32, bitData = append.Allocate(words * 4); append.Array(shape + offset, bitData, words); append.UInt(shape + offset + 16, (uint)bits); }
        append.UInt(shape + 152, (uint)triangles); append.UInt(shape + 156, 0);
        byte[] result = append.Finish(); ValidateTrees(result, variantIndex);
        var cooked = HavokMeshData.ReadVariant(result, variantIndex) ?? throw new InvalidDataException("Cooked mesh failed independent decoding.");
        if (cooked.Indices.Length != indices.Count || !cooked.Materials.SequenceEqual(materials) || !cooked.Vertices.SequenceEqual(parts.SelectMany(p => p.Indices.Select(i => p.Decoded[i]))))
            throw new InvalidDataException("Cooked triangle winding, quantized geometry or material runs disagree with the input.");
        return result;
    }

    private static List<ulong> ShareBoundaryVertices(List<Part> parts, IReadOnlyList<Vector3> source, Vector3 min, Vector3 max)
    {
        var boundary = parts.SelectMany(p => p.Vertices).GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var scale = (max - min) * new Vector3(1f / 2097151, 1f / 2097151, 1f / 4194303);
        var canonical = new Dictionary<uint, (ulong Bits, Vector3 Point)>();
        uint Q(float value, float origin, float step, uint limit, float upper)
        {
            uint q = step == 0 ? 0 : (uint)Math.Clamp(Math.Round(((double)value - origin) / step), 0, limit);
            while (q > 0 && origin + q * step > upper) q--;
            return q;
        }
        foreach (uint id in boundary)
        {
            var v = source[(int)id];
            ulong bits = Q(v.X, min.X, scale.X, 2097151, max.X) | ((ulong)Q(v.Y, min.Y, scale.Y, 2097151, max.Y) << 21) |
                ((ulong)Q(v.Z, min.Z, scale.Z, 4194303, max.Z) << 42);
            var point = min + new Vector3(bits & 2097151, (bits >> 21) & 2097151, bits >> 42) * scale;
            canonical.Add(id, (bits, point));
        }
        var shared = new List<ulong>(); var bank = new Dictionary<uint, ushort>(); int bankNumber = 0;
        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            var order = Enumerable.Range(0, part.Vertices.Length).Where(i => !boundary.Contains(part.Vertices[i]))
                .Concat(Enumerable.Range(0, part.Vertices.Length).Where(i => boundary.Contains(part.Vertices[i]))).ToArray();
            int locals = order.Count(i => !boundary.Contains(part.Vertices[i]));
            var ids = order.Select(i => part.Vertices[i]).ToArray();
            int needed = ids.Skip(locals).Count(id => !bank.ContainsKey(id));
            if (bank.Count + needed > 65536)
            {
                bankNumber++; if (bankNumber > byte.MaxValue) throw new NotSupportedException("Shared vertex bank limit exceeded.");
                while (shared.Count < bankNumber * 65536) shared.Add(0);
                bank.Clear();
            }
            var slots = new List<ushort>();
            foreach (uint id in ids.Skip(locals))
            {
                if (!bank.TryGetValue(id, out ushort index))
                { index = checked((ushort)bank.Count); bank.Add(id, index); shared.Add(canonical[id].Bits); }
                slots.Add(index);
            }
            var oldToNew = new byte[order.Length]; for (int i = 0; i < order.Length; i++) oldToNew[order[i]] = checked((byte)i);
            var decoded = order.Select(i => canonical.TryGetValue(part.Vertices[i], out var value) ? value.Point : part.Decoded[i]).ToArray();
            parts[p] = part with { Vertices = ids, Indices = part.Indices.Select(i => oldToNew[i]).ToArray(),
                Packed = order.Take(locals).Select(i => part.Packed[i]).ToArray(), Decoded = decoded, Shared = slots.ToArray(), Bank = (byte)bankNumber };
        }
        return shared;
    }

    private static bool Contains(Bounds outer, Bounds inner) => inner.Min.X >= outer.Min.X && inner.Min.Y >= outer.Min.Y && inner.Min.Z >= outer.Min.Z &&
        inner.Max.X <= outer.Max.X && inner.Max.Y <= outer.Max.Y && inner.Max.Z <= outer.Max.Z;
    private static void SortLeaves(int[] ids, int first, int count, Bounds[] bounds)
    {
        var box = bounds[ids[first]]; for (int i = 1; i < count; i++) box = Union(box, bounds[ids[first + i]]);
        var size = box.Max - box.Min; int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        float Center(int id) { var b = bounds[id]; return (b.Min[axis] + b.Max[axis]) * .5f; }
        System.Array.Sort(ids, first, count, Comparer<int>.Create((a, b) => Center(a).CompareTo(Center(b)) is int comparison && comparison != 0 ? comparison : a.CompareTo(b)));
    }
    private static Bounds Unpack(Bounds parent, ReadOnlySpan<byte> code)
    {
        var step = (parent.Max - parent.Min) * (1f / 226);
        var low = new Vector3(code[0] >> 4, code[1] >> 4, code[2] >> 4);
        var high = new Vector3(code[0] & 15, code[1] & 15, code[2] & 15);
        return new(parent.Min + low * low * step, parent.Max - high * high * step);
    }
    private static void EmitBounds(PackAppend append, int address, int stride, Bounds[] actual,
        (int Left, int Right)[] children, Bounds root)
    {
        void Emit(int node, Bounds parent, bool first)
        {
            var code = new byte[3];
            if (!first)
                for (int axis = 0; axis < 3; axis++)
                {
                    for (int value = 1; value < 16; value++)
                    {
                        byte previous = code[axis]; code[axis] = (byte)((value << 4) | (previous & 15));
                        if (!Contains(Unpack(parent, code), actual[node])) { code[axis] = previous; break; }
                    }
                    for (int value = 1; value < 16; value++)
                    {
                        byte previous = code[axis]; code[axis] = (byte)((previous & 0xf0) | value);
                        if (!Contains(Unpack(parent, code), actual[node])) { code[axis] = previous; break; }
                    }
                }
            var decoded = Unpack(parent, code);
            if (!Contains(decoded, actual[node])) throw new InvalidDataException("Quantized native BVH bounds exclude descendant geometry.");
            append.Copy(address + node * stride, code);
            if (children[node].Left >= 0) { Emit(children[node].Left, decoded, false); Emit(children[node].Right, decoded, false); }
        }
        Emit(0, root, true);
    }

    /// <summary>Checks native leaf/jump maps independently of primitive-array decoding.</summary>
    public static void ValidateTrees(ReadOnlySpan<byte> input, int variantIndex)
    {
        var file = new HavokMeshPackfile(input); int shape = file.Variants[variantIndex].Address, tree = file.Pointer(shape + 96) + 16;
        var top = file.Array(tree, 5); var subtrees = file.Array(tree + 64, 96); var primitives = file.Array(tree + 80, 4);
        var geometry = HavokMeshData.ReadVariant(input, variantIndex) ?? throw new InvalidDataException("Cooked topology contains no triangles.");
        Vector3 V(int at) => new(file.F(at), file.F(at + 4), file.F(at + 8));
        Bounds ReadBounds(int at) => new(V(at + 16), V(at + 32));
        Bounds Decode(int at, Bounds parent) => Unpack(parent, new[] { file.Byte(at), file.Byte(at + 1), file.Byte(at + 2) });
        var actual = Enumerable.Range(0, subtrees.Count).Select(s =>
        {
            uint info = file.U(subtrees.Address + s * 96 + 80); int first = (int)(info >> 8), count = (byte)info;
            return PointBounds(geometry.Vertices.Skip(first * 3).Take(count * 3));
        }).ToArray();
        var topSeen = new HashSet<int>(); var sectionsSeen = new HashSet<int>();
        void Top(int node, Bounds parent)
        {
            if ((uint)node >= top.Count || !topSeen.Add(node)) throw new InvalidDataException("Invalid top-tree jump or cycle.");
            int at = top.Address + node * 5; var decoded = Decode(at, parent); int payload = ((file.Byte(at + 3) & 0x7f) << 8) | file.Byte(at + 4);
            if (!Contains(parent, decoded)) throw new InvalidDataException("Top child bounds exceed their decoded parent.");
            if ((file.Byte(at + 3) & 0x80) == 0)
            { if (payload >= subtrees.Count || !sectionsSeen.Add(payload) || !Contains(decoded, actual[payload])) throw new InvalidDataException("Invalid subtree leaf or bounds exclude geometry."); }
            else { Top(node + 1, decoded); Top(node + payload * 2, decoded); }
        }
        Top(0, ReadBounds(tree));
        if (topSeen.Count != top.Count || sectionsSeen.Count != subtrees.Count) throw new InvalidDataException("Top tree does not cover every subtree exactly once.");
        var primitiveSeen = new HashSet<int>();
        for (int s = 0; s < subtrees.Count; s++)
        {
            int subtree = subtrees.Address + s * 96; var nodes = file.Array(subtree, 4); uint info = file.U(subtree + 80); int count = (byte)info, first = (int)(info >> 8);
            var visited = new HashSet<int>(); var leaves = new HashSet<int>();
            void Visit(int node, Bounds parent)
            {
                if ((uint)node >= nodes.Count || !visited.Add(node)) throw new InvalidDataException("Invalid subtree jump or cycle.");
                int at = nodes.Address + node * 4; var decoded = Decode(at, parent); byte payload = file.Byte(at + 3);
                if (!Contains(parent, decoded)) throw new InvalidDataException("Primitive child bounds exceed their decoded parent.");
                if ((payload & 1) == 0)
                {
                    int leaf = payload >> 1;
                    if (leaf >= count || !leaves.Add(leaf) || first + leaf >= primitives.Count || !primitiveSeen.Add(first + leaf) ||
                        !Contains(decoded, PointBounds(geometry.Vertices.Skip((first + leaf) * 3).Take(3)))) throw new InvalidDataException("Invalid primitive leaf or bounds exclude geometry.");
                }
                else { Visit(node + 1, decoded); Visit(node + (payload & 0xfe), decoded); }
            }
            Visit(0, ReadBounds(subtree)); if (visited.Count != nodes.Count || leaves.Count != count) throw new InvalidDataException("Subtree does not cover every primitive exactly once.");
        }
        int bits = 8; for (int i = subtrees.Count - 1; i > 0; i >>= 1) bits++;
        if (primitiveSeen.Count != primitives.Count || file.U(tree + 48) != primitives.Count || file.U(tree + 52) != bits || file.Byte(shape + 18) != bits)
            throw new InvalidDataException("Native primitive totals or shape-key bits disagree with the rebuilt trees.");
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    /// <summary>Appends only to a standard final __data__ section. Existing object addresses, palette
    /// references and unrelated variants stay stable; fixup tables are regenerated with replaced pointers.</summary>
    private sealed class PackAppend
    {
        private readonly byte[] _template;
        private readonly int _start, _local, _global, _virtual, _end;
        private readonly MemoryStream _data = new();
        private readonly Dictionary<int, int> _replaced = [];
        private readonly List<(int Object, int Section, int Class)> _newVirtuals = [];
        private int I(int p) => BinaryPrimitives.ReadInt32LittleEndian(_template.AsSpan(p));
        public PackAppend(ReadOnlySpan<byte> input)
        {
            _template = input.ToArray();
            if (I(20) != 3) throw new NotSupportedException("Topology cooking currently requires the stock three-section PC packfile.");
            const int header = 80 + 2 * 64;
            _start = I(header + 20); _local = I(header + 24); _global = I(header + 28); _virtual = I(header + 32); _end = I(header + 36);
            if (_start + _end != _template.Length) throw new NotSupportedException("The Havok data section must be the final file extent.");
            _data.Write(input.Slice(_start, _local));
        }
        public int Allocate(int size)
        {
            while ((_data.Length & 15) != 0) _data.WriteByte(0);
            int address = checked(_start + (int)_data.Length); _data.Write(new byte[size]); return address;
        }
        public void Copy(int address, ReadOnlySpan<byte> bytes)
        { long position = _data.Position; _data.Position = address - _start; _data.Write(bytes); _data.Position = position; }
        public void Byte(int address, byte value) => Copy(address, [value]);
        public void UInt(int address, uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); Copy(address, b); }
        public void ULong(int address, ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); Copy(address, b); }
        public void UShort(int address, ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, value); Copy(address, b); }
        public void Vector(int address, Vector3 value)
        { UInt(address, BitConverter.SingleToUInt32Bits(value.X)); UInt(address + 4, BitConverter.SingleToUInt32Bits(value.Y)); UInt(address + 8, BitConverter.SingleToUInt32Bits(value.Z)); }
        public void Pointer(int address, int target) { Copy(address, new byte[8]); _replaced[address - _start] = target - _start; }
        public void Array(int address, int target, int count)
        {
            Copy(address, new byte[8]); UInt(address + 8, (uint)count); UInt(address + 12, (uint)count | 0x80000000);
            if (count > 0) Pointer(address, target);
        }
        public void VirtualLike(int address, int original)
        {
            for (int p = _virtual; p <= _end - 12; p += 12)
                if (I(_start + p) == original - _start)
                { _newVirtuals.Add((address - _start, I(_start + p + 4), I(_start + p + 8))); return; }
            throw new NotSupportedException("The template tree has no native virtual-class fixup.");
        }
        public void RequireClass(int address, string name, uint signature)
        {
            for (int p = _virtual; p <= _end - 12; p += 12)
                if (I(_start + p) == address - _start)
                {
                    int section = I(_start + p + 4), offset = I(_start + p + 8);
                    if ((uint)section >= 3 || offset < 5) break;
                    int at = checked(I(80 + section * 64 + 20) + offset);
                    int end = System.Array.IndexOf(_template, (byte)0, at);
                    if (end < 0 || _template[at - 1] != 9 ||
                        BinaryPrimitives.ReadUInt32LittleEndian(_template.AsSpan(at - 5)) != signature ||
                        System.Text.Encoding.ASCII.GetString(_template, at, end - at) != name) break;
                    return;
                }
            throw new NotSupportedException("Unsupported native collision class signature: " + name);
        }
        public byte[] Finish()
        {
            using var writer = new BinaryWriter(_data, System.Text.Encoding.ASCII, leaveOpen: true);
            void Pad() { while ((_data.Position & 15) != 0) writer.Write((byte)0xff); }
            Pad(); int local = checked((int)_data.Position);
            for (int p = _local; p <= _global - 8; p += 8)
                if (I(_start + p) >= 0 && !_replaced.ContainsKey(I(_start + p))) writer.Write(_template.AsSpan(_start + p, 8));
            foreach (var (source, target) in _replaced.OrderBy(p => p.Key)) { writer.Write(source); writer.Write(target); }
            Pad(); int global = checked((int)_data.Position);
            for (int p = _global; p <= _virtual - 12; p += 12)
                if (I(_start + p) >= 0 && !_replaced.ContainsKey(I(_start + p))) writer.Write(_template.AsSpan(_start + p, 12));
            Pad(); int virtuals = checked((int)_data.Position);
            for (int p = _virtual; p <= _end - 12; p += 12)
                if (I(_start + p) >= 0) writer.Write(_template.AsSpan(_start + p, 12));
            foreach (var item in _newVirtuals) { writer.Write(item.Object); writer.Write(item.Section); writer.Write(item.Class); }
            Pad(); int end = checked((int)_data.Position);
            byte[] result = new byte[checked(_start + end)]; _template.AsSpan(0, _start).CopyTo(result); _data.ToArray().CopyTo(result, _start);
            const int header = 80 + 2 * 64;
            foreach (var (offset, value) in new[] { (24, local), (28, global), (32, virtuals), (36, end), (40, end), (44, end) })
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(header + offset), value);
            _ = new HavokMeshPackfile(result);
            return result;
        }
    }
}
