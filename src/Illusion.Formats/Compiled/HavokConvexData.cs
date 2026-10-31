using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Illusion.Formats.Compiled;

public sealed record HavokConvexHull(Vector3[] Vertices, uint[] Indices, float ConvexRadius);

/// <summary>Reads actual polygon faces from the 64-bit little-endian Havok 2014.2 packfiles shipped by Mafia III.</summary>
public static class HavokConvexData
{
    private readonly record struct Section(int Start, int Local, int Global, int Virtual, int End);

    public static IReadOnlyList<HavokConvexHull?> Read(ReadOnlySpan<byte> input)
    {
        // Copy because the local checked readers and fixup resolvers must share one stable buffer.
        byte[] data = input.ToArray();
        void Check(int offset, int count)
        { if (offset < 0 || count < 0 || offset > data.Length - count) throw new InvalidDataException("Havok packfile extent exceeds its resource."); }
        int I(int offset) { Check(offset, 4); return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset)); }
        ushort U16(int offset) { Check(offset, 2); return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)); }
        float F(int offset) { Check(offset, 4); return BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset)); }
        Check(0, 80);
        if (I(0) != 0x57e0e057 || I(4) != 0x10c0c010 || I(12) != 11 ||
            data[16] != 8 || data[17] != 1 || !data.AsSpan(40, 14).SequenceEqual("hk_2014.2.0-r1"u8) || data[54] != 0 || data[55] != 0xff)
            throw new NotSupportedException("Only Mafia III Havok 2014.2 little-endian 64-bit packfiles are supported.");
        int count = I(20), rootSection = I(24), rootOffset = I(28);
        if (count is < 1 or > 32 || (uint)rootSection >= count) throw new InvalidDataException("Invalid Havok section directory.");
        Check(80, checked(count * 64));
        var sections = new Section[count];
        for (int index = 0; index < count; index++)
        {
            int header = 80 + 64 * index;
            var section = new Section(I(header + 20), I(header + 24), I(header + 28), I(header + 32), I(header + 36));
            if (section.Local < 0 || section.Global < section.Local || section.Virtual < section.Global || section.End < section.Virtual)
                throw new InvalidDataException("Invalid Havok fixup ranges.");
            Check(section.Start, section.End); sections[index] = section;
        }
        var pointers = new Dictionary<(int Section, int Offset), (int Section, int Offset)>();
        for (int index = 0; index < count; index++)
        {
            var section = sections[index];
            for (int offset = section.Local; offset <= section.Global - 8; offset += 8)
            {
                int source = I(section.Start + offset), target = I(section.Start + offset + 4);
                if (source >= 0) pointers[(index, source)] = (index, target);
            }
            for (int offset = section.Global; offset <= section.Virtual - 12; offset += 12)
            {
                int source = I(section.Start + offset), targetSection = I(section.Start + offset + 4), target = I(section.Start + offset + 8);
                if (source >= 0) pointers[(index, source)] = (targetSection, target);
            }
        }
        int Address((int Section, int Offset) pointer, int length)
        {
            if ((uint)pointer.Section >= sections.Length || pointer.Offset < 0 || pointer.Offset > sections[pointer.Section].Local - length)
                throw new InvalidDataException("Invalid Havok object pointer.");
            int result = checked(sections[pointer.Section].Start + pointer.Offset); Check(result, length); return result;
        }
        (int Section, int Offset) Pointer(int section, int offset) => pointers.TryGetValue((section, offset), out var target)
            ? target : throw new InvalidDataException("Missing Havok object fixup.");
        string Text((int Section, int Offset) pointer)
        {
            int address = Address(pointer, 1), limit = sections[pointer.Section].Start + sections[pointer.Section].Local;
            int end = Array.IndexOf(data, (byte)0, address, limit - address);
            if (end < 0) throw new InvalidDataException("Unterminated Havok type name.");
            return Encoding.ASCII.GetString(data, address, end - address);
        }
        int root = Address((rootSection, rootOffset), 16), variants = I(root + 8);
        if (variants is < 0 or > 100000) throw new InvalidDataException("Invalid Havok variant count.");
        var arrayPointer = Pointer(rootSection, rootOffset);
        Address(arrayPointer, checked(variants * 24));
        var result = new List<HavokConvexHull?>();
        for (int index = 0; index < variants; index++)
        {
            int entry = arrayPointer.Offset + index * 24;
            string type = Text(Pointer(arrayPointer.Section, entry + 8));
            if (type != "hknpConvexPolytopeShape") { result.Add(null); continue; }
            var shapePointer = Pointer(arrayPointer.Section, entry + 16);
            int shape = Address(shapePointer, 80);
            // Windows serialized hknpShape has eight more bytes than the symbolized Mac native base.
            int verticesCount = U16(shape + 48), facesCount = U16(shape + 68), indicesCount = U16(shape + 72);
            int vertices = checked(shape + 48 + U16(shape + 50));
            int faces = checked(shape + 68 + U16(shape + 70));
            int indices = checked(shape + 72 + U16(shape + 74));
            if (verticesCount is < 4 or > 256 || facesCount is < 4 or > 256) throw new InvalidDataException("Invalid Havok convex shape counts.");
            int objectLimit = sections[shapePointer.Section].Start + sections[shapePointer.Section].Local;
            if ((long)vertices + verticesCount * 16 > objectLimit || (long)faces + facesCount * 4 > objectLimit || (long)indices + indicesCount > objectLimit)
                throw new InvalidDataException("Invalid Havok relative convex arrays.");
            var points = new Vector3[verticesCount]; Vector3 center = Vector3.Zero;
            for (int vertex = 0; vertex < verticesCount; vertex++)
            {
                int offset = vertices + 16 * vertex;
                var point = new Vector3(F(offset), F(offset + 4), F(offset + 8));
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) throw new InvalidDataException("Non-finite Havok vertex.");
                points[vertex] = point; center += point;
            }
            center /= verticesCount;
            var triangles = new List<uint>();
            for (int face = 0; face < facesCount; face++)
            {
                int first = U16(faces + 4 * face), size = data[faces + 4 * face + 2];
                if (size < 3 || first > indicesCount - size) throw new InvalidDataException("Invalid Havok convex face.");
                int a = data[indices + first];
                for (int corner = 1; corner < size - 1; corner++)
                {
                    int b = data[indices + first + corner], c = data[indices + first + corner + 1];
                    if (a >= verticesCount || b >= verticesCount || c >= verticesCount) throw new InvalidDataException("Havok face vertex exceeds its array.");
                    if (Vector3.Dot(Vector3.Cross(points[b] - points[a], points[c] - points[a]), center - points[a]) > 0) (b, c) = (c, b);
                    triangles.Add((uint)a); triangles.Add((uint)b); triangles.Add((uint)c);
                }
            }
            float radius = F(shape + 20);
            if (!float.IsFinite(radius) || radius < 0) throw new InvalidDataException("Invalid Havok convex radius.");
            result.Add(new HavokConvexHull(points, triangles.ToArray(), radius));
        }
        return result;
    }
}
