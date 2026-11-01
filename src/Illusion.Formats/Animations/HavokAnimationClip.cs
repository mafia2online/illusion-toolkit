using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Illusion.Formats.Animations;

public sealed record HavokAnimationSkeleton(string Name, string[] BoneNames, short[] Parents, Matrix4x4[] ReferenceLocal)
{
    /// <summary>Native hkaBone.lockTranslation flags; absent only for callers constructing an explicit skeleton.</summary>
    public bool[] TranslationLocked { get; init; } = [];
    public string[] FloatSlotNames { get; init; } = [];
    public float[] ReferenceFloats { get; init; } = [];
}

/// <summary>A native Mafia III Havok 2014.2 interleaved or packed spline clip with its explicit track-to-bone binding.</summary>
public sealed partial class HavokAnimationClip
{
    private readonly Transform[] _samples;
    private readonly Func<float, Matrix4x4[]>? _sampler;
    private readonly int _frameCount;
    private Func<float, float[]>? _floatSampler;
    public int FloatTrackCount => FloatTrackToSlot.Count;
    public IReadOnlyList<short> FloatTrackToSlot { get; private set; } = [];
    /// <summary>Native scalar values, before the game morph updater divides them by 100.</summary>
    public float[] SampleFloats(float seconds)
    {
        if (!float.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        return _floatSampler?.Invoke(Math.Clamp(seconds, 0, Duration)) ?? [];
    }
    public string SkeletonName { get; }
    public float Duration { get; }
    /// <summary>Native binding: 0 normal, 1 deprecated additive order, 2 additive.</summary>
    public byte BlendHint { get; private set; }
    public int TrackCount { get; }
    public int FrameCount => _frameCount;
    public IReadOnlyList<short> TrackToBone { get; }
    private readonly record struct Transform(Vector3 Translation, Quaternion Rotation, Vector3 Scale);
    private HavokAnimationClip(string name, float duration, int tracks, short[] mapping, Transform[] samples)
    { SkeletonName = name; Duration = duration; TrackCount = tracks; TrackToBone = mapping; _samples = samples; _frameCount = samples.Length / tracks; }

    private HavokAnimationClip(string name, float duration, int tracks, short[] mapping, int frames, Func<float, Matrix4x4[]> sampler)
    { SkeletonName = name; Duration = duration; TrackCount = tracks; TrackToBone = mapping; _samples = []; _frameCount = frames; _sampler = sampler; }

    /// <summary>Samples native parent-local tracks; additive bindings return deltas to compose with ApplyAdditive.</summary>
    public Matrix4x4[] Sample(float seconds)
    {
        if (!float.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (_sampler is not null) return _sampler(Math.Clamp(seconds, 0, Duration));
        float position = Math.Clamp(seconds / Duration, 0, 1) * (FrameCount - 1);
        int first = Math.Min((int)position, FrameCount - 1), next = Math.Min(first + 1, FrameCount - 1);
        float blend = position - first;
        var result = new Matrix4x4[TrackCount];
        for (int i = 0; i < result.Length; i++)
        {
            var a = _samples[first * TrackCount + i]; var b = _samples[next * TrackCount + i];
            result[i] = Matrix4x4.CreateScale(Vector3.Lerp(a.Scale, b.Scale, blend))
                * Matrix4x4.CreateFromQuaternion(Quaternion.Lerp(a.Rotation, b.Rotation, blend))
                * Matrix4x4.CreateTranslation(Vector3.Lerp(a.Translation, b.Translation, blend));
        }
        return result;
    }

    public static IReadOnlyList<HavokAnimationClip> Read(ReadOnlySpan<byte> input) => Read(input, null);

    public static IReadOnlyList<HavokAnimationSkeleton> ReadSkeletons(ReadOnlySpan<byte> input)
    {
        var result = new List<HavokAnimationSkeleton>();
        _ = Read(input, result);
        return result;
    }

    private static IReadOnlyList<HavokAnimationClip> Read(ReadOnlySpan<byte> input, List<HavokAnimationSkeleton>? skeletons)
    {
        // SDS hkAnimation has an outer class-name wrapper. Raw packfiles are accepted too.
        int magic = input[..Math.Min(input.Length, 160)].IndexOf(new byte[] { 0x57, 0xe0, 0xe0, 0x57, 0x10, 0xc0, 0xc0, 0x10 });
        if (magic < 0) throw new InvalidDataException("No Havok animation packfile header.");
        byte[] bytes = input[magic..].ToArray();
        void Check(int at, int size)
        { if (at < 0 || size < 0 || at > bytes.Length - size) throw new InvalidDataException("Havok animation extent exceeds its resource."); }
        int I(int at) { Check(at, 4); return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at)); }
        float F(int at) { Check(at, 4); float value = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at)); return float.IsFinite(value) ? value : throw new InvalidDataException("Non-finite animation sample."); }
        Check(0, 80);
        if (I(12) != 11 || bytes[16] != 8 || bytes[17] != 1 || !bytes.AsSpan(40, 14).SequenceEqual("hk_2014.2.0-r1"u8))
            throw new NotSupportedException("Only Mafia III Havok 2014.2 PC64 animations are supported.");
        int sectionCount = I(20);
        if (sectionCount is < 1 or > 32) throw new InvalidDataException("Invalid Havok section count.");
        Check(80, checked(sectionCount * 64));
        var sections = new (int Start, int Local, int Global, int Virtual, int End)[sectionCount];
        for (int s = 0; s < sectionCount; s++)
        {
            int h = 80 + s * 64;
            var section = (Start: I(h + 20), Local: I(h + 24), Global: I(h + 28), Virtual: I(h + 32), End: I(h + 36));
            if (section.Local < 0 || section.Global < section.Local || section.Virtual < section.Global || section.End < section.Virtual)
                throw new InvalidDataException("Invalid Havok animation fixup ranges.");
            Check(section.Start, section.End); sections[s] = section;
        }
        int Address(int section, int offset, int size = 1)
        {
            if ((uint)section >= sectionCount || offset < 0 || size < 0 || offset > sections[section].Local - size)
                throw new InvalidDataException("Invalid Havok animation object pointer.");
            return checked(sections[section].Start + offset);
        }
        var pointers = new Dictionary<int, int>();
        var classes = new Dictionary<int, string>();
        string Text(int address)
        {
            Check(address, 1); int end = Array.IndexOf(bytes, (byte)0, address);
            if (end < 0 || end - address > 4096) throw new InvalidDataException("Invalid Havok animation string.");
            return Encoding.UTF8.GetString(bytes, address, end - address);
        }
        for (int s = 0; s < sectionCount; s++)
        {
            var section = sections[s];
            for (int p = section.Local; p <= section.Global - 8; p += 8)
            {
                int source = I(section.Start + p), target = I(section.Start + p + 4);
                if (source >= 0) pointers[Address(s, source, 8)] = Address(s, target);
            }
            for (int p = section.Global; p <= section.Virtual - 12; p += 12)
            {
                int source = I(section.Start + p), targetSection = I(section.Start + p + 4), target = I(section.Start + p + 8);
                if (source >= 0) pointers[Address(s, source, 8)] = Address(targetSection, target);
            }
            for (int p = section.Virtual; p <= section.End - 12; p += 12)
            {
                int obj = I(section.Start + p), nameSection = I(section.Start + p + 4), name = I(section.Start + p + 8);
                if (obj >= 0) classes[Address(s, obj)] = Text(Address(nameSection, name));
            }
        }
        int Pointer(int source) => pointers.TryGetValue(source, out int target) ? target : throw new InvalidDataException("Missing Havok animation fixup.");
        Transform ReadTransform(int at)
        {
            var rotation = new Quaternion(F(at + 16), F(at + 20), F(at + 24), F(at + 28));
            if (rotation.LengthSquared() < .000001f) throw new InvalidDataException("Invalid native animation quaternion.");
            return new Transform(new(F(at), F(at + 4), F(at + 8)), Quaternion.Normalize(rotation), new(F(at + 32), F(at + 36), F(at + 40)));
        }
        if (skeletons is not null)
            foreach (var (skeleton, type) in classes)
            {
                if (type != "hkaSkeleton") continue;
                Check(skeleton, 100);
                int count = I(skeleton + 32);
                if (count is < 1 or > 4096 || I(skeleton + 48) != count || I(skeleton + 64) != count)
                    throw new InvalidDataException("Inconsistent native skeleton tables.");
                int parentData = Pointer(skeleton + 24), bones = Pointer(skeleton + 40), pose = Pointer(skeleton + 56);
                Check(parentData, checked(count * 2)); Check(bones, checked(count * 16)); Check(pose, checked(count * 48));
                var parents = new short[count]; var names = new string[count]; var local = new Matrix4x4[count];
                for (int i = 0; i < count; i++)
                {
                    parents[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(parentData + i * 2));
                    if (parents[i] < -1 || parents[i] >= i) throw new InvalidDataException("Invalid native skeleton hierarchy.");
                    names[i] = Text(Pointer(bones + i * 16));
                    var transform = ReadTransform(pose + i * 48);
                    local[i] = Matrix4x4.CreateScale(transform.Scale) * Matrix4x4.CreateFromQuaternion(transform.Rotation)
                        * Matrix4x4.CreateTranslation(transform.Translation);
                }
                int floatCount = I(skeleton + 96), referenceFloatCount = I(skeleton + 80);
                if (floatCount is < 0 or > 65536 || referenceFloatCount is < 0 or > 65536)
                    throw new InvalidDataException("Inconsistent native skeleton float tables.");
                int floatNames = floatCount == 0 ? 0 : Pointer(skeleton + 88), floatValues = referenceFloatCount == 0 ? 0 : Pointer(skeleton + 72);
                Check(floatNames, checked(floatCount * 8)); Check(floatValues, checked(referenceFloatCount * 4));
                skeletons.Add(new HavokAnimationSkeleton(Text(Pointer(skeleton + 16)), names, parents, local)
                { TranslationLocked = Enumerable.Range(0, count).Select(i => bytes[bones + i * 16 + 8] != 0).ToArray(),
                    FloatSlotNames = Enumerable.Range(0, floatCount).Select(i => Text(Pointer(floatNames + 8 * i))).ToArray(),
                    ReferenceFloats = Enumerable.Range(0, referenceFloatCount).Select(i => F(floatValues + 4 * i)).ToArray() });
            }
        if (skeletons is not null) return [];
        var result = new List<HavokAnimationClip>();
        foreach (var (binding, type) in classes)
        {
            if (type != "hkaAnimationBinding") continue;
            Check(binding, 81);
            int animation = Pointer(binding + 24);
            string? animationType = classes.GetValueOrDefault(animation);
            if (animationType is not ("hkaInterleavedUncompressedAnimation" or "hkaSplineCompressedAnimation")) continue;
            byte blendHint = bytes[binding + 80];
            if (blendHint > 2) throw new NotSupportedException("Unsupported native Havok blend hint.");
            Check(animation, 88);
            float duration = F(animation + 20);
            int tracks = I(animation + 24), transformCount = I(animation + 64), mappingCount = I(binding + 40);
            if (duration <= 0 || tracks is < 1 or > 255
                || mappingCount != 0 && mappingCount != tracks) throw new InvalidDataException("Invalid native animation track counts.");
            int floatTracks = I(animation + 28), floatMapCount = I(binding + 56);
            if (floatTracks is < 0 or > 65536 || floatMapCount != 0 && floatMapCount != floatTracks)
                throw new InvalidDataException("Invalid native float track counts.");
            var floatMapping = new short[floatTracks];
            int floatMap = floatMapCount == 0 ? 0 : Pointer(binding + 48);
            if (floatMapCount != 0) Check(floatMap, checked(floatTracks * 2));
            for (int i = 0; i < floatTracks; i++)
                floatMapping[i] = floatMapCount == 0 ? checked((short)i) : BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(floatMap + 2 * i));
            var mapping = new short[tracks];
            int map = mappingCount == 0 ? 0 : Pointer(binding + 32);
            if (mappingCount != 0) Check(map, checked(tracks * 2));
            for (int i = 0; i < tracks; i++)
                mapping[i] = mappingCount == 0 ? checked((short)i) : BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(map + 2 * i));
            if (animationType == "hkaSplineCompressedAnimation")
            {
                var clip = ReadSpline(bytes, animation, Pointer, Text(Pointer(binding + 16)), duration, tracks, mapping);
                clip.BlendHint = blendHint; clip.FloatTrackToSlot = floatMapping; result.Add(clip);
                continue;
            }
            if (transformCount < tracks || transformCount % tracks != 0 || transformCount > 10000000)
                throw new InvalidDataException("Invalid native interleaved sample count.");
            int samples = Pointer(animation + 56); Check(samples, checked(transformCount * 48));
            var transforms = new Transform[transformCount];
            for (int i = 0; i < transforms.Length; i++)
            {
                transforms[i] = ReadTransform(samples + 48 * i);
            }
            int frames = transformCount / tracks, floatSampleCount = I(animation + 80);
            if (floatSampleCount != checked(frames * floatTracks)) throw new InvalidDataException("Inconsistent interleaved float samples.");
            int floatData = floatSampleCount == 0 ? 0 : Pointer(animation + 72);
            Check(floatData, checked(floatSampleCount * 4));
            float[] scalars = Enumerable.Range(0, floatSampleCount).Select(i => F(floatData + 4 * i)).ToArray();
            var interleaved = new HavokAnimationClip(Text(Pointer(binding + 16)), duration, tracks, mapping, transforms)
                { BlendHint = blendHint, FloatTrackToSlot = floatMapping };
            interleaved._floatSampler = seconds =>
            {
                float position = seconds / duration * (frames - 1); int first = Math.Min((int)position, frames - 1), next = Math.Min(first + 1, frames - 1);
                return Enumerable.Range(0, floatTracks).Select(i => scalars[first * floatTracks + i]
                    + (scalars[next * floatTracks + i] - scalars[first * floatTracks + i]) * (position - first)).ToArray();
            };
            result.Add(interleaved);
        }
        return result;
    }
}
