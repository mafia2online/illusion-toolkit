namespace Illusion.Rendering.Shaders;

/// <summary>Native PS1078 authored-flow fine normals at explicit deep-water depth fade.
/// Authored variance mip G/A channels contain normal XY; no reflection or foam is synthesized.</summary>
public static class NativeWaterFineNormalShader
{
    public const string Functions = """
float3 EvaluateNativeWaterFineNormal(Texture2D<float4> noise, SamplerState wrapped,
    float2 world, float time, float3 baseNormal, float scale, float intensity, float2 flow)
{
    float phase = frac(time * .125), halfPhase = frac(time * .125 + .5);
    float weight = 2 * abs(phase - .5);
    float2 minus = (world - time * .375) * scale;
    float2 plus = (world.yx + time * .375) * scale;
    float4 a0 = noise.Sample(wrapped, (minus + flow * phase) * float2(1, -1));
    float4 a1 = noise.Sample(wrapped, (minus + flow * halfPhase) * float2(1, -1));
    float4 b0 = noise.Sample(wrapped, plus + flow.yx * phase);
    float4 b1 = noise.Sample(wrapped, plus + flow.yx * halfPhase);
    float4 a = lerp(a0, a1, weight), b = lerp(b0, b1, weight);
    float2 slope = float2((a.g * 2 - 1) - (b.a * 2 - 1),
                         (a.a * 2 - 1) + (b.g * 2 - 1)) * ((length(flow) + 1) * intensity) * .5;
    float3 tangent = float3(baseNormal.z, 0, -baseNormal.x);
    float3 binormal = float3(0, baseNormal.z, -baseNormal.y);
    float t2 = dot(tangent, tangent), b2 = dot(binormal, binormal);
    if (t2 < 1e-10 || b2 < 1e-10) return baseNormal;
    return normalize(baseNormal + slope.x * tangent * rsqrt(t2)
        + slope.y * binormal * rsqrt(b2));
}
float3 EvaluateNativeWaterFineNormal(Texture2D<float4> noise, SamplerState wrapped,
    float2 world, float time, float3 baseNormal, float scale, float intensity)
{
    return EvaluateNativeWaterFineNormal(noise, wrapped, world, time, baseNormal, scale, intensity, 0);
}
""";
}
