using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Illusion.Formats.Animations;

/// <summary>Native PC64 Havok 2014.2 authored frame: parent-local tracks and raw float-slot values.</summary>
public sealed record HavokAnimationFrame(Matrix4x4[] Transforms, float[] Floats);

/// <summary>Rebuilds an existing native binding as an interleaved clip. Spline sources are sampled
/// explicitly; the resulting keys use native linear TRS interpolation, not spline recompression.</summary>
public static class HavokAnimationAuthoring
{
    public static byte[] SetTransformFrame(ReadOnlySpan<byte> input, int clip, int frame, int track, Matrix4x4 transform)
    {
        var original = Select(input, clip);
        if ((uint)frame >= original.FrameCount || (uint)track >= original.TrackCount) throw new ArgumentOutOfRangeException(nameof(frame));
        var pack = new Pack(input); var animation = FindAnimation(pack, clip);
        if (pack.Classes[animation].Name == "hkaInterleavedUncompressedAnimation")
        {
            var existing = pack.Read(pack.Pointer(animation, 56), checked((frame * original.TrackCount + track) * 48), 48);
            if (ReadTransform(existing) == transform) return input.ToArray();
            byte[] encoded = new byte[48]; WriteTransform(encoded, transform);
            return pack.ReplaceKey(input, animation, 56, checked((frame * original.TrackCount + track) * 48), encoded);
        }
        var frames = Capture(original);
        frames[frame].Transforms[track] = transform;
        return Bake(input, clip, frames, original.Duration);
    }

    public static byte[] SetFloatFrame(ReadOnlySpan<byte> input, int clip, int frame, int track, float value)
    {
        var original = Select(input, clip);
        if ((uint)frame >= original.FrameCount || (uint)track >= original.FloatTrackCount || !float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(frame));
        var pack = new Pack(input); var animation = FindAnimation(pack, clip);
        if (pack.Classes[animation].Name == "hkaInterleavedUncompressedAnimation")
        {
            byte[] encoded = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(encoded, value);
            return pack.ReplaceKey(input, animation, 72, checked((frame * original.FloatTrackCount + track) * 4), encoded);
        }
        var frames = Capture(original);
        frames[frame].Floats[track] = value;
        return Bake(input, clip, frames, original.Duration);
    }

    public static HavokAnimationFrame[] Capture(HavokAnimationClip clip)
    {
        if (clip.FrameCount is < 1 or > 36000 || (long)clip.FrameCount * (clip.TrackCount * 48L + clip.FloatTrackCount * 4L) > 512L * 1024 * 1024)
            throw new NotSupportedException("Native animation authoring is limited to 36000 frames and 512 MiB of interleaved samples.");
        return Enumerable.Range(0, clip.FrameCount).Select(frame =>
        {
            float time = clip.FrameCount == 1 ? 0 : clip.Duration * frame / (clip.FrameCount - 1);
            return new HavokAnimationFrame(clip.Sample(time), clip.SampleFloats(time));
        }).ToArray();
    }

    /// <summary>Reads actual interleaved key bytes rather than sampling a rounded floating time.
    /// Spline inputs expose the explicit native-frame sample that will be baked on first edit.</summary>
    public static HavokAnimationFrame ReadFrame(ReadOnlySpan<byte> input, int clip, int frame)
    {
        var original = Select(input, clip); if ((uint)frame >= original.FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
        var pack = new Pack(input); var animation = FindAnimation(pack, clip, requireUnique: false);
        if (pack.Classes[animation].Name != "hkaInterleavedUncompressedAnimation")
        {
            float time = original.FrameCount == 1 ? 0 : original.Duration * frame / (original.FrameCount - 1);
            return new(original.Sample(time), original.SampleFloats(time));
        }
        var transforms = new Matrix4x4[original.TrackCount]; var floats = new float[original.FloatTrackCount];
        var raw = pack.Read(pack.Pointer(animation, 56), checked(frame * original.TrackCount * 48), checked(original.TrackCount * 48));
        for (int i = 0; i < transforms.Length; i++) transforms[i] = ReadTransform(raw.AsSpan(i * 48, 48));
        if (floats.Length > 0)
        {
            raw = pack.Read(pack.Pointer(animation, 72), checked(frame * floats.Length * 4), checked(floats.Length * 4));
            for (int i = 0; i < floats.Length; i++) floats[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4));
        }
        return new(transforms, floats);
    }

