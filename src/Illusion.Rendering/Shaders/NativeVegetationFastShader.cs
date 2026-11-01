namespace Illusion.Rendering.Shaders;

/// <summary>Verified FAST current-position stage. Base world position and the separately transformed
/// native TEXCOORD4..7 object origin are explicit; these two inputs must not be conflated.</summary>
internal static class NativeVegetationFastShader
{
    public const string Functions = """
float3 EvaluateNativeFastVegetationWind(float3 baseWorldPosition,float2 nativeObjectOrigin,float color0Red,
    float2 d219,float2 viewOrigin,float2 fieldOrigin,float2 originDelta,
    Texture2D<float4> currentOffset,SamplerState linearWrap)
{
    float2 uv=(((nativeObjectOrigin+viewOrigin)-fieldOrigin)-originDelta)*.0078125+.5;
    uv.y=1-uv.y;
    float2 displacement=currentOffset.SampleLevel(linearWrap,uv,0).xy*2-1;
    baseWorldPosition.xy+=displacement*(color0Red*(d219.y-d219.x)+d219.x);
    return baseWorldPosition;
}
""";
}
