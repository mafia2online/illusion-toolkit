using System.Numerics;

namespace Illusion.Formats.Compiled;

public sealed partial class CompiledEffectPointPreview
{
    // Original UpdateAnimTmp1017833E0 builds row-major UV origins by repeated float
    // addition, and Texture::Init1019D5450 selects the sole cycle's native start/count.
    private sealed record PointAtlas(Vector4[] Regions,int Frames,int Start,bool Animated,bool Blended,bool RandomStart,
        float Direction,bool RespectPreload,int EndCondition,int[] Sequence,float Speed)
    {
        public int Mirror { get; init; }
        public Vector4 Region(int frame,int flags)
        {
            var uv = Regions[frame];
            // Native FillVerticesFallback102776B90 swaps endpoint X for mirror bit0,
            // and endpoint Y for bit1, on each of the two sampled atlas frames.
            if ((flags & 16) != 0) (uv.X,uv.Z) = (uv.Z,uv.X);
            if ((flags & 32) != 0) (uv.Y,uv.W) = (uv.W,uv.Y);
            return uv;
        }
        public (int First,int Second,byte Blend) Sample(int birth,int flags,float age,float life,float normalizedAge,float preload)
        {
            if (!Animated) return (birth,birth,0);
            float phase = RespectPreload ? Speed * ((age - preload) / (life - preload)) : Speed * normalizedAge;
            if (RepeatByTime) phase = (age - (RespectPreload ? preload : 0)) * Speed;
            if (!float.IsFinite(phase) || phase >= int.MaxValue - 8192) throw new NotSupportedException("Native atlas phase exceeds its finite signed-index range.");
            phase = MathF.Max(phase,0); int frame = (int)phase, first, second;
            if (EndCondition == 1)
            {
                int index = Math.Min(frame,Sequence.Length - 3);
                first = Sequence[index];second = Sequence[index + 1];
                if ((flags & 8) != 0) { first = Frames + birth - first;second = Frames + birth - second; }
                else { first += birth;second += birth; }
            }
            else if ((flags & 8) != 0)
            {
                int count = Sequence.Length - 2;
                int index = count - (count + frame - 1 - birth) % count - 1;
                first = Sequence[index];second = Sequence[(count + index - 1) % count];
            }
            else
            {
                int index = (frame + birth) % (Sequence.Length - 2);
                first = Sequence[index];second = Sequence[index + 1];
            }
            byte a = unchecked((byte)first), b = Blended ? unchecked((byte)second) : (byte)birth;
            if (a >= Regions.Length || b >= Regions.Length) throw new NotSupportedException("Native atlas frame lies beyond its duplicated UV cache.");
            return (a,b,Blended ? (byte)((phase - MathF.Truncate(phase)) * 255) : (byte)0);
        }
        public bool RepeatByTime { get; init; }
    }
    private static PointAtlas ReadFixedAtlas(CompiledGraph graph,CompiledElement texture)
    {
        int rows = Integer(graph,texture,"m_Rows"), cols = Integer(graph,texture,"m_Cols");
        if (rows is < 1 or > 256 || cols is < 1 or > 256 || rows * cols > 256)
            throw new NotSupportedException("Native atlas preview requires1–256 cells with positive rows/columns.");
        var cycles = graph.Items(graph.Field(texture,"m_Cycles"));
        if (Integer(graph,texture,"m_CyclesCount") != 1 || cycles.Count != 1)
            throw new NotSupportedException("Native atlas preview requires one explicit cycle.");
        int frames = Integer(graph,cycles[0],"m_NumFrames"), start = Integer(graph,cycles[0],"m_StartFrame") - 1;
        if (frames < 1 || frames > rows * cols || start < 0 || start >= rows * cols)
            throw new NotSupportedException("Static native atlas frame count/start must fit its authored cells.");
        bool animated = Bool(graph,texture,"m_Animated");int[] sequence = [];float speed = 0;int endCondition = 0,repeat = 0;
        if (animated)
        {
            int firstFrame = Integer(graph,cycles[0],"m_FirstFrame");repeat = Integer(graph,cycles[0],"m_RepeatPerCycle");endCondition = Integer(graph,cycles[0],"m_EndCondition");
            if (firstFrame < 1 || firstFrame > rows * cols - frames + 1 || start >= frames || repeat < 0 || endCondition is < 0 or > 2 || endCondition == 2 && frames < 2)
                throw new NotSupportedException("Animated atlas preview requires its frame interval within the grid, a start within its cycle, nonnegative repeat count and verified loop/clamp/ping-pong mode.");
            int length = endCondition == 2 ? 2 * frames - 2 : frames;
            if ((long)length * Math.Max(repeat,1) > 4096) throw new NotSupportedException("Native atlas preview limits its expanded sequence to4096 frames.");
            var values = Enumerable.Range(firstFrame - 1,frames).ToList();
            if (endCondition == 2) for (int i = frames - 2;i >= 1;i--) values.Add(firstFrame - 1 + i);
            var expanded = new List<int>();
            for (int cycle = 0;cycle < Math.Max(repeat,1);cycle++)
                expanded.AddRange(endCondition == 1 && cycle > 0 ? Enumerable.Repeat(firstFrame + frames - 2,length) : values);
            speed = expanded.Count;
            if (repeat == 0)
            {
                float playTime = Float(graph,cycles[0],"m_PlayTime");
                if (playTime <= 0) throw new NotSupportedException("Native atlas cycle play time must be positive.");
                speed /= playTime;
                if (!float.IsFinite(speed)) throw new NotSupportedException("Native atlas speed must be finite.");
            }
            expanded.Add(endCondition == 1 ? expanded[^1] : expanded[0]);
            expanded.Add(endCondition == 1 ? expanded[^1] : expanded[1]);sequence = expanded.ToArray();
        }
        float u1 = Float(graph,texture,"m_RegionU1"),v1 = Float(graph,texture,"m_RegionV1");
        float du = (Float(graph,texture,"m_RegionU2") - u1) / cols,dv = (Float(graph,texture,"m_RegionV2") - v1) / rows;
        if (!float.IsFinite(du) || !float.IsFinite(dv)) throw new NotSupportedException("Native atlas UV steps must be finite.");
        var regions = new Vector4[rows * cols]; float v = v1;
        for (int row = 0; row < rows; row++)
        {
            float u = u1;
            for (int col = 0; col < cols; col++) { regions[row * cols + col] = new(u,v,u + du,v + dv); u += du; }
            v += dv;
        }
        if (regions.Any(r => !float.IsFinite(r.X) || !float.IsFinite(r.Y) || !float.IsFinite(r.Z) || !float.IsFinite(r.W)))
            throw new NotSupportedException("Native atlas UV accumulation exceeded its finite preview range.");
        return new([..regions,..regions],frames,start,animated,Bool(graph,texture,"m_BlendedTexture"),Bool(graph,texture,"m_RandomStartFrame"),
            Float(graph,texture,"m_AnimPlayDir"),Bool(graph,texture,"m_RespectPreload"),endCondition,sequence,speed)
            { RepeatByTime = repeat == 0,Mirror = Integer(graph,texture,"m_RandomMirror") };
    }
}
