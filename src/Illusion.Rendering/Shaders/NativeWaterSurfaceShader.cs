namespace Illusion.Rendering.Shaders;

/// <summary>Source-only analytical water composition. Native analytical field and screen-ray
/// reconstruction; toolkit lighting. Dynamic ripple/height inputs are explicitly zero.</summary>
internal static class NativeWaterSurfaceShader
{
    internal const string Header = """
cbuffer WaterConstants : register(b0)
{
    row_major float4x4 World;
    row_major float4x4 ViewProjection;
    float4 CameraTime;
    float4 RightTanX;
    float4 ForwardTanY;
    float4 Up;
    float4 LightMode;
    float4 FieldData;
    float4 TargetData;
    float4 Packed[32];
    float4 SunColor;
    float4 AmbientUp;
    float4 AmbientDown;
    float4 SpecParams;
    float4 Gamma;
    float4 FineNormal;
    float4 FoamState;
    float4 FoamColor;
    float4 FoamControls;
};
Texture2D<float4> Tex0 : register(t0);
Texture2D<float4> Tex1 : register(t1);
Texture2D<float4> Tex2 : register(t2);
Texture2D<float2> Lookup : register(t3);
Texture2D<float4> FineNoise : register(t4);
Texture2D<float4> FlowField : register(t5);
Texture2D<float4> FoamHistory : register(t6);
Texture2D<float4> FoamDetail : register(t7);
Texture2D<float4> FoamNormal : register(t8);
SamplerState Wrapped : register(s0);
SamplerState PointClamp : register(s1);
SamplerState LinearClamp : register(s2);
struct VertexIn { float3 Position:POSITION; float3 Normal:NORMAL; float2 UV:TEXCOORD;
    float3 Tangent:TANGENT; float3 Binormal:BINORMAL; };
struct DefinitionOut { float4 Position:SV_Position; float3 WorldPosition:TEXCOORD0;
    float2 UV:TEXCOORD1; float3 Normal:TEXCOORD2; float3 Tangent:TEXCOORD3; float3 Binormal:TEXCOORD4; };
DefinitionOut DefinitionVS(VertexIn input)
{
    DefinitionOut o;
    float4 world = mul(float4(input.Position, 1), World);
    o.Position = mul(world, ViewProjection);
    o.WorldPosition = world.xyz;
    o.UV = input.UV;
    o.Normal = mul(float4(input.Normal, 0), World).xyz;
    o.Tangent = mul(float4(input.Tangent, 0), World).xyz;
    o.Binormal = mul(float4(input.Binormal, 0), World).xyz;
    return o;
}
struct DefinitionTargets { float4 Mask:SV_Target0; float4 Color:SV_Target1; float4 Flow:SV_Target2; };
DefinitionTargets DefinitionPS(DefinitionOut input)
{
    DefinitionTargets o;
    o.Mask = Tex1.Sample(Wrapped, input.UV);
    float2 flow = FineNormal.w != 0 ? Tex2.Sample(Wrapped, input.UV).xy : float2(.5, .5);
    o.Flow = float4(flow, FineNormal.w, 1);
    // Alpha is authored water data, never an opacity/discard test.
    float4 color = Tex0.Sample(Wrapped, input.UV);
    // Native d996 dither phase is deliberately fixed to zero: no invented frame clock.
    float d0 = frac((input.Position.x + input.Position.y * 2 - 1.5) * .2);
    float d1 = frac(dot(input.Position.xy, float2(2.394, 3.234)));
    float dither = (d1 - d0) * .1666 + d0;
    o.Color = color + (dither * color) * .0333;
    return o;
}
""";

