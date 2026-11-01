namespace Illusion.Rendering.Shaders;

/// <summary>Verified analytical component of native PS7139. Caller supplies the user-owned
/// sincos.dds as linear R8G8 UNORM, native S023 anisotropic16/wrap sampler, world XY plus
/// floating-origin XY, seconds, and unnormalized native T032 RGBA template weights.
/// This pixel-shader function uses native implicit derivatives and all supplied lookup mips.
/// The base field excludes dynamic ripple; ApplyNativeWaterContact adds an explicit native state.
/// Shoreline depth and reflection passes remain separate.</summary>
public static class AnalyticalWaterShader
{
    public const string Functions = """
struct AnalyticalWaterField { float3 Displacement; float3 Normal; float2 Gradient; };
AnalyticalWaterField EvaluateAnalyticalWater(Texture2D<float2> lookup, SamplerState wrapped,
    float4 packed[32], float2 world, float time, float4 weights)
{
    float3 displacement = 0;
    float2 gradient = 0;
    [unroll] for (int templateIndex = 0; templateIndex < 4; templateIndex++)
    {
        int b = templateIndex * 8;
        float4 phase = world.y * packed[b + 1];
        phase = packed[b] * world.x + phase;
        phase = packed[b + 2] * time + phase;
        float2 q0 = lookup.Sample(wrapped, float2(phase.x, 0)) * 2 - 1;
        float2 q1 = lookup.Sample(wrapped, float2(phase.y, 0)) * 2 - 1;
        float2 q2 = lookup.Sample(wrapped, float2(phase.z, 0)) * 2 - 1;
        float2 q3 = lookup.Sample(wrapped, float2(phase.w, 0)) * 2 - 1;
        float4 sine = float4(q0.x, q1.x, q2.x, q3.x);
        float4 cosine = float4(q0.y, q1.y, q2.y, q3.y);
        displacement += float3(dot(packed[b + 3], sine), dot(packed[b + 4], sine),
            dot(packed[b + 5], cosine)) * weights[templateIndex];
        gradient += float2(dot(packed[b + 6], sine), dot(packed[b + 7], sine)) * weights[templateIndex];
    }
    AnalyticalWaterField result;
    result.Displacement = displacement;
    result.Gradient = gradient;
    result.Normal = normalize(float3(gradient, 2 - displacement.z));
    return result;
}
AnalyticalWaterField ApplyNativeWaterContact(AnalyticalWaterField field, float3 contact)
{
    // PS7139 adds contact gradients before normalization, and uses the original analytical
    // height in the normal's Z component. Contact height affects displacement separately.
    field.Normal = normalize(float3(field.Gradient + contact.xy, 2 - field.Displacement.z));
    field.Displacement.z += contact.z;
    field.Gradient += contact.xy;
    return field;
}
""";
}
