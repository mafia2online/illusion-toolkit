namespace Illusion.Rendering.Passes;

/// <summary>Native S_WaterStamp::Write byte accumulation, without actor-shape generation.</summary>
public static class NativeWaterStampQuantizer
{
    public const int Size = 512;
    public const float DomainSize = 96;
    public static byte Accumulate(byte current, float influence)
    {
        if (!float.IsFinite(influence)) throw new ArgumentOutOfRangeException(nameof(influence));
        float value = current + influence * 255;
        value = Math.Max(value, 0);
        return value >= 255 ? (byte)255 : (byte)(int)value;
    }
    public static byte[] BuildSparse(IReadOnlyList<int> texels, IReadOnlyList<float> influences)
    {
        if (texels.Count != influences.Count || texels.Count > Size * Size) throw new ArgumentException("One influence per sparse native stamp texel is required.");
        var seen = new HashSet<int>();
        for (int i = 0; i < texels.Count; i++)
            if (texels[i] is < 0 or >= Size * Size || !seen.Add(texels[i]) || !float.IsFinite(influences[i]))
                throw new ArgumentException("Native stamp indices must be unique within 512², and influences finite.");
        var data = new byte[Size * Size];
        for (int i = 0; i < texels.Count; i++) data[texels[i]] = Accumulate(0, influences[i]);
        return data;
    }
}
