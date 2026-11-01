namespace Illusion.Rendering.Shaders;

/// <summary>Native PS1078 fine normals at explicit zero query-flow and deep-water depth fade.
/// Authored variance mip G/A channels contain normal XY; no reflection or foam is synthesized.</summary>
public static class NativeWaterFineNormalShader
{
    public const string Functions = """
float3 EvaluateNativeWaterFineNormal(Texture2D<float4> noise, SamplerState wrapped,
    float2 world, float time, float3 baseNormal, float scale, float intensity)
{
    // At zero flow both phase samples coincide. Keep the native time-dependent UV pair.
    float4 a = noise.Sample(wrapped, (world - time * .375) * scale * float2(1, -1));
    float4 b = noise.Sample(wrapped, (world.yx + time * .375) * scale);
    float2 slope = float2((a.g * 2 - 1) - (b.a * 2 - 1),
                         (a.a * 2 - 1) + (b.g * 2 - 1)) * intensity * .5;
    float3 tangent = float3(baseNormal.z, 0, -baseNormal.x);
    float3 binormal = float3(0, baseNormal.z, -baseNormal.y);
    float t2 = dot(tangent, tangent), b2 = dot(binormal, binormal);
    if (t2 < 1e-10 || b2 < 1e-10) return baseNormal;
    return normalize(baseNormal + slope.x * tangent * rsqrt(t2)
        + slope.y * binormal * rsqrt(b2));
}
""";
}
