using System.Numerics;
using Illusion.Formats.Compiled;

namespace Illusion.NativeCookHost;

internal sealed unsafe partial class NavigationOracle
{
    internal sealed record PhysicsReport(int Variants, int Triangles, int ScalarNodes, int NonzeroBounds,
        int ExactVertexComponents, float MaximumVertexError, float MaximumBoundsError,
        int UncontainedNodes, float MaximumContainmentError);
    private readonly record struct PhysicsBox(Vector3 Min, Vector3 Max)
    {
        public bool Contains(PhysicsBox other) => other.Min.X >= Min.X && other.Min.Y >= Min.Y && other.Min.Z >= Min.Z &&
            other.Max.X <= Max.X && other.Max.Y <= Max.Y && other.Max.Z <= Max.Z;
        public static PhysicsBox Union(PhysicsBox a, PhysicsBox b) => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));
    }

    /// <summary>Read-only native-binary consumption proof, with no shape construction, game callbacks
    /// or simulation. Both native entrypoints have zero calls and only perform reads/math/output writes.</summary>
    public PhysicsReport ValidatePhysics(byte[] packet, int? selectedVariant, bool strictBounds)
    {
        var file = new HavokMeshPackfile(packet);
        nint raw = Map(packet, 0), triangleOutput = Allocate(48), parentBox = Allocate(32), decodedBox = Allocate(32);
        int variants = 0, triangles = 0, nodesChecked = 0, nonzero = 0, exact = 0, uncontained = 0;
        float vertexError = 0, boundsError = 0, containmentError = 0;
        PhysicsBox Header(int address) => new(new(file.F(address + 16), file.F(address + 20), file.F(address + 24)),
            new(file.F(address + 32), file.F(address + 36), file.F(address + 40)));
        PhysicsBox Decode(PhysicsBox parent, int address)
        {
            *(Vector3*)parentBox = parent.Min; *(Vector3*)(parentBox + 16) = parent.Max;
            ((delegate* unmanaged<nint, nint, nint, void>)0x143D5FF70L)(parentBox, raw + address, decodedBox);
            var native = new PhysicsBox(V(decodedBox), V(decodedBox + 16));
            var step = (parent.Max - parent.Min) * (1f / 226);
            var high = new Vector3(file.Byte(address) >> 4, file.Byte(address + 1) >> 4, file.Byte(address + 2) >> 4);
            var low = new Vector3(file.Byte(address) & 15, file.Byte(address + 1) & 15, file.Byte(address + 2) & 15);
            var managed = new PhysicsBox(parent.Min + high * high * step, parent.Max - low * low * step);
            boundsError = MathF.Max(boundsError, MathF.Max(Vector3.Distance(native.Min, managed.Min), Vector3.Distance(native.Max, managed.Max)));
            if (native != managed) throw new InvalidDataException("Managed Codec3Axis bounds disagree with the original native PC decoder.");
            nodesChecked++; if ((file.Byte(address) | file.Byte(address + 1) | file.Byte(address + 2)) != 0) nonzero++;
            return native;
        }
        for (int variant = 0; variant < file.Variants.Count; variant++)
        {
            if (selectedVariant is { } selected && variant != selected) continue;
            if (file.Variants[variant].Type != "hknpCompressedMeshShape") continue;
            var mesh = HavokMeshData.ReadVariant(packet, variant) ?? throw new InvalidDataException("Missing compressed triangle geometry.");
            if (triangles + mesh.ShapeKeys.Length > 500000) throw new NotSupportedException("Physics validation exceeds its triangle limit.");
            int tree = file.Pointer(file.Variants[variant].Address + 96) + 16;
            var sections = file.Array(tree + 64, 96);
            var primitiveBounds = new Dictionary<uint, PhysicsBox>();
            for (int triangle = 0; triangle < mesh.ShapeKeys.Length; triangle++)
            {
                uint key = mesh.ShapeKeys[triangle];
                ((delegate* unmanaged<nint, uint, nint, nint>)0x143F31A70L)(raw + tree, key, triangleOutput);
                PhysicsBox? box = null;
                for (int corner = 0; corner < 3; corner++)
                {
                    Vector3 native = V(triangleOutput + corner * 16), managed = mesh.Vertices[mesh.Indices[triangle * 3 + corner]];
                    if (!float.IsFinite(native.X) || !float.IsFinite(native.Y) || !float.IsFinite(native.Z)) throw new InvalidDataException("Native triangle decoder produced non-finite coordinates.");
                    vertexError = MathF.Max(vertexError, Vector3.Distance(native, managed));
                    for (int axis = 0; axis < 3; axis++) if (native[axis] == managed[axis]) exact++;
                    if (native != managed) throw new InvalidDataException($"Managed/native triangle coordinates differ at variant {variant}, key {key:X}, corner {corner}: {native} != {managed}.");
                    var point = new PhysicsBox(native, native); box = box is { } prior ? PhysicsBox.Union(prior, point) : point;
                }
                uint primitive = key & ~1u;
                primitiveBounds[primitive] = primitiveBounds.TryGetValue(primitive, out var old) ? PhysicsBox.Union(old, box!.Value) : box!.Value;
            }
            var sectionBounds = new Dictionary<int, PhysicsBox>();
            PhysicsBox Walk(int address, int count, int stride, int index, PhysicsBox parent, HashSet<int> seen, Func<int, PhysicsBox> leaf)
            {
                if ((uint)index >= count || !seen.Add(index)) throw new InvalidDataException("Invalid native scalar tree child map.");
                int at = address + index * stride; PhysicsBox decoded = Decode(parent, at), actual;
                int value = stride == 4 ? file.Byte(at + 3) : ((file.Byte(at + 3) & 127) << 8) | file.Byte(at + 4);
                bool branch = stride == 4 ? (value & 1) != 0 : (file.Byte(at + 3) & 128) != 0;
                if (branch)
                {
                    int distance = stride == 4 ? value & 254 : value * 2;
                    actual = PhysicsBox.Union(Walk(address, count, stride, index + 1, decoded, seen, leaf),
                        Walk(address, count, stride, index + distance, decoded, seen, leaf));
                }
                else actual = leaf(stride == 4 ? value >> 1 : value);
                if (!decoded.Contains(actual))
                {
                    uncontained++;
                    var miss = Vector3.Max(decoded.Min - actual.Min, actual.Max - decoded.Max);
                    containmentError = MathF.Max(containmentError, MathF.Max(miss.X, MathF.Max(miss.Y, miss.Z)));
                    if (strictBounds) throw new InvalidDataException($"Native Codec3Axis node {index} misses native triangle coordinates: {decoded} versus {actual}.");
                }
                return actual;
            }
            for (int s = 0; s < sections.Count; s++)
            {
                int section = sections.Address + s * 96; var nodes = file.Array(section, 4); var seen = new HashSet<int>(); int sectionId = s;
                var actual = Walk(nodes.Address, nodes.Count, 4, 0, Header(section), seen, primitive =>
                    primitiveBounds.TryGetValue(((uint)sectionId << 8) | ((uint)primitive * 2), out var box) ? box :
                        throw new InvalidDataException("Native subtree leaf is not a decoded triangle primitive."));
                if (seen.Count != nodes.Count) throw new InvalidDataException("Unreachable native subtree nodes.");
                sectionBounds.Add(s, actual);
            }
            var top = file.Array(tree, 5); var topSeen = new HashSet<int>();
            Walk(top.Address, top.Count, 5, 0, Header(tree), topSeen, id => sectionBounds.TryGetValue(id, out var box) ? box : throw new InvalidDataException("Absent native subtree."));
            if (topSeen.Count != top.Count) throw new InvalidDataException("Unreachable native top-level nodes.");
            variants++; triangles += mesh.ShapeKeys.Length;
        }
        if (variants == 0) throw new NotSupportedException("No selected supported compressed mesh variants.");
        return new(variants, triangles, nodesChecked, nonzero, exact, vertexError, boundsError, uncontained, containmentError);
    }
}
