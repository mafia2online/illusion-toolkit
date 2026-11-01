using System.Numerics;

namespace Illusion.Formats.Compiled;

public sealed partial class CompiledEffectPointPreview
{
    // Original UpdateAnimTmp1017833E0 builds row-major UV origins by repeated float
    // addition, and Texture::Init1019D5450 selects the sole cycle's native start/count.
    private static (Vector4[] Regions,int Frames,int Start) ReadFixedAtlas(CompiledGraph graph,CompiledElement texture)
    {
        int rows = Integer(graph,texture,"m_Rows"), cols = Integer(graph,texture,"m_Cols");
        if (rows is < 1 or > 256 || cols is < 1 or > 256 || rows * cols > 256)
            throw new NotSupportedException("Static native atlas preview requires1–256 cells with positive rows/columns.");
        var cycles = graph.Items(graph.Field(texture,"m_Cycles"));
        if (Integer(graph,texture,"m_CyclesCount") != 1 || cycles.Count != 1)
            throw new NotSupportedException("Static native atlas preview requires one explicit cycle.");
        int frames = Integer(graph,cycles[0],"m_NumFrames"), start = Integer(graph,cycles[0],"m_StartFrame") - 1;
        if (frames < 1 || frames > rows * cols || start < 0 || start >= rows * cols)
            throw new NotSupportedException("Static native atlas frame count/start must fit its authored cells.");
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
        return (regions,frames,start);
    }
}
