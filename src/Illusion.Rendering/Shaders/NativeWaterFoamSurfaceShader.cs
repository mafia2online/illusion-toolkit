namespace Illusion.Rendering.Shaders;

/// <summary>Verified PS1078 pre-lighting foam inputs. DiffuseDiagnostic is the native foam
/// contribution before depth/lighting, not a replacement for unavailable t221/t240 radiance.
/// Caller supplies history mips/sampler, authored T000/T001, depth factors and frame origin.</summary>
public static class NativeWaterFoamSurfaceShader
{
    public const string Functions = """
struct NativeWaterFoamMaterial
{
    float Weight;
    float3 DiffuseDiagnostic;
    float3 Normal;
    float Roughness;
};
float2 NativeWaterFoamHistoryUV(float2 worldMinusHalfDisplacement, float2 frameOffset, float2 currentOrigin)
{
    float2 offset = (currentOrigin - (worldMinusHalfDisplacement + frameOffset)) / 256;
    offset *= rsqrt(max(length(offset), .00001));
    return offset * float2(.5, -.5) + .5;
}
float2 NativeWaterFoamSampleUV(float2 uv, float2 nativeSampleOffset)
{
    return uv + ddx_coarse(uv) * nativeSampleOffset.x - ddy_coarse(uv) * nativeSampleOffset.y;
}
NativeWaterFoamMaterial EvaluateNativeWaterFoamMaterial(
    Texture2D<float4> varianceTexture, Texture2D<float4> foamDetail,
    Texture2D<float4> foamNormal, Texture2D<float4> history,
    SamplerState wrapped, SamplerState historySampler,
    float2 worldMinusHalfDisplacement, float3 coarseNormal, float2 flow,
    float time, float2 frameOffset, float2 currentOrigin, float2 nativeSampleOffset,
    float4 D265, float4 D267, float depthFade, float depthSlope, float gbufferRoughness)
{
    NativeWaterFoamMaterial result;
    float phase = frac(time * .125), halfPhase = frac(time * .125 + .5);
    float blend = abs(phase - .5) * 2;
    float2 uv = NativeWaterFoamHistoryUV(worldMinusHalfDisplacement, frameOffset, currentOrigin);
    result.Weight = history.Sample(historySampler, NativeWaterFoamSampleUV(uv, nativeSampleOffset)).r;
    float2 foamUV0 = worldMinusHalfDisplacement * D267.x + flow * phase;
    float2 foamUV1 = worldMinusHalfDisplacement * D267.x + flow * halfPhase;
    foamUV0 = NativeWaterFoamSampleUV(foamUV0, nativeSampleOffset);
    foamUV1 = NativeWaterFoamSampleUV(foamUV1, nativeSampleOffset);
    float detail = lerp(foamDetail.Sample(wrapped, foamUV0).r, foamDetail.Sample(wrapped, foamUV1).r, blend);
    result.DiffuseDiagnostic = saturate(result.Weight * (result.Weight + detail - .5) * D265.rgb);
    float3 authoredNormal = lerp(foamNormal.Sample(wrapped, foamUV0).xyz,
        foamNormal.Sample(wrapped, foamUV1).xyz, blend) * 2 - 1;
    float2 minus = (worldMinusHalfDisplacement - time * .375) * D267.y;
    float2 plus = (worldMinusHalfDisplacement.yx + time * .375) * D267.y;
    float4 a0 = varianceTexture.Sample(wrapped, NativeWaterFoamSampleUV((minus + flow * phase) * float2(1, -1), nativeSampleOffset));
    float4 a1 = varianceTexture.Sample(wrapped, NativeWaterFoamSampleUV((minus + flow * halfPhase) * float2(1, -1), nativeSampleOffset));
    float4 b0 = varianceTexture.Sample(wrapped, NativeWaterFoamSampleUV(plus + flow.yx * phase, nativeSampleOffset));
    float4 b1 = varianceTexture.Sample(wrapped, NativeWaterFoamSampleUV(plus + flow.yx * halfPhase, nativeSampleOffset));
    float4 a = lerp(a0, a1, blend), b = lerp(b0, b1, blend);
    float strength = (length(flow) + 1) * D267.w;
    float2 slope = float2((a.g * 2 - 1) - (b.a * 2 - 1), (a.a * 2 - 1) + (b.g * 2 - 1)) * strength;
    slope *= depthFade * .5 * depthSlope;
    slope = lerp(slope, authoredNormal.xy / max(authoredNormal.z, .00001), result.Weight);
    coarseNormal = normalize(coarseNormal);
    float3 tangent = float3(coarseNormal.z, 0, -coarseNormal.x);
    float3 binormal = float3(0, coarseNormal.z, -coarseNormal.y);
    float t2 = dot(tangent, tangent), b2 = dot(binormal, binormal);
    result.Normal = t2 < 1e-10 || b2 < 1e-10 ? coarseNormal
        : normalize(coarseNormal + slope.x * tangent * rsqrt(t2) + slope.y * binormal * rsqrt(b2));
    float varianceR = (a.r + b.r) * strength * .5;
    float varianceB = (a.b + b.b) * strength * .5;
    float roughness = 1 - min(sqrt((varianceR + varianceB) * .5), 1);
    result.Roughness = lerp(gbufferRoughness, lerp(roughness, D265.w, result.Weight), depthFade);
    return result;
}
""";
}
