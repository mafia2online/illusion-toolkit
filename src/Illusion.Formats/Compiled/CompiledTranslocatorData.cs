using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Native translocator placement data. The KD-tree's 48-byte rows contain authored
/// model descriptors and compressed transforms; no model is selected by its name.</summary>
public sealed class CompiledTranslocatorData
{
    public readonly record struct Descriptor(ulong ObjectId, float Range, float RangeSquared);
    public readonly record struct Placement(int Index, bool GlobalDescriptor, int DescriptorIndex,
        int ModificationIndex, Matrix4x4 Transform, Vector3 BoundsMin, Vector3 BoundsMax);
    public ulong ResourceId { get; }
    public ulong GlobalResourceId { get; }
    public IReadOnlyList<Descriptor> Descriptors { get; }
    public IReadOnlyList<Placement> Placements { get; }

    private CompiledTranslocatorData(ulong id, ulong global, Descriptor[] descriptors, Placement[] placements)
        => (ResourceId, GlobalResourceId, Descriptors, Placements) = (id, global, descriptors, placements);

    public static CompiledTranslocatorData Read(CompiledGraph graph, CompiledElement resource)
    {
        if (resource.TypeId != 0x1A24D577) throw new NotSupportedException("Not a native translocator resource.");
        CompiledElement Required(CompiledElement node, string name) => graph.Field(node, name)
            ?? throw new InvalidDataException($"Translocator lacks {name}.");
        float Scalar(CompiledElement node, string name)
        {
            var data = graph.Data(Required(node, name));
            if (data.Length != 4) throw new InvalidDataException("Invalid translocator scalar.");
            float value = BinaryPrimitives.ReadSingleLittleEndian(data);
            return float.IsFinite(value) ? value : throw new InvalidDataException("Nonfinite translocator scalar.");
        }
        ulong Reference(CompiledElement field)
        {
            if (field.Event != 4 || field.DataSize != 12) throw new InvalidDataException("Invalid translocator object reference.");
            return BinaryPrimitives.ReadUInt64LittleEndian(graph.Data(field));
        }
        var descriptors = graph.Items(Required(resource, "m_ObjectDescriptions")).Select(item => new Descriptor(
            Reference(Required(item, "m_Object")), Scalar(item, "m_Range"), Scalar(item, "m_RangeSqr"))).ToArray();
        var converter = graph.Data(Required(resource, "m_CompressedDataConverter"));
        if (converter.Length != 84) throw new NotSupportedException("Unsupported native translocator converter.");
        var coefficients = new float[21];
        for (int i = 0; i < coefficients.Length; i++)
        {
            coefficients[i] = BinaryPrimitives.ReadSingleLittleEndian(converter[(i * 4)..]);

        }
        ulong global = Reference(Required(resource, "m_GlobalResource"));
        var tree = graph.Data(Required(resource, "m_Tree"));
        if (tree.Length % 48 != 0 || tree.Length / 48 > 2_000_000) throw new InvalidDataException("Invalid translocator KD-tree length.");
        int modificationCount = graph.Items(Required(resource, "m_LocalModifications")).Count;
        var placements = new Placement[tree.Length / 48];
        // Serialized maxima are compiler bookkeeping; runtime unpacking consumes only minima/ranges.
        // Empty trees consume no converter fields (retail empty resources may contain NaN sentinels).
        if (placements.Length != 0) ValidateCoefficients(coefficients);
        for (int i = 0; i < placements.Length; i++)
        {
            var row = tree.Slice(i * 48, 48);
            bool shared = (row[0] & 128) != 0;
            int descriptor = ((row[0] & 127) << 8) | row[1];
            int modification = BinaryPrimitives.ReadUInt16LittleEndian(row[17..]);
            if (!shared && descriptor >= descriptors.Length || shared && global == 0 || modification != 65535 && modification >= modificationCount)
                throw new InvalidDataException("Translocator placement index is out of range.");
            var minimum = new Vector3(F(row, 20), F(row, 24), F(row, 28));
            var maximum = new Vector3(F(row, 32), F(row, 36), F(row, 40));
            if (!Finite(minimum) || !Finite(maximum)) throw new InvalidDataException("Nonfinite translocator bounds.");
            placements[i] = new Placement(i, shared, descriptor, modification == 65535 ? -1 : modification,
                DecodeTransform(row, coefficients), minimum, maximum);
        }
        return new(resource.ObjectId, global, descriptors, placements);
    }

