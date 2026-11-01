namespace Illusion.Rendering.Shaders;

/// <summary>Native PS1754 water-contact simulation equations, with explicit native field,
/// stamp, capture, wind and view inputs. Resource production and engine feedback remain caller-owned.</summary>
public static class NativeWaterContactShader
{
    public const string Functions = """
struct NativeWaterContactOutput { float4 State : SV_Target0; float4 Foam : SV_Target1; };
NativeWaterContactOutput EvaluateNativeWaterContact(Texture2D<float4> previous,
    Texture2D<float4> previousFoam, Texture2D<float4> foamSource,
    Texture2D<float4> stamp, Texture2D<float4> wind, SamplerState wrapped,
    SamplerState bordered, float2 uv, float4 origins, float2 captureCenter,
    float2 windOrigin, float windStrength, float dt)
{
    float2 previousUv = mad(origins.xy - origins.zw, asfloat(0x3c2aaaab), uv);
    float4 state = previous.SampleLevel(bordered, previousUv, 0);
    float foam = previousFoam.SampleLevel(bordered, previousUv, 0).x;
    if (dt > 0)
    {
        float stampHeight = stamp.SampleLevel(bordered, uv, 0).x;
        if (stampHeight > 0)
        {
            foam = max(min(abs(stampHeight - state.z), 1), foam);
            state = float4(0, 0, -stampHeight, 0);
        }
        else
        {
            float2 offset = uv - .5;
            float2 world = mad(offset, 96, origins.xy);
            float2 direction = (captureCenter - world) * .00390625;
            float lengthValue = max(sqrt(dot(direction, direction)), .00001);
            direction *= rsqrt(lengthValue);
            float2 captureUv = mad(direction, float2(.5, -.5), .5);
            float2 capture = foamSource.SampleLevel(bordered, captureUv, 0).xz;
            float captureForce = (1 - capture.y) * capture.x;
            float up = previous.SampleLevel(bordered, previousUv, 0, int2(0, -1)).z;
            float left = previous.SampleLevel(bordered, previousUv, 0, int2(-1, 0)).z;
            float right = previous.SampleLevel(bordered, previousUv, 0, int2(1, 0)).z;
            float down = previous.SampleLevel(bordered, previousUv, 0, int2(0, 1)).z;
            float foamUp = previousFoam.SampleLevel(bordered, previousUv, 0, int2(0, -1)).x;
            float foamLeft = previousFoam.SampleLevel(bordered, previousUv, 0, int2(-1, 0)).x;
            float foamRight = previousFoam.SampleLevel(bordered, previousUv, 0, int2(1, 0)).x;
            float foamDown = previousFoam.SampleLevel(bordered, previousUv, 0, int2(0, 1)).x;
            float2 windUv = mad(offset, 96, -windOrigin);
            windUv = mad(windUv, .0078125, .5);
            windUv.y = 1 - windUv.y;
            float2 windValue = wind.SampleLevel(wrapped, windUv, 0).xy;
            bool haveWind = windValue.x != 0 && windValue.y != 0;
            windValue = mad(windValue, 2, -1);
            float windAmount = haveWind ? sqrt(dot(windValue, windValue)) : 0;
            windAmount *= windStrength;
            float heightSum = left + up;
            heightSum = right + heightSum;
            heightSum = down + heightSum;
            float foamSum = foamLeft + foamUp;
            foamSum = foamRight + foamSum;
            foamSum = foamDown + foamSum;
            float heightLaplacian = mad(heightSum, .25, -state.z);
            float foamLaplacian = mad(foamSum, .25, -foam);
            float foamAcceleration = mad(foamLaplacian, asfloat(0x43855556), foam * -25);
            float foamVelocity = mad(foamAcceleration, dt, state.w);
            float damping = min(exp2(asfloat(0xbd159c1f) * dt), 1);
            float2 edgeDistances = min(uv, 1 - uv);
            float edge = saturate(min(edgeDistances.x, edgeDistances.y) * 64);
            state.xy = (float2(left, up) - float2(right, down)) * edge;
            state.xy += state.xy;
            float acceleration = mad(heightLaplacian, asfloat(0x43855556), state.z * -25);
            acceleration = mad(windAmount, .025, acceleration);
            acceleration = mad(captureForce, -2, acceleration);
            float velocity = mad(acceleration, dt, state.w);
            velocity *= damping; velocity *= edge;
            state.w = min(max(velocity, -1), 1);
            float height = mad(state.w, dt, state.z);
            height *= damping; height *= edge;
            state.z = min(max(height, -1), 1);
            foam = mad(foamVelocity, dt, foam);
            foam *= damping;
            foam = saturate(edge * foam);
        }
    }
    NativeWaterContactOutput result; result.State = state; result.Foam = foam.xxxx; return result;
}
""";
}
