using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Native compiled particle constants, linear emission/age curves and prepared Bezier splines.</summary>
public sealed partial class CompiledEffectCurve
{
    private readonly Func<float, float, bool, Vector3> _sample;
    public int Dimensions { get; }
    public ulong SourceId { get; }
    public bool IsConstant { get; }

    private CompiledEffectCurve(int dimensions, ulong sourceId, bool constant, Func<float, float, bool, Vector3> sample)
    { Dimensions = dimensions; SourceId = sourceId; IsConstant = constant; _sample = sample; }

    public Vector3 Sample(float emissionTime, float normalizedAge = 0, bool looping = true)
    {
        if (!float.IsFinite(emissionTime) || !float.IsFinite(normalizedAge)) throw new ArgumentException("Particle curve time and age must be finite.");
        var result = _sample(emissionTime, normalizedAge, looping);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z)) throw new InvalidDataException("Particle curve produced a non-finite value.");
        return result;
    }

    public static CompiledEffectCurve FromParameter(CompiledGraph graph, CompiledElement owner, string field)
    {
        var source = graph.Resolve(graph.Field(graph.Field(owner, field), "m_Interpolator"))
            ?? throw new InvalidDataException("The particle parameter has no native interpolator.");
        return Read(graph, source);
    }

    public static CompiledEffectCurve Read(CompiledGraph graph, CompiledElement source)
    {
        if (source.TypeId is 0xFC25B0BE or 0x069E546F or 0xE9A9F953 or 0x13121D82) return ReadBezier(graph, source);
        int dimensions = source.TypeId switch
        {
            0xA431F78B or 0xCFA090E0 or 0xDAE4921D or 0x91AAAB58 => 1,
            0x5E8A135A or 0x351B7431 or 0x205F76CC or 0x6B114F89 => 3,
            _ => throw new NotSupportedException($"Particle interpolator {source.TypeId:X8} has not been verified for evaluation.")
        };
        if (graph.Field(source, "m_ConstValue") is { } constant)
        {
            var value = Vector(graph.Data(constant), 0, dimensions);
            return new(dimensions, source.ObjectId, true, (_, _, _) => value);
        }
        if (source.TypeId is 0xDAE4921D or 0x205F76CC)
        {
            var keys = Packed(graph, source, dimensions, false);
            int count = Integer(graph, source, "m_NumKeys");
            if (count < 0 || keys.Length != count + 2) throw new InvalidDataException("Invalid native linear particle key count.");
            var loop = Loop.Read(graph, source);
            return new(dimensions, source.ObjectId, false, (time, _, looping) =>
            {
                time = looping ? loop.Apply(time) : time;
                int index = 0;
                while (index < count && time >= keys[index + 1].Time) index++;
                var key = keys[index];
                return key.Value + key.AgeSlope * (time - key.Time);
            });
        }
        var rows = graph.Items(graph.Field(source, "m_Keys"));
        int rowCount = Integer(graph, source, "m_NumKeys");
        if (rowCount < 1 || rowCount > rows.Count) throw new InvalidDataException("Invalid native two-dimensional particle row count.");
        var compiled = rows.Take(rowCount).Select(row => new Row(Float(graph, row, "m_EmitTime"), Float(graph, row, "m_MinAgeTime"),
            Float(graph, row, "m_MaxAgeTime"), Loop.Read(graph, row), Packed(graph, row, dimensions, true))).ToArray();
        for (int i = 0; i < compiled.Length; i++)
        {
            if (compiled[i].Keys.Length != Integer(graph, rows[i], "m_NumKeys") || compiled[i].Keys.Length == 0
                || compiled[i].MinAge > compiled[i].MaxAge || i > 0 && compiled[i].Time < compiled[i - 1].Time)
                throw new InvalidDataException("Invalid native two-dimensional particle keys.");
        }
        float minTime = Float(graph, source, "m_MinEmitTime");
        return new(dimensions, source.ObjectId, false, (time, age, looping) =>
        {
            time = MathF.Max(minTime, time);
            int index = 0;
            while (index + 1 < compiled.Length && time >= compiled[index + 1].Time) index++;
            var row = compiled[index];
            age = Math.Clamp(looping ? row.Loop.Apply(age) : age, row.MinAge, row.MaxAge);
            int keyIndex = 0;
            while (keyIndex + 1 < row.Keys.Length && age >= row.Keys[keyIndex + 1].Time) keyIndex++;
            var key = row.Keys[keyIndex];
            float dt = time - row.Time;
            // Preserve the native multiply/add order (the shipped AVX code does not fuse these).
            var value = key.Value + key.EmissionSlope * dt;
            var slope = key.AgeSlope + key.MixedSlope * dt;
            return value + slope * (age - key.Time);
        });
    }

    private readonly record struct Key(float Time, Vector3 Value, Vector3 EmissionSlope, Vector3 AgeSlope, Vector3 MixedSlope);
    private sealed record Row(float Time, float MinAge, float MaxAge, Loop Loop, Key[] Keys);
    private readonly record struct Loop(float Start, float Duration, int Count)
    {
        public static Loop Read(CompiledGraph graph, CompiledElement owner) => graph.Field(owner, "m_TimeLoopTransform") is { } node
            ? new(Float(graph, node, "m_LoopTimeStart"), Float(graph, node, "m_LoopTimeDuration"), Integer(graph, node, "m_LoopCount")) : default;
        public float Apply(float time)
        {
            if (Count == 0 || time < Start) return time;
            if (!(Duration > 0)) throw new InvalidDataException("A looping particle curve requires a positive duration.");
            if (Count < 0 || time < Start + (float)unchecked(Count + 1) * Duration) return (time - Start) % Duration + Start;
            return time - Count * Duration;
        }
    }
    private static Key[] Packed(CompiledGraph graph, CompiledElement source, int dimensions, bool twoDimensional)
    {
        var field = graph.Field(source, "m_Keys") ?? throw new InvalidDataException("Missing particle keys.");
        var bytes = graph.Data(field);
        int stride = 4 + dimensions * (twoDimensional ? 16 : 8);
        if (bytes.Length < 4) throw new InvalidDataException("Truncated particle keys.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (count < 1 || bytes.Length != 4L + count * (long)stride) throw new InvalidDataException("Invalid packed particle keys.");
        int active = checked(Integer(graph, source, "m_NumKeys") + (twoDimensional ? 0 : 2));
        if (active < 1 || active > count) throw new InvalidDataException("Particle key count exceeds native storage.");
        var keys = new Key[active];
        for (int i = 0; i < active; i++)
        {
            int at = 4 + i * stride;
            float time = Scalar(bytes, at);
            // The one-dimensional buffer ends with a time-only search sentinel; its other
            // channels are unconsumed native storage, and can contain non-finite padding.
            if (!twoDimensional && i == active - 1) { keys[i] = new(time, default, default, default, default); continue; }
            // Coincident native knots encode instantaneous jumps. The earlier zero-width
            // segment is never selected, even when its precomputed slope divides by zero.
            if (i + 1 < active && time == Scalar(bytes, at + stride))
            { keys[i] = new(time, default, default, default, default); continue; }
            var value = Vector(bytes, at + 4, dimensions);
            var first = Vector(bytes, at + 4 + dimensions * 4, dimensions);
            keys[i] = twoDimensional
                ? new(time, value, first, Vector(bytes, at + 4 + dimensions * 8, dimensions), Vector(bytes, at + 4 + dimensions * 12, dimensions))
                : new(time, value, Vector3.Zero, first, Vector3.Zero);
            if (i > 0 && time < keys[i - 1].Time) throw new InvalidDataException("Particle keys are not ordered.");
        }
        return keys;
    }
    private static float Float(CompiledGraph graph, CompiledElement node, string name) =>
        Scalar(graph.Data(graph.Field(node, name) ?? throw new InvalidDataException("Missing particle field " + name)), 0);
    private static int Integer(CompiledGraph graph, CompiledElement node, string name) =>
        BinaryPrimitives.ReadInt32LittleEndian(graph.Data(graph.Field(node, name) ?? throw new InvalidDataException("Missing particle field " + name)));
    private static float Scalar(ReadOnlySpan<byte> bytes, int at)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]);
        if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite particle curve data.");
        return value;
    }
    private static Vector3 Vector(ReadOnlySpan<byte> bytes, int at, int dimensions) => dimensions == 1
        ? new(Scalar(bytes, at), 0, 0) : new(Scalar(bytes, at), Scalar(bytes, at + 4), Scalar(bytes, at + 8));
}
