namespace Illusion.Rendering.Shaders;

/// <summary>Source-owned native reflection temporal kernels PS326/PS1403. Inputs are explicit
/// render products; this module does not assign an environment-probe role to arbitrary DDS files.</summary>
public static class NativeReflectionTemporalShader
{
    public const string Functions = """
struct NativeReflectionTemporalOutput
{
    float4 Radiance : SV_Target0;
    float4 Confidence : SV_Target1;
};
float3 ReflectionHistoryClip(Texture2D<float4> current, SamplerState sampled,
    float2 uv, float3 center, float3 history, float weight)
{
    if (1 - weight > .01)
    {
        float3 sum = center, squares = center * center;
        // Original kernel order, including the asymmetric final eight samples.
        const int2 offsets[20] = {int2(-1,0),int2(1,0),int2(0,-1),int2(0,1),
            int2(-1,-1),int2(1,-1),int2(1,1),int2(-1,1),int2(-2,0),int2(2,0),
            int2(0,-2),int2(0,2),int2(-1,2),int2(1,2),int2(-1,-2),int2(1,-2),
            int2(2,-1),int2(2,1),int2(-2,-1),int2(-2,1)};
        [unroll] for (int i = 0; i < 20; ++i)
        {
            float3 value = current.SampleLevel(sampled, uv, 0, offsets[i]).xyz;
            sum += value;
            squares = mad(value, value, squares);
        }
        float3 mean = sum * (1.0 / 21);
        float3 variance = mad(squares, (1.0 / 21), -mean * mean);
        float3 sigma = sqrt(abs(variance));
        float3 low = mad(sum, (1.0 / 21), -sigma);
        float3 high = mad(sum, (1.0 / 21), sigma);
        float3 combined = high + low;
        float3 extent = (high - low) * .5;
        float3 relative = mad(-combined, .5, history) / max(extent, .00001);
        float outside = max(abs(relative.z), max(abs(relative.y), abs(relative.x)));
        if (outside > 1)
            history = mad(combined, .5, extent * relative / outside);
    }
    return history;
}
NativeReflectionTemporalOutput ReflectionTemporalCombine(Texture2D<float4> current,
    Texture2D<float4> previous, Texture2D<float4> roughness,
    Texture2D<float4> previousConfidence, Texture2D<float4> rejection,
    SamplerState currentSampler, SamplerState historySampler,
    float2 uv, float2 historyUv, float3 ray, float3 previousRay,
    float historyValidity, bool rejectEmptyHistory)
{
    ray /= max(sqrt(dot(ray, ray)), .00001);
    previousRay /= max(sqrt(dot(previousRay, previousRay)), .00001);
    float3 old = min(previous.SampleLevel(historySampler, historyUv, 0).xyz, 65504);
    float4 gathered = rejection.GatherRed(currentSampler, uv);
    float floorValue = min(min(gathered.y, gathered.x), min(gathered.w, gathered.z));
    float confidence = previousConfidence.SampleLevel(historySampler, historyUv, 0).x;
    confidence = historyValidity > 0 ? confidence : 0;
    confidence = max(confidence - (1.0 / 60), floorValue);
    float rough = roughness.SampleLevel(currentSampler, uv, 0).x;
    rough = min(rough * rough * 37.5, 1);
    rough = max(rough, saturate(confidence * 5));
    float angle = max(mad(-mad(rough, -95, 100), length(ray - previousRay), 1), 0);
    float angle2 = angle * angle;
    float angle4 = angle2 * angle2;
    float weight = min(rough, angle * angle4);
    float3 now = min(current.Sample(currentSampler, uv).xyz, 65504);
    float3 clipped = ReflectionHistoryClip(current, currentSampler, uv, now, old, weight);
    weight = min(weight + .5, saturate(historyValidity * 4) * .96875);
    const float3 luminance = float3(.2125, .7154, .0721);
    float currentLuma = max(dot(now, luminance) + .00001, .00001);
    float historyLuma = max(dot(clipped, luminance) + .00001, .00001);
    float3 difference = mad(weight, clipped - now, now) - clipped;
    float change = dot((weight >= .1 ? 1 : 0) * abs(difference), luminance) + .00001;
    float currentWeight = (1 - weight) / (change / historyLuma + 1);
    float a = currentWeight / (currentLuma + 1);
    float b = weight / (historyLuma + 1);
    float3 combined = mad(now, a, b * clipped) / max(mad(currentWeight, 1 / (currentLuma + 1), b), .00001);
    NativeReflectionTemporalOutput result;
    result.Radiance = float4(rejectEmptyHistory && dot(old, float3(1,1,1)) == 0 ? now : combined, 1);
    result.Confidence = float4(confidence, 0, 0, 0);
    return result;
}
NativeReflectionTemporalOutput EvaluateNativeReflectionTemporal(Texture2D<float4> current,
    Texture2D<float4> previous, Texture2D<float4> roughness,
    Texture2D<float4> previousConfidence, Texture2D<float4> motion,
    Texture2D<float4> rejection, SamplerState currentSampler, SamplerState historySampler,
    float2 uv, float3 rayOrigin, float3 rayX, float3 rayY,
    float3 previousRayOrigin, float3 previousRayX, float3 previousRayY)
{
    float4 velocity = motion.SampleLevel(currentSampler, uv, 0);
    float2 previousUv = velocity.xy + uv;
    float validity = mad(velocity.w, .25, .75) * velocity.z;
    float3 ray = mad(rayY, uv.y, mad(rayX, uv.x, rayOrigin));
    float3 oldRay = mad(previousRayY, previousUv.y, mad(previousRayX, previousUv.x, previousRayOrigin));
    return ReflectionTemporalCombine(current, previous, roughness, previousConfidence, rejection,
        currentSampler, historySampler, uv, previousUv, ray, oldRay, validity, false);
}
NativeReflectionTemporalOutput EvaluateNativeReflectionTemporalReproject(Texture2D<float4> depth,
    Texture2D<float4> current, Texture2D<float4> previous, Texture2D<float4> roughness,
    Texture2D<float4> previousConfidence, Texture2D<float4> rejection,
    SamplerState currentSampler, SamplerState historySampler, float2 uv,
    float3 rayOrigin, float3 rayX, float3 rayY, float3 camera, float3 previousCamera,
    float4 previousProjectionX, float4 previousProjectionY, float4 previousProjectionW)
{
    float z = depth.Sample(currentSampler, uv).x;
    float3 ray = mad(rayY, uv.y, mad(rayX, uv.x, rayOrigin));
    float3 relative = z * ray;
    float3 world = mad(ray, z, camera);
    float3 oldRay = world - previousCamera;
    float4 position = float4(world, 1);
    float w = max(dot(previousProjectionW, position), .00001);
    float2 projected = float2(dot(previousProjectionX, position), dot(previousProjectionY, position)) / w;
    float2 oldUv = mad(projected, float2(.5,-.5), .5);
    float valid = (oldUv.x >= 0 ? 1 : 0) * (oldUv.y >= 0 ? 1 : 0)
        * (oldUv.x <= 1 ? 1 : 0) * (oldUv.y <= 1 ? 1 : 0);
    return ReflectionTemporalCombine(current, previous, roughness, previousConfidence, rejection,
        currentSampler, historySampler, uv, oldUv, relative, oldRay, valid, true);
}
""";
}