    private static Matrix4x4 ReadTransform(ReadOnlySpan<byte> input)
    {
        static float F(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
        var rotation = Quaternion.Normalize(new Quaternion(F(input, 16), F(input, 20), F(input, 24), F(input, 28)));
        return Matrix4x4.CreateScale(F(input, 32), F(input, 36), F(input, 40)) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(F(input, 0), F(input, 4), F(input, 8));
    }

    private static HavokAnimationClip Select(ReadOnlySpan<byte> input, int index)
    {
        var clips = HavokAnimationClip.Read(input);
        return (uint)index < clips.Count ? clips[index] : throw new ArgumentOutOfRangeException(nameof(index));
    }

    public static byte[] Bake(ReadOnlySpan<byte> input, int clip, IReadOnlyList<HavokAnimationFrame> frames, float duration)
    {
        var source = Select(input, clip);
        if (!float.IsFinite(duration) || duration <= 0 || frames.Count is < 1 or > 36000
            || (long)frames.Count * (source.TrackCount * 48L + source.FloatTrackCount * 4L) > 512L * 1024 * 1024
            || frames.Any(frame => frame.Transforms.Length != source.TrackCount || frame.Floats.Length != source.FloatTrackCount))
            throw new ArgumentException("Native frame counts, duration or track extents are invalid.", nameof(frames));
        var pack = new Pack(input);
        var animation = FindAnimation(pack, clip);
        var transformBytes = new byte[checked(frames.Count * source.TrackCount * 48)];
        var floatBytes = new byte[checked(frames.Count * source.FloatTrackCount * 4)];
        for (int frame = 0; frame < frames.Count; frame++)
        {
            for (int track = 0; track < source.TrackCount; track++)
                WriteTransform(transformBytes.AsSpan((frame * source.TrackCount + track) * 48, 48), frames[frame].Transforms[track]);
            for (int track = 0; track < source.FloatTrackCount; track++)
            {
                float value = frames[frame].Floats[track]; if (!float.IsFinite(value)) throw new ArgumentException("Native float keys must be finite.", nameof(frames));
                BinaryPrimitives.WriteSingleLittleEndian(floatBytes.AsSpan((frame * source.FloatTrackCount + track) * 4), value);
            }
        }
        pack.UInt(animation, 16, 1); pack.UInt(animation, 20, BitConverter.SingleToUInt32Bits(duration));
        pack.ReplaceArray(animation, 56, transformBytes, checked(frames.Count * source.TrackCount), forceNew: true);
        pack.ReplaceArray(animation, 72, floatBytes, checked(frames.Count * source.FloatTrackCount), forceNew: true);
        pack.SetClass(animation, "hkaInterleavedUncompressedAnimation", 0xa5eff3f2);
        byte[] result = pack.Finish();
        var verified = HavokAnimationClip.Read(result);
        if (verified.Count != HavokAnimationClip.Read(input).Count || verified[clip].FrameCount != frames.Count
            || verified[clip].TrackCount != source.TrackCount || verified[clip].FloatTrackCount != source.FloatTrackCount
            || !verified[clip].TrackToBone.SequenceEqual(source.TrackToBone) || !verified[clip].FloatTrackToSlot.SequenceEqual(source.FloatTrackToSlot)
            || verified[clip].BlendHint != source.BlendHint || verified[clip].SkeletonName != source.SkeletonName)
            throw new InvalidDataException("Native animation rewrite failed binding validation.");
        return result;
    }

    private static Location FindAnimation(Pack pack, int clip, bool requireUnique = true)
    {
        var bindings = pack.Classes.Where(pair => pair.Value.Name == "hkaAnimationBinding")
            .Where(pair => pack.Classes.TryGetValue(pack.Pointer(pair.Key, 24), out var type)
                && type.Name is "hkaInterleavedUncompressedAnimation" or "hkaSplineCompressedAnimation").ToArray();
        if ((uint)clip >= bindings.Length) throw new InvalidDataException("Native binding selection is invalid.");
        var animation = pack.Pointer(bindings[clip].Key, 24);
        if (requireUnique && bindings.Count(binding => pack.Pointer(binding.Key, 24) == animation) != 1)
            throw new NotSupportedException("The native animation object is shared by multiple bindings; author a separate clip first.");
        return animation;
    }

    private static void WriteTransform(Span<byte> output, Matrix4x4 value)
    {
        if (!Matrix4x4.Decompose(value, out var scale, out var rotation, out var translation) || rotation.LengthSquared() < .000001f)
            throw new ArgumentException("Animation transforms must be finite decomposable TRS matrices.");
        rotation = Quaternion.Normalize(rotation);
        var rebuilt = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
        var actual = new[] { value.M11,value.M12,value.M13,value.M14,value.M21,value.M22,value.M23,value.M24,value.M31,value.M32,value.M33,value.M34,value.M41,value.M42,value.M43,value.M44 };
        var expected = new[] { rebuilt.M11,rebuilt.M12,rebuilt.M13,rebuilt.M14,rebuilt.M21,rebuilt.M22,rebuilt.M23,rebuilt.M24,rebuilt.M31,rebuilt.M32,rebuilt.M33,rebuilt.M34,rebuilt.M41,rebuilt.M42,rebuilt.M43,rebuilt.M44 };
        if (actual.Where((number, index) => !float.IsFinite(number) || MathF.Abs(number - expected[index]) > .0001f * MathF.Max(1, MathF.Abs(number))).Any())
            throw new NotSupportedException("Native animation authoring does not encode shear or projective transforms.");
        float[] values = [translation.X,translation.Y,translation.Z,0,rotation.X,rotation.Y,rotation.Z,rotation.W,scale.X,scale.Y,scale.Z,0];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(output[(i * 4)..], values[i]);
    }

    private readonly record struct Location(int Section, int Offset);
    private sealed class Pack
    {
        private sealed class Section
        {
            internal required byte[] Header;
            internal required MemoryStream Data;
            internal readonly Dictionary<int, Location> Pointers = [];
            internal readonly Dictionary<int, Location> Virtuals = [];
        }
        private readonly byte[] _prefix, _header;
        private readonly Section[] _sections;
        internal Dictionary<Location, (string Name, uint Signature)> Classes { get; } = [];
        internal Pack(ReadOnlySpan<byte> input)
        {
            int magic = input[..Math.Min(input.Length, 160)].IndexOf(new byte[] { 0x57,0xe0,0xe0,0x57,0x10,0xc0,0xc0,0x10 });
            if (magic < 0) throw new InvalidDataException("No native Havok packfile.");
            _prefix = input[..magic].ToArray(); byte[] raw = input[magic..].ToArray();
            _ = HavokAnimationClip.Read(raw);
            if (magic != 0 && (magic < 20 || BinaryPrimitives.ReadUInt32LittleEndian(_prefix) != 1
                || BinaryPrimitives.ReadInt32LittleEndian(_prefix.AsSpan(12)) != magic - 20
                || BinaryPrimitives.ReadInt32LittleEndian(_prefix.AsSpan(magic - 4)) != raw.Length))
                throw new NotSupportedException("Unknown outer native animation resource wrapper.");
            int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at));
            int count = I(20); _header = raw.AsSpan(0, 80).ToArray(); _sections = new Section[count];
            for (int s = 0; s < count; s++)
            {
                int h = 80 + s * 64, start = I(h + 20), local = I(h + 24), global = I(h + 28), virtuals = I(h + 32), end = I(h + 36);
                if (I(h + 40) != end || I(h + 44) != end) throw new NotSupportedException("Native animation exports/imports require an explicit symbol-table rewrite.");
                var section = _sections[s] = new Section { Header = raw.AsSpan(h, 64).ToArray(), Data = new MemoryStream() };
                section.Data.Write(raw.AsSpan(start, local));
                for (int p = local; p <= global - 8; p += 8) if (I(start + p) >= 0) section.Pointers.Add(I(start + p), new(s, I(start + p + 4)));
                for (int p = global; p <= virtuals - 12; p += 12) if (I(start + p) >= 0) section.Pointers.Add(I(start + p), new(I(start + p + 4), I(start + p + 8)));
                for (int p = virtuals; p <= end - 12; p += 12) if (I(start + p) >= 0) section.Virtuals.Add(I(start + p), new(I(start + p + 4), I(start + p + 8)));
            }
            for (int s = 0; s < count; s++) foreach (var pair in _sections[s].Virtuals)
            {
                var bytes = _sections[pair.Value.Section].Data.ToArray(); int offset = pair.Value.Offset;
                int end = Array.IndexOf(bytes, (byte)0, offset);
                if (offset < 5 || end < offset || bytes[offset - 1] != 9) throw new InvalidDataException("Malformed native virtual class name.");
                Classes.Add(new(s, pair.Key), (Encoding.ASCII.GetString(bytes, offset, end - offset), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset - 5))));
            }
        }
        internal Location Pointer(Location owner, int offset) => _sections[owner.Section].Pointers.GetValueOrDefault(owner.Offset + offset,
            new(-1, -1)) is var pointer && pointer.Section >= 0 ? pointer : throw new InvalidDataException("Missing native animation pointer.");
        private void Copy(Location target, int offset, ReadOnlySpan<byte> value)
        {
            var stream = _sections[target.Section].Data; long position = stream.Position;
            stream.Position = checked(target.Offset + offset); stream.Write(value); stream.Position = position;
        }
        internal byte[] Read(Location target, int offset, int count)
        {
            var stream = _sections[target.Section].Data; int at = checked(target.Offset + offset);
            if (at < 0 || count < 0 || at > stream.Length - count) throw new InvalidDataException("Native animation key exceeds its sample allocation.");
            return stream.GetBuffer().AsSpan(at, count).ToArray();
        }
        internal byte[] ReplaceKey(ReadOnlySpan<byte> original, Location owner, int fieldOffset, int offset, byte[] value)
        {
            var target = Pointer(owner, fieldOffset);
            var stream = _sections[target.Section].Data;
            int count = BinaryPrimitives.ReadInt32LittleEndian(_sections[owner.Section].Data.GetBuffer().AsSpan(owner.Offset + fieldOffset + 8));
            int length = checked(count * value.Length);
            int at = checked(target.Offset + offset);
            if (offset < 0 || offset > length - value.Length || target.Offset < 0 || target.Offset > stream.Length - length)
                throw new InvalidDataException("The native key exceeds its sample allocation.");
            if (stream.GetBuffer().AsSpan(at, value.Length).SequenceEqual(value)) return original.ToArray();
            bool shared = _sections.SelectMany((section, index) => section.Pointers.Select(pair => (Source: new Location(index, pair.Key), Target: pair.Value)))
                .Any(pair => pair.Source != new Location(owner.Section, owner.Offset + fieldOffset) && pair.Target.Section == target.Section
                    && pair.Target.Offset >= target.Offset && pair.Target.Offset < target.Offset + length);
            foreach (var entry in Classes.Where(pair => pair.Value.Name is "hkaInterleavedUncompressedAnimation" or "hkaSkeleton"))
                foreach (var (field, stride) in new[] { (56, 48), (72, 4) })
                {
                    if (entry.Key == owner && field == fieldOffset || !_sections[entry.Key.Section].Pointers.TryGetValue(entry.Key.Offset + field, out var data) || data.Section != target.Section) continue;
                    var bytes = _sections[entry.Key.Section].Data.GetBuffer(); int otherCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(entry.Key.Offset + field + 8));
                    if (otherCount > 0 && (long)data.Offset < (long)target.Offset + length && (long)data.Offset + (long)otherCount * stride > target.Offset) shared = true;
                }
            if (shared)
            {
                var copy = stream.GetBuffer().AsSpan(target.Offset, length).ToArray(); value.CopyTo(copy, offset); ReplaceArray(owner, fieldOffset, copy, count, forceNew: true);
                var isolated = Finish(); _ = HavokAnimationClip.Read(isolated); return isolated;
            }
            Copy(target, offset, value); var result = Finish(); _ = HavokAnimationClip.Read(result); return result;
        }
        internal void UInt(Location owner, int offset, uint value)
        { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); Copy(owner, offset, bytes); }
        internal void ReplaceArray(Location owner, int offset, byte[] data, int count, bool forceNew = false)
        {
            var section = _sections[owner.Section]; int target;
            int oldCount = BinaryPrimitives.ReadInt32LittleEndian(section.Data.GetBuffer().AsSpan(owner.Offset + offset + 8));
            if (!forceNew && count > 0 && oldCount == count && section.Pointers.TryGetValue(owner.Offset + offset, out var existing)
                && existing.Section == owner.Section && existing.Offset >= 0 && existing.Offset <= section.Data.Length - data.Length
                && _sections.Sum(candidate => candidate.Pointers.Values.Count(pointer => pointer == existing)) == 1)
            { target = existing.Offset; Copy(existing, 0, data); }
            else
            {
                section.Data.Position = section.Data.Length; while ((section.Data.Length & 15) != 0) section.Data.WriteByte(0);
                target = checked((int)section.Data.Length); section.Data.Write(data);
            }
            Copy(owner, offset, new byte[8]); UInt(owner, offset + 8, checked((uint)count)); UInt(owner, offset + 12, checked((uint)count) | 0x80000000);
            section.Pointers.Remove(owner.Offset + offset); if (count > 0) section.Pointers.Add(owner.Offset + offset, new(owner.Section, target));
        }
        internal void SetClass(Location owner, string name, uint signature)
        {
            if (!Classes.TryGetValue(owner, out var old) || old.Name is not ("hkaSplineCompressedAnimation" or "hkaInterleavedUncompressedAnimation"))
                throw new NotSupportedException("Only a native supported animation class may be rebuilt.");
            var existing = Classes.FirstOrDefault(pair => pair.Value.Name == name && pair.Value.Signature == signature);
            Location target;
            if (existing.Value.Name is not null) target = _sections[existing.Key.Section].Virtuals[existing.Key.Offset];
            else
            {
                int section = _sections[owner.Section].Virtuals[owner.Offset].Section; var data = _sections[section].Data;
                data.Position = data.Length; Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, signature); data.Write(bytes); data.WriteByte(9);
                target = new(section, checked((int)data.Position)); data.Write(Encoding.ASCII.GetBytes(name)); data.WriteByte(0);
            }
            _sections[owner.Section].Virtuals[owner.Offset] = target;
        }
        internal byte[] Finish()
        {
            using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
            writer.Write(_header); foreach (var section in _sections) writer.Write(section.Header);
            void Pad(byte value = 0xff) { while ((output.Position & 15) != 0) writer.Write(value); }
            for (int s = 0; s < _sections.Length; s++)
            {
                var section = _sections[s]; Pad(); int start = checked((int)output.Position); writer.Write(section.Data.ToArray());
                Pad(); int local = checked((int)output.Position - start);
                foreach (var pair in section.Pointers.Where(pair => pair.Value.Section == s).OrderBy(pair => pair.Key)) { writer.Write(pair.Key); writer.Write(pair.Value.Offset); }
                Pad(); int global = checked((int)output.Position - start);
                foreach (var pair in section.Pointers.Where(pair => pair.Value.Section != s).OrderBy(pair => pair.Key)) { writer.Write(pair.Key); writer.Write(pair.Value.Section); writer.Write(pair.Value.Offset); }
                Pad(); int virtuals = checked((int)output.Position - start);
                // Binding discovery follows native virtual-record order. Preserve it so a key edit
                // cannot silently renumber other clips in a multi-binding animation resource.
                foreach (var pair in section.Virtuals) { writer.Write(pair.Key); writer.Write(pair.Value.Section); writer.Write(pair.Value.Offset); }
                Pad(); int end = checked((int)output.Position - start); long position = output.Position;
                foreach (var pair in new[] { (20,start),(24,local),(28,global),(32,virtuals),(36,end),(40,end),(44,end) })
                { output.Position = 80 + s * 64 + pair.Item1; writer.Write(pair.Item2); }
                output.Position = position;
            }
            byte[] pack = output.ToArray(); _ = new Compiled.HavokMeshPackfile(pack);
            var result = new byte[checked(_prefix.Length + pack.Length)]; _prefix.CopyTo(result, 0); pack.CopyTo(result, _prefix.Length);
            if (_prefix.Length > 0) BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(_prefix.Length - 4), pack.Length);
            return result;
        }
    }
}
