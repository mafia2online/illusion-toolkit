using System.Numerics;

namespace Illusion.Formats.Compiled;

public sealed partial class CompiledEffectCurve
{
    private sealed class BezierKey
    {
        public int Frame;
        public Vector3 Value, InTan, OutTan, InTanLen, OutTanLen;
        public uint Flags;
        public int InMode => (int)(Flags >> 7 & 7);
        public int OutMode => (int)(Flags >> 10 & 7);
    }
    private sealed record BezierRow(float Time, Loop Loop, BezierKey[] Keys);

    private static CompiledEffectCurve ReadBezier(CompiledGraph graph, CompiledElement source)
    {
        int dimensions = source.TypeId is 0xFC25B0BE or 0xE9A9F953 ? 1 : 3;
        if (graph.Field(source, "m_KeysDimension") is not null && Integer(graph, source, "m_KeysDimension") != dimensions)
            throw new InvalidDataException("Native Bezier dimensions do not match the interpolator type.");
        if (source.TypeId is 0xFC25B0BE or 0x069E546F)
        {
            var keys = BezierKeys(graph, source, dimensions);
            var loop = Loop.Read(graph, source);
            return new(dimensions, source.ObjectId, false, (time, _, looping) => EvaluateBezier(keys, looping ? loop.Apply(time) : time, dimensions));
        }
        var rows = graph.Items(graph.Field(source, "m_Keys"));
        int count = Integer(graph, source, "m_NumKeys");
        if (count < 1 || count > rows.Count) throw new InvalidDataException("Invalid native Bezier row count.");
        var compiled = rows.Take(count).Select(row => new BezierRow(Float(graph, row, "m_EmitTime"), Loop.Read(graph, row), BezierKeys(graph, row, dimensions))).ToArray();
        for (int i = 1; i < compiled.Length; i++)
            if (compiled[i].Time < compiled[i - 1].Time) throw new InvalidDataException("Unordered native Bezier emission rows.");
        return new(dimensions, source.ObjectId, false, (time, age, looping) =>
        {
            int index = 0;
            while (index + 1 < compiled.Length && time >= compiled[index + 1].Time) index++;
            var row = compiled[index];
            return EvaluateBezier(row.Keys, looping ? row.Loop.Apply(age) : age, dimensions);
        });
    }

    private static BezierKey[] BezierKeys(CompiledGraph graph, CompiledElement source, int dimensions)
    {
        if (graph.Field(source, "m_KeysDimension") is not null && Integer(graph, source, "m_KeysDimension") != dimensions)
            throw new InvalidDataException("Native Bezier key dimensions do not match the interpolator type.");
        var rows = graph.Items(graph.Field(source, "m_Keys"));
        if (rows.Count == 0) throw new InvalidDataException("Empty native Bezier curve.");
        Vector3 Read(CompiledElement row, string name)
        {
            var field = graph.Field(row, name) ?? throw new InvalidDataException("Missing native Bezier " + name);
            return dimensions == 1 ? new(Float(graph, field, "x"), 0, 0) : Vector(graph.Data(field), 0, 3);
        }
        var keys = rows.Select(row => new BezierKey
        {
            Frame = Integer(graph, row, "m_Frame"), Value = Read(row, "m_Value"),
            InTan = Read(row, "m_InTan"), OutTan = Read(row, "m_OutTan"),
            InTanLen = Read(row, "m_InTanLen"), OutTanLen = Read(row, "m_OutTanLen"),
            Flags = unchecked((uint)Integer(graph, row, "m_Flags"))
        }).ToArray();
        for (int i = 1; i < keys.Length; i++)
            if (keys[i].Frame < keys[i - 1].Frame) throw new InvalidDataException("Unordered native Bezier keys.");
        if (keys.Any(k => k.InMode == 7 || k.OutMode == 7))
            throw new NotSupportedException("Reserved native Bezier tangent mode7 is not supported.");
        PrepareBezier(keys);
        return keys;
    }

