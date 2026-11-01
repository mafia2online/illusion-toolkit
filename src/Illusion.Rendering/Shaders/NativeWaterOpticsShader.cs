namespace Illusion.Rendering.Shaders;

/// <summary>Native PS1078 depth attenuation and dielectric BRDF, with bounded editor
/// screen-space radiance tracing. The capture is not a replacement for native environment probes.</summary>
public static class NativeWaterOpticsShader
{
    public const string NativeFunctions = """
float NativeWaterDepthFade(float waterZ, float opaqueZ, float threshold)
{
    return pow(saturate(abs(waterZ - opaqueZ) / max(threshold, .000001)), 1.5);
}
float NativeWaterDielectricBrdf(Texture2D<float4> brdf, SamplerState linearClamp,
    float3 view, float3 normal, float3 coarse, float roughness, float depthFade, float3 referenceNormal)
{
    float3 reflected = reflect(-view, normal);
    float3 adjusted = reflected + coarse * max(abs(dot(reflected, coarse)), .5);
    float3 halfway = normalize(lerp(referenceNormal, normalize(view + adjusted), depthFade));
    float2 terms = brdf.SampleLevel(linearClamp, float2(saturate(dot(halfway, view)), roughness), 0).xy;
    return terms.x * .02 + terms.y;
}
float NativeWaterDielectricBrdf(Texture2D<float4> brdf, SamplerState linearClamp,
    float3 view, float3 normal, float3 coarse, float roughness)
{
    return NativeWaterDielectricBrdf(brdf, linearClamp, view, normal, coarse, roughness, 1, coarse);
}
float NativeWaterVarianceRoughness(Texture2D<float4> noise, SamplerState wrapped,
    float2 world, float time, float scale, float intensity, float2 flow)
{
    float phase = frac(time * .125), halfPhase = frac(time * .125 + .5), weight = 2 * abs(phase - .5);
    float2 minus = (world - time * .375) * scale, plus = (world.yx + time * .375) * scale;
    float4 a = lerp(noise.Sample(wrapped, (minus + flow * phase) * float2(1,-1)),
                    noise.Sample(wrapped, (minus + flow * halfPhase) * float2(1,-1)), weight);
    float4 b = lerp(noise.Sample(wrapped, plus + flow.yx * phase), noise.Sample(wrapped, plus + flow.yx * halfPhase), weight);
    float strength = (length(flow) + 1) * intensity;
    return 1 - min(sqrt(max(0, (a.r + b.r + a.b + b.b) * strength * .25)), 1);
}

""";
    public const string Functions = NativeFunctions + """
// Bounded current-frame trace: 64 coarse steps, then six depth-crossing refinements.
// Every hit must be a visible opaque pixel behind the water's own depth. No clamped
// screen-edge pixels or geometry in front of the water can become transmission hits.
bool TraceWaterRadiance(float3 origin, float3 direction, float minimumDepth, out float3 radiance)
{
    radiance = 0;
    float previous = .05;
    [loop] for (int step = 1; step <= 64; step++)
    {
        float distance = .05 + step * step * .0625;
        float3 world = origin + direction * distance;
        float4 clip = mul(float4(world, 1), ViewProjection);
        if (clip.w <= 0) return false;
        float2 uv = clip.xy / clip.w * float2(.5, -.5) + .5;
        if (any(uv <= 0) || any(uv >= 1)) return false;
        float hardware = OpaqueDepth.SampleLevel(PointClamp, uv, 0);
        float scene = OpticsDepth.x * OpticsDepth.y / (OpticsDepth.y - hardware * (OpticsDepth.y - OpticsDepth.x));
        float rayZ = dot(world - CameraTime.xyz, CameraForward.xyz);
        if (hardware < .999999 && scene > minimumDepth + .001 && rayZ >= scene)
        {
            float lo = previous, hi = distance;
            [unroll] for (int refine = 0; refine < 6; refine++)
            {
                float middle = (lo + hi) * .5;
                float3 candidate = origin + direction * middle;
                float4 q = mul(float4(candidate, 1), ViewProjection);
                float2 p = q.xy / q.w * float2(.5, -.5) + .5;
                float d = OpaqueDepth.SampleLevel(PointClamp, p, 0);
                float z = OpticsDepth.x * OpticsDepth.y / (OpticsDepth.y - d * (OpticsDepth.y - OpticsDepth.x));
                if (dot(candidate - CameraTime.xyz, CameraForward.xyz) >= z) hi = middle; else lo = middle;
            }
            float4 hit = mul(float4(origin + direction * hi, 1), ViewProjection);
            float2 hitUV = hit.xy / hit.w * float2(.5, -.5) + .5;
            float d = OpaqueDepth.SampleLevel(PointClamp, hitUV, 0);
            float z = OpticsDepth.x * OpticsDepth.y / (OpticsDepth.y - d * (OpticsDepth.y - OpticsDepth.x));
            float residual = dot(origin + direction * hi - CameraTime.xyz, CameraForward.xyz) - z;
            if (d < .999999 && z > minimumDepth + .001 && residual >= 0 && residual < max(.05, distance * .01))
            { radiance = OpaqueColor.SampleLevel(LinearClamp, hitUV, 0).rgb; return true; }
        }
        previous = distance;
    }
    return false;
}
float3 ComposeWaterOptics(float3 world, float3 normal, float3 coarse, float2 uv, float depthFade, float roughness, float3 lit)
{
    float3 view = normalize(CameraTime.xyz - world);
    float waterDepth = dot(world - CameraTime.xyz, CameraForward.xyz);
    float3 transmitted = OpaqueColor.SampleLevel(LinearClamp, uv, 0).rgb;
    if (OpticsState.z != 0)
    {
        float3 refracted;
        if (TraceWaterRadiance(world, refract(-view, normal, 1 / 1.333), waterDepth, refracted)) transmitted = refracted;
        lit = lerp(transmitted, lit, depthFade);
    }
    if (OpticsState.y != 0)
    {
        float3 reflected;
        bool reflectedHit;
        if (OpticsState.w == 2)
        {
            reflected = TemporalReflection.SampleLevel(LinearClamp, uv, 0).rgb;
            reflectedHit = ReflectionDepthValidity.SampleLevel(PointClamp, uv, 0).y > .5;
        }
        else reflectedHit = TraceWaterRadiance(world + normal * .01, reflect(-view, normal), 0, reflected);
        if (reflectedHit)
        {
            float fresnel = saturate(NativeWaterDielectricBrdf(OpticsBrdf, LinearClamp, view, normal, normalize(coarse), lerp(1, roughness, depthFade), depthFade, normalize(coarse)));
            float reflectionWeight = depthFade * (depthFade * .75 + .25) * .5;
            lit = lerp(lit, reflected, fresnel * reflectionWeight);
        }
    }
    return lit;
}
""";
}