    /// <summary>Matches the native 22/22/18-bit position, three 16-bit quaternion components,
    /// positive quaternion W and 10-bit uniform scale. Converter coefficients are the native 21 floats.</summary>
    public static Matrix4x4 DecodeTransform(ReadOnlySpan<byte> row, ReadOnlySpan<float> coefficients)
    {
        if (row.Length < 19 || coefficients.Length != 21) throw new ArgumentException("Invalid native translocator transform input.");
        ValidateCoefficients(coefficients);
        int px = (row[2] << 14) | (row[3] << 6) | (row[4] >> 2);
        int py = ((row[4] & 3) << 20) | (row[5] << 12) | (row[6] << 4) | (row[7] >> 4);
        int pz = ((row[7] & 15) << 14) | (row[8] << 6) | (row[9] >> 2);
        float x = px / 4194303f * coefficients[6] + coefficients[0];
        float y = py / 4194303f * coefficients[7] + coefficients[1];
        float z = pz / 262143f * coefficients[8] + coefficients[2];
        const float quaternionReciprocal = 1.5259021893143654e-5f;
        float qx = BinaryPrimitives.ReadUInt16BigEndian(row[11..]) * quaternionReciprocal * coefficients[15] + coefficients[9];
        float qy = BinaryPrimitives.ReadUInt16BigEndian(row[13..]) * quaternionReciprocal * coefficients[16] + coefficients[10];
        float qz = BinaryPrimitives.ReadUInt16BigEndian(row[15..]) * quaternionReciprocal * coefficients[17] + coefficients[11];
        float qw = MathF.Sqrt(MathF.Abs(((1 - qx * qx) - qy * qy) - qz * qz));
        float scale = (BinaryPrimitives.ReadUInt16BigEndian(row[9..]) & 1023) / 1023f * coefficients[20] + coefficients[18];
        var rotation = Matrix4x4.Identity;
        if (MathF.Abs(qw) < 1)
        {
            float xx = qx * (qx + qx), xy = qx * (qy + qy), xz = qx * (qz + qz);
            float yy = qy * (qy + qy), yz = qy * (qz + qz), zz = qz * (qz + qz);
            float wx = qw * (qx + qx), wy = qw * (qy + qy), wz = qw * (qz + qz);
            rotation = new(1 - (yy + zz), xy - wz, wy + xz, 0,
                xy + wz, 1 - (xx + zz), yz - wx, 0,
                xz - wy, wx + yz, 1 - (xx + yy), 0, 0, 0, 0, 1);
        }
        rotation.M11 *= scale; rotation.M12 *= scale; rotation.M13 *= scale;
        rotation.M21 *= scale; rotation.M22 *= scale; rotation.M23 *= scale;
        rotation.M31 *= scale; rotation.M32 *= scale; rotation.M33 *= scale;
        rotation.Translation = new(x, y, z);
        if (!Finite(rotation.Translation) || !float.IsFinite(rotation.M11) || !float.IsFinite(rotation.M12) || !float.IsFinite(rotation.M13)
            || !float.IsFinite(rotation.M21) || !float.IsFinite(rotation.M22) || !float.IsFinite(rotation.M23)
            || !float.IsFinite(rotation.M31) || !float.IsFinite(rotation.M32) || !float.IsFinite(rotation.M33))
            throw new InvalidDataException("Nonfinite native translocator transform.");
        return rotation;
    }

    private static void ValidateCoefficients(ReadOnlySpan<float> coefficients)
    {
        foreach (int index in (ReadOnlySpan<int>)[0, 1, 2, 6, 7, 8, 9, 10, 11, 15, 16, 17, 18, 20])
            if (!float.IsFinite(coefficients[index])) throw new InvalidDataException("Nonfinite consumed translocator converter field.");
    }

    private static float F(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
