namespace Illusion.Rendering.Shaders;

/// <summary>Source port of native rain-ripple PS6243. The caller supplies the original state,
/// three-dimensional noise, coordinates, sampler and global parameters; this does not synthesize
/// runtime weather or establish the separate water-contact ripple binding.</summary>
public static class NativeRainRippleShader
{
    public const string Functions = """
struct NativeRainRippleOutput
{
    float4 HeightVelocity : SV_Target0;
    float4 Normal : SV_Target1;
};
NativeRainRippleOutput EvaluateNativeRainRipple(Texture2D<float4> previous,
    Texture3D<float4> noise, SamplerState sampled, float2 uv,
    float previousTime, float currentTime, float rainParameter)
{
    float seed = noise.SampleLevel(sampled, float3(uv * 4, frac(currentTime)), 0).x;
    seed = mad(uv.x, .00390625, seed);
    seed = mad(uv.y, .00390625, seed);
    bool spawn = abs(frac(seed) - frac(100 * currentTime)) < (1 / rsqrt(rainParameter)) * .0005;
    float dt = min(currentTime - previousTime, .05);
    float damping = exp2(dt * -1.736966);
    float4 old = previous.SampleLevel(sampled, uv, 0);
    float velocity = mad(dt, mad(old.z - old.x, 200, -old.x * 10), old.y) * damping;
    velocity = min(max(velocity, -15), 15);
    float height = min(max(mad(velocity, dt, old.x), -15), 15);
    NativeRainRippleOutput result;
    result.HeightVelocity = spawn ? float4(5, 0, 5, 10)
        : float4(height, velocity, height, max(old.w - dt, 0));
    float a = previous.SampleLevel(sampled, uv, 0, int2(1, -1)).x - old.x;
    float b = previous.SampleLevel(sampled, uv, 0, int2(1, 1)).x - old.x;
    float3 normal = normalize(float3(-b - a, a - b, 2));
    result.Normal = float4(normal.xy * .5 + .5, normal.z,
        spawn ? 1 : max(min(abs(old.x * .1), .5), old.w));
    return result;
}
""";
}