    internal static readonly string Source = Header + AnalyticalWaterShader.Functions + NativeWaterFineNormalShader.Functions + NativeWaterFoamSurfaceShader.Functions + """
struct WaveTargets { float4 Displacement:SV_Target0; float4 Normal:SV_Target1; };
WaveTargets WavePS(DefinitionOut input)
{
    float2 uv = input.Position.xy * FieldData.xy;
    float4 weights = Tex1.SampleLevel(PointClamp, uv, 0);
    AnalyticalWaterField field = EvaluateAnalyticalWater(Lookup, Wrapped, Packed,
        input.WorldPosition.xy, CameraTime.w, weights);
    float3 normal = normalize(field.Normal.x * normalize(input.Tangent)
        + field.Normal.y * normalize(input.Binormal) + field.Normal.z * normalize(input.Normal));
    float3 ray = input.WorldPosition - CameraTime.xyz;
    WaveTargets o;
    o.Displacement = float4(field.Displacement, sqrt(dot(ray, ray)));
    o.Normal = float4(normal * .5 + .5, 1);
    return o;
}
struct GridOut { float4 Position:SV_Position; float3 Normal:TEXCOORD0; float3 WorldPosition:TEXCOORD1; float2 Displacement:TEXCOORD2; };
GridOut GridVS(uint4 packedPosition:POSITION)
{
    float2 scale = 3.064427073695697e-5 * float2(1, 256);
    float2 uv = float2(dot(float2(packedPosition.xy), scale), dot(float2(packedPosition.zw), scale))
        + float2(-.0021982791367918253, -.0021982791367918253);
    float3 ray = normalize(RightTanX.xyz * ((uv.x * 2 - 1) * RightTanX.w)
        + ForwardTanY.xyz + Up.xyz * ((1 - uv.y * 2) * ForwardTanY.w));
    float4 displacement = Tex0.SampleLevel(PointClamp, uv, 0);
    float3 world = CameraTime.xyz + ray * displacement.w;
    bool inside = uv.x >= 0 && uv.y >= 0 && uv.x < 1 && uv.y < 1;
    world += displacement.xyz * (inside ? 1 : 0);
    GridOut o;
    o.Position = mul(float4(world, 1), ViewProjection);
    o.Normal = Tex1.SampleLevel(PointClamp, uv, 0).xyz * 2 - 1;
    o.WorldPosition = world;
    o.Displacement = displacement.xy;
    return o;
}
float4 SurfacePS(GridOut input):SV_Target
{
    float3 normal = normalize(input.Normal);
    float4 flowData = FlowField.SampleLevel(LinearClamp, input.Position.xy * TargetData.xy, 0);
    float2 flow = flowData.z > .5 ? flowData.xy * 2 - 1 : 0;
    if (FoamState.x != 0)
    {
        NativeWaterFoamMaterial foam = EvaluateNativeWaterFoamMaterial(FineNoise, FoamDetail, FoamNormal,
            FoamHistory, Wrapped, LinearClamp, input.WorldPosition.xy - input.Displacement * .5,
            normal, flow, CameraTime.w, 0, FoamState.zw, 0, FoamColor, FoamControls, 1, 1, 0);
        if (FoamState.y == 1) return float4(foam.Normal * .5 + .5, 1);
        if (FoamState.y == 2) return float4(saturate(foam.Roughness).xxx, 1);
        return float4(foam.DiffuseDiagnostic, 1);
    }
    if (FineNormal.z != 0) normal = EvaluateNativeWaterFineNormal(FineNoise, Wrapped,
        input.WorldPosition.xy - input.Displacement * .5, CameraTime.w, normal, FineNormal.x, FineNormal.y, flow);
    if (FieldData.z != 0) return float4(normal * .5 + .5, 1);
    float3 color = Tex2.SampleLevel(LinearClamp, input.Position.xy * TargetData.xy, 0).rgb;
    if (LightMode.w == 2) color = float3(.80, .81, .83);
    float3 L = normalize(-LightMode.xyz);
    float3 V = normalize(CameraTime.xyz - input.WorldPosition);
    float hemi = .5 + .5 * normal.z;
    float3 ambient = lerp(AmbientDown.rgb, AmbientUp.rgb, hemi);
    float cosAngle = dot(normal, L);
    float ndl = saturate(cosAngle);
    float3 diffuse = ambient + SunColor.rgb * ndl;
    float3 specular = 0, rim = 0;
    if (LightMode.w != 1)
    {
        float3 R = 2 * cosAngle * normal - L;
        float vdr = saturate(dot(V, R));
        float specTerm = pow(vdr, SpecParams.x) * SpecParams.y * step(1e-4, ndl);
        specular = SunColor.rgb * specTerm;
        float fresnel = pow(1 - saturate(dot(normal, V)), SpecParams.w);
        rim = AmbientUp.rgb * (fresnel * SpecParams.z);
    }
    return float4(pow(saturate(color * diffuse + specular + rim), Gamma.rgb), 1);
}
""";
}
