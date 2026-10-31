using System.Buffers.Binary;
using System.Numerics;
using Illusion.Bridge.Payload;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledMeshBinding
{
    private sealed record AttributeSlot(Stream Stream, int Offset, bool Color, IReadOnlyList<int> UvKeys);

    private static IReadOnlyList<AttributeSlot> AttributeSlots(Context context)
    {
        List<AttributeSlot> result = [];
        var uvSlots = new Dictionary<(Stream Stream, int Offset), List<int>>();
        int nextSemantic = 0; bool primary = true;
        foreach (var stream in context.Streams)
        {
            if (stream.Layout.Color >= 0 && !result.Any(s => s.Color))
                result.Add(new(stream, stream.Layout.Color, true, []));
            int offset = stream.Layout.Uv;
            for (int channel = 0; channel < 5; channel++)
            {
                bool alias = channel == 3 && (stream.Layout.Flags & 0x1800) == 0x1000;
                if ((stream.Layout.Flags & (256u << channel)) == 0 && !alias) continue;
                int key = primary ? 0 : nextSemantic; primary = false; nextSemantic++;
                var location = (stream, offset);
                if (!uvSlots.TryGetValue(location, out var keys)) uvSlots.Add(location, keys = []);
                keys.Add(key);
                if (!alias) offset += 4;
            }
        }
        foreach (var (location, keys) in uvSlots)
            result.Add(new(location.Stream, location.Offset, false, keys));
        var allKeys = result.Where(s => !s.Color).SelectMany(s => s.UvKeys).ToArray();
        if (allKeys.Distinct().Count() != allKeys.Length)
            throw new NotSupportedException("Overlapping native UV channels cannot be authored independently.");
        return result;
    }

    private static ReadOnlySpan<byte> AttributeSource(AttributeSlot slot, int donor) =>
        slot.Stream.Original.AsSpan(slot.Stream.Offset + donor * slot.Stream.Layout.Stride + slot.Offset, 4);
    private static Vector2 AttributeUv(ReadOnlySpan<byte> bytes) => new(
        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes)),
        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..])));
    private static Vector4 AttributeColor(ReadOnlySpan<byte> bytes) => new Vector4(bytes[2], bytes[1], bytes[0], bytes[3]) / 255f;

    private static void ExportAttributes(MeshObjectPayload payload, Context context)
    {
        foreach (var slot in AttributeSlots(context))
            if (slot.Color)
                payload.LoopColors = context.Mesh.Indices.Select(v => AttributeColor(AttributeSource(slot, checked((int)v)))).ToArray();
            else
                foreach (int key in slot.UvKeys.Where(k => k > 0))
                    payload.ExtraLoopUvs.Add(key, context.Mesh.Indices.Select(v =>
                    {
                        var value = AttributeUv(AttributeSource(slot, checked((int)v)));
                        return new Vector2(value.X, 1 - value.Y);
                    }).ToArray());
    }

    private static void ValidateAttributes(MeshObjectPayload payload, Context context)
    {
        int loops = payload.LoopVertexIndices.Length;
        if (payload.LoopColors.Length != 0 && payload.LoopColors.Length != loops
            || payload.ExtraLoopUvs.Values.Any(values => values.Length != loops))
            throw new InvalidDataException("Native corner colors and additional UV arrays must match the triangle corner count.");
        var slots = AttributeSlots(context);
        if (payload.LoopColors.Length > 0 && !slots.Any(s => s.Color))
            throw new InvalidDataException("The native vertex declaration has no COLOR0 channel.");
        var keys = slots.Where(s => !s.Color).SelectMany(s => s.UvKeys).ToHashSet();
        if (payload.ExtraLoopUvs.Keys.Any(key => key <= 0 || !keys.Contains(key)))
            throw new InvalidDataException("An authored UV channel is absent from the native declaration; edit the exported UV layers.");
    }

    private sealed class AttributeEditor
    {
        private readonly MeshObjectPayload _payload;
        private readonly Context _context;
        private readonly IReadOnlyList<AttributeSlot> _slots;
        public AttributeEditor(MeshObjectPayload payload, Context context)
        {
            ValidateAttributes(payload, context); _payload = payload; _context = context;
            _slots = AttributeSlots(context).Where(s => s.Color || s.UvKeys.Any(k => k > 0)).ToArray();
        }
        private byte[] Encode(AttributeSlot slot, int corner, int donor)
        {
            byte[] original = AttributeSource(slot, donor).ToArray();
            if (corner < 0) return original;
            if (slot.Color)
            {
                if (_payload.LoopColors.Length == 0) return original;
                var color = _payload.LoopColors[corner];
                static byte Channel(float value)
                {
                    if (!float.IsFinite(value) || value < -1e-6f || value > 1 + 1e-6f)
                        throw new InvalidDataException("Native COLOR0 components must be finite values between 0 and 1.");
                    return (byte)MathF.Round(Math.Clamp(value, 0, 1) * 255);
                }
                return [Channel(color.Z), Channel(color.Y), Channel(color.X), Channel(color.W)];
            }
            byte[] result = original;
            foreach (int key in slot.UvKeys)
            {
                Vector2 input;
                if (key == 0) input = _payload.LoopUvs[corner];
                else if (_payload.ExtraLoopUvs.TryGetValue(key, out var values)) input = values[corner];
                else continue;
                if (!float.IsFinite(input.X) || !float.IsFinite(input.Y)) throw new InvalidDataException("Native UV values must be finite.");
                var baseline = AttributeUv(original);
                var expected = new Vector2(baseline.X, 1 - baseline.Y);
                byte[] packed;
                // Retain original half bits (including signed zero) when Blender returns its exact imported value.
                if (input == expected) packed = original;
                else
                {
                    Half x = (Half)input.X, y = (Half)(1 - input.Y);
                    if (!Half.IsFinite(x) || !Half.IsFinite(y)) throw new InvalidDataException("Native UV exceeds the half-float range.");
                    packed = new byte[4]; BinaryPrimitives.WriteUInt16LittleEndian(packed, BitConverter.HalfToUInt16Bits(x));
                    BinaryPrimitives.WriteUInt16LittleEndian(packed.AsSpan(2), BitConverter.HalfToUInt16Bits(y));
                }
                if (packed.AsSpan().SequenceEqual(original)) continue;
                if (!result.AsSpan().SequenceEqual(original) && !result.AsSpan().SequenceEqual(packed))
                    throw new InvalidDataException("Two authored UV layers share the same native storage and contain conflicting edits.");
                result = packed;
            }
            return result;
        }
        public string Key(int corner, int donor) => Convert.ToHexString(_slots.SelectMany(s => Encode(s, corner, donor)).ToArray());
        public Vector2? PrimaryUv(int corner, int donor)
        {
            var slot = _slots.FirstOrDefault(s => !s.Color && s.UvKeys.Contains(0));
            return slot is null ? null : AttributeUv(Encode(slot, corner, donor));
        }
        public void Write(Stream stream, Span<byte> target, int corner, int donor)
        {
            foreach (var slot in _slots.Where(s => ReferenceEquals(s.Stream, stream)))
                Encode(slot, corner, donor).CopyTo(target[slot.Offset..]);
        }
        public Vector4[]? Colors(IReadOnlyList<int> corners, IReadOnlyList<int> donors)
        {
            var color = _slots.FirstOrDefault(s => s.Color);
            if (color is null || _context.Mesh.Colors is null && _payload.LoopColors.Length == 0) return null;
            return Enumerable.Range(0, donors.Count).Select(i => AttributeColor(Encode(color, corners[i], donors[i]))).ToArray();
        }
    }
}