    private static void PrepareBezier(BezierKey[] keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            int previous = (i + keys.Length - 1) % keys.Length, next = (i + 1) % keys.Length;
            int before = key.Frame - keys[previous].Frame;
            if (previous >= i) before += keys[^1].Frame - keys[0].Frame;
            int after = keys[next].Frame - key.Frame;
            if (next <= i) after += keys[^1].Frame - keys[0].Frame;
            if (before == 0) before = 1;
            if (after == 0) after = 1;
            switch (key.InMode)
            {
                case 1:
                    if (i == 0 || keys[previous].OutMode == 1) key.InTan = (keys[previous].Value - key.Value) / before;
                    else BesselEnd(keys, i);
                    break;
                case 2 or 4: key.InTan = Vector3.Zero; break;
                case 3: key.InTan = (keys[previous].Value - key.Value) * 2 / before; break;
            }
            switch (key.OutMode)
            {
                case 1:
                    if (i == keys.Length - 1 || keys[next].InMode == 1) key.OutTan = (keys[next].Value - key.Value) / after;
                    else BesselStart(keys, i);
                    break;
                case 2 or 4: key.OutTan = Vector3.Zero; break;
                case 3: key.OutTan = (keys[next].Value - key.Value) * 2 / after; break;
            }
            if (key.InMode == 0 || key.OutMode == 0) AdjustBessel(keys, i);
            else if (key.InMode == 6 || key.OutMode == 6) AdjustFlat(keys, i);
        }
    }

    private static Vector3 EvaluateBezier(BezierKey[] keys, float time, int dimensions)
    {
        float frame = time * 30;
        if (frame <= keys[0].Frame) return keys[0].Value;
        if (frame >= keys[^1].Frame) return keys[^1].Value;
        int index = 0;
        while (index + 1 < keys.Length && frame >= keys[index + 1].Frame) index++;
        var left = keys[index]; var right = keys[index + 1];
        float fraction = (frame - left.Frame) / (right.Frame - left.Frame);
        if (left.OutMode == 2 || right.InMode == 2) return fraction == 1 ? right.Value : left.Value;
        if (left.OutMode is not (5 or 6) && right.InMode is not (5 or 6))
        {
            float inverse = 1 - fraction, square = fraction * fraction, cube = square * fraction;
            float step = (right.Frame - left.Frame) / 3f;
            var a = left.Value + step * left.OutTan;
            var b = right.Value + step * right.InTan;
            return ((left.Value * inverse + a * (fraction * 3)) * inverse + b * (square * 3)) * inverse + right.Value * cube;
        }
        var result = Vector3.Zero;
        for (int component = 0; component < dimensions; component++)
            result[component] = WeightedBezier(left, right, fraction, component);
        return result;
    }

    private static float WeightedBezier(BezierKey left, BezierKey right, float fraction, int c)
    {
        float span = right.Frame - left.Frame;
        static (float X, float Y) Handle(BezierKey key, BezierKey other, bool incoming, float span, int c)
        {
            int mode = incoming ? key.InMode : key.OutMode;
            float tangent = incoming ? key.InTan[c] : key.OutTan[c];
            float angle = MathF.Atan(tangent), cosine = MathF.Cos(angle);
            float length = incoming ? key.InTanLen[c] : key.OutTanLen[c];
            bool absolute = (key.Flags & 0x100000) != 0;
            if (!absolute) length = (float)((double)length * span / Math.Cos(angle));
            float x;
            if (mode is 5 or 6)
            {
                if (mode == 6 || length < 0)
                {
                    length = (span / 3) / cosine;
                    // Native Interpolate caches automatic handles on its private key buffer.
                    // Fractional handles use the original rounded .3333 constant thereafter.
                    float cached = absolute ? length : BitConverter.UInt32BitsToSingle(0x3EAAA64C);
                    if (incoming) key.InTanLen[c] = cached; else key.OutTanLen[c] = cached;
                }
                x = cosine * length;
            }
            else if (mode == 1)
            {
                float dy = incoming ? key.Value[c] - other.Value[c] : other.Value[c] - key.Value[c];
                angle = incoming ? -MathF.Atan(dy / span) : MathF.Atan(dy / span);
                length = MathF.Sqrt(span * span + dy * dy) / 3;
                x = span / 3;
            }
            else { x = span / 3; length = x / cosine; }
            return (x, MathF.Sin(angle) * length);
        }
        var a = Handle(left, right, false, span, c);
        var b = Handle(right, left, true, span, c);
        float x1 = left.Frame + a.X, x2 = right.Frame - b.X;
        float y1 = left.Value[c] + a.Y, y2 = right.Value[c] + b.Y;
        double x0 = left.Frame, x3 = right.Frame;
        double cubic = x3 - x0 - 3d * x2 + 3d * x1;
        double quadratic = 3d * (x0 - 2d * x1 + x2), linear = 3d * (x1 - x0);
        double target = x0 + (x3 - x0) * fraction;
        float t = quadratic == 0 && cubic == 0 ? fraction : target == x0 ? 0 : target == x3 ? 1 : BezierRoot(x0 - target, linear, quadratic, cubic);
        float t2 = t * t, t3 = t * t2;
        double y0 = left.Value[c], y3 = right.Value[c];
        return (float)(((y3 - y0 - 3d * y2 + 3d * y1) * t3 + 3d * (y0 - 2d * y1 + y2) * t2) + 3d * (y1 - y0) * t + y0);
    }

    private static float BezierRoot(double constant, double linear, double quadratic, double cubic)
    {
        // Native SolveCubic uses the depressed cubic, a 1e-9 discriminant window,
        // then selects the greatest real root in [0,1].
        double a = quadratic / cubic, b = linear / cubic, c = constant / cubic;
        double p = (b - a * a * (1d / 3)) * (1d / 3);
        double q = (c + ((a * (2d / 27)) * (a * a) - (a * (1d / 3)) * b)) * .5;
        double d = q * q + p * p * p;
        Span<double> roots = stackalloc double[3];
        int count;
        static double CubeRoot(double value) => value < 0 ? -Math.Pow(-value, 1d / 3) : Math.Pow(value, 1d / 3);
        if (d > -1e-9 && d < 1e-9)
        {
            if (q > -1e-9 && q < 1e-9) { roots[0] = 0; count = 1; }
            else { double u = CubeRoot(-q); roots[0] = 2 * u; roots[1] = -u; count = 2; }
        }
        else if (d < 0)
        {
            double angle = Math.Acos(-q / Math.Sqrt(-(p * p * p))) / 3;
            double scale = 2 * Math.Sqrt(-p);
            roots[0] = scale * Math.Cos(angle);
            roots[1] = -scale * Math.Cos(angle + 1.047197551);
            roots[2] = -scale * Math.Cos(angle - 1.047197551);
            count = 3;
        }
        else { double s = Math.Sqrt(d); roots[0] = CubeRoot(s - q) - CubeRoot(s + q); count = 1; }
        for (int i = 0; i < count; i++) roots[i] -= a * (1d / 3);
        if (count == 1) return (float)roots[0];
        float result = -1;
        for (int i = 0; i < count; i++) if (roots[i] >= 0 && roots[i] <= 1 && roots[i] > result) result = (float)roots[i];
        return result;
    }
}
