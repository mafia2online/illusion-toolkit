using System.Buffers.Binary;
using System.Numerics;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>
/// Conservative, fixed-size refit of the native OPCODE/BPA trees. Every existing node retains its
/// children and primitive ranges, but encloses the union of the old tree and the edited mesh.
/// This trades subtree culling efficiency for correctness without rebuilding unknown leaf mappings.
/// Layouts are verified against S_OpcodeData::Serialize/Deserialize and C_BPAColliderCombined::Intersect.
/// </summary>
internal static class CompiledSubdivisionRefit
{
    internal sealed record Change(CompiledElement Field, byte[] Before, byte[] After);

    internal static List<Change> Compute(CompiledGraph graph, CompiledElement mesh, Vector3[] positions)
    {
        Vector3 min = positions.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min);
        Vector3 max = positions.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max);
        var changes = new List<Change>();
        var cd = graph.Resolve(graph.Field(mesh, "m_MeshCDSubdiv"));
        if (cd is not null)
        {
            var opcode = graph.Field(cd, "m_OpcodeData");
            if (cd.TypeId != 0x4dc47de8 || opcode is null || opcode.TypeId != 0x3a9e63f8)
                throw new InvalidDataException("Unsupported compiled collision subdivision; position changes require a verified bounds refit.");
            byte[] before = graph.Data(opcode).ToArray();
            changes.Add(new Change(opcode, before, RefitOpcode(before, min, max)));
        }
        var render = graph.Resolve(graph.Field(mesh, "m_RenderSubdiv"));
        if (render is not null)
        {
            var context = graph.Resolve(graph.Field(render, "m_Ctx"));
            if (render.TypeId != 0x51b03839 || context?.TypeId != 0x557b6b6d)
                throw new InvalidDataException("Unsupported compiled render subdivision; position changes require a verified bounds refit.");
            foreach (string field in new[] { "m_StdMatTree", "m_CollapsedMatTree" })
            {
                var tree = graph.Resolve(graph.Field(context, field));
                if (tree is null) continue;
                var nodes = graph.Field(tree, "m_Nodes");
                if (tree.TypeId != 0x2946549f || nodes?.TypeId != 0x33342390)
                    throw new InvalidDataException("Unsupported compiled BPA node layout.");
                if (changes.Any(c => ReferenceEquals(c.Field, nodes))) continue;
                byte[] before = graph.Data(nodes).ToArray();
                changes.Add(new Change(nodes, before, RefitBpa(before, min, max)));
            }
        }
        return changes;
    }

    internal static byte[] RefitBpa(byte[] source, Vector3 min, Vector3 max)
    {
        if (source.Length < 4) throw new InvalidDataException("Truncated BPA node array.");
        int count = checked((int)U32(source, 0));
        if (source.Length != 4L + 20L * count) throw new InvalidDataException("Unsupported BPA node array size.");
        for (int node = 0; node < count; node++)
        {
            int start = 4 + node * 20;
            var lo = new Vector3(I16(source, start), I16(source, start + 2), I16(source, start + 4));
            var hi = new Vector3(I16(source, start + 6), I16(source, start + 8), I16(source, start + 10));
            if (lo.X > hi.X || lo.Y > hi.Y || lo.Z > hi.Z) throw new InvalidDataException("Invalid BPA node bounds.");
            min = Vector3.Min(min, lo); max = Vector3.Max(max, hi);
            for (int child = 16; child <= 18; child += 2)
                if (I16(source, start + child) >= count) throw new InvalidDataException("BPA child exceeds the node array.");
        }
        var lower = new[] { FloorShort(min.X), FloorShort(min.Y), FloorShort(min.Z) };
        var upper = new[] { CeilShort(max.X), CeilShort(max.Y), CeilShort(max.Z) };
        byte[] result = (byte[])source.Clone();
        for (int node = 0; node < count; node++)
            for (int axis = 0; axis < 3; axis++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(4 + node * 20 + axis * 2), lower[axis]);
                BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(4 + node * 20 + 6 + axis * 2), upper[axis]);
            }
        return result;
    }

    internal static byte[] RefitOpcode(byte[] source, Vector3 min, Vector3 max)
    {
        // 0xB: quantized no-leaf hybrid tree, compact u16 triangle remap. 0xC is the
        // single-node model: it has no spatial tree/bounds to refit. Other variants
        // cannot safely borrow this layout even when their byte counts happen to match.
        if (source.Length < 15 || source[4] > 1
            || (U32(source, 0) != 0xb && !(U32(source, 0) == 0xc && source[4] == 0)))
            throw new InvalidDataException("Unsupported compiled OPCODE flags; position changes require a verified bounds refit.");
        int position = 5, nodesOffset = 0, count = 0;
        if (source[4] != 0)
        {
            if (source.Length < 33) throw new InvalidDataException("Truncated quantized OPCODE tree.");
            var centerScale = CompiledMeshDecoder.Vector(source.AsSpan(5));
            var extentScale = CompiledMeshDecoder.Vector(source.AsSpan(17));
            if (!Finite(centerScale) || !Finite(extentScale) || Vector3.Min(extentScale, Vector3.Zero) != Vector3.Zero)
                throw new InvalidDataException("Invalid OPCODE quantization coefficients.");
            count = checked((int)U32(source, 29)); nodesOffset = 33;
            position = CheckedEnd(nodesOffset, count, 20, source.Length);
            for (int node = 0; node < count; node++)
            {
                int start = nodesOffset + node * 20;
                var center = new Vector3(I16(source, start + 8), I16(source, start + 10), I16(source, start + 12)) * centerScale;
                var extent = new Vector3(U16(source, start + 14), U16(source, start + 16), U16(source, start + 18)) * extentScale;
                if (!Finite(center) || !Finite(extent)) throw new InvalidDataException("Invalid OPCODE node bounds.");
                min = Vector3.Min(min, center - extent); max = Vector3.Max(max, center + extent);
                for (int child = 0; child <= 4; child += 4)
                {
                    uint value = U32(source, start + child);
                    if ((value & 1) == 0 && value / 2 >= count)
                        throw new InvalidDataException("OPCODE child exceeds the node array.");
                }
            }
        }
        // Validate the preserved hybrid-leaf table and compact triangle remap before changing anything.
        for (int table = 0; table < 2; table++)
        {
            if (source.Length - position < 5 || source[position + 4] > 1)
                throw new InvalidDataException("Truncated OPCODE hybrid table.");
            int entries = checked((int)U32(source, position)); bool present = source[position + 4] != 0;
            position += 5;
            if (present) position = CheckedEnd(position, entries, table == 0 ? 4 : 2, source.Length);
        }
        if (position != source.Length) throw new InvalidDataException("Unsupported OPCODE trailer.");
        byte[] result = (byte[])source.Clone();
        if (count == 0) return result;
        var middle = (min + max) * 0.5f;
        var centers = new[] { FloorShort(middle.X), FloorShort(middle.Y), FloorShort(middle.Z) };
        var extents = new[] { Extent(min.X, max.X, centers[0]), Extent(min.Y, max.Y, centers[1]), Extent(min.Z, max.Z, centers[2]) };
        // Unit coefficients deliberately give conservative whole-unit boxes. A one-unit margin
        // protects the original quantized boxes against float cancellation during the union.
        for (int coefficient = 0; coefficient < 6; coefficient++)
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(5 + coefficient * 4), BitConverter.SingleToInt32Bits(1));
        for (int node = 0; node < count; node++)
            for (int axis = 0; axis < 3; axis++)
            {
                int start = nodesOffset + node * 20;
                BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(start + 8 + axis * 2), centers[axis]);
                BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(start + 14 + axis * 2), extents[axis]);
            }
        return result;
    }

    private static int CheckedEnd(int start, int count, int stride, int length)
    {
        if ((long)start + (long)count * stride > length) throw new InvalidDataException("Truncated compiled subdivision array.");
        return checked(start + count * stride);
    }
    private static short FloorShort(float value) => Bound(MathF.Floor(value));
    private static short CeilShort(float value) => Bound(MathF.Ceiling(value));
    private static short Bound(float value)
    {
        if (!float.IsFinite(value) || value < short.MinValue || value > short.MaxValue)
            throw new InvalidDataException("Compiled subdivision bounds exceed their native signed-short range.");
        return (short)value;
    }
    private static ushort Extent(float min, float max, short center)
    {
        float value = MathF.Ceiling(MathF.Max(center - min, max - center)) + 1;
        if (!float.IsFinite(value) || value < 0 || value > ushort.MaxValue)
            throw new InvalidDataException("Compiled OPCODE extent exceeds its native unsigned-short range.");
        return (ushort)value;
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
    private static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
    private static short I16(byte[] b, int p) => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(p));
}
