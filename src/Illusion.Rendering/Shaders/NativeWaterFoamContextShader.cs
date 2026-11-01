namespace Illusion.Rendering.Shaders;

internal static class NativeWaterFoamContextShader
{
    internal const string Fullscreen = """
float4 main(uint id:SV_VertexID):SV_Position{float2 uv=float2((id<<1)&2,id&2);return float4(uv*float2(2,-2)+float2(-1,1),0,1);}
""";
    internal const string Depth = """
cbuffer A:register(b0){float4 DepthData;}Texture2D<float> HardwareDepth:register(t0);
float main(float4 p:SV_Position):SV_Target{float z=HardwareDepth.Load(int3(p.xy,0));return DepthData.x/(DepthData.y-z*(DepthData.y-DepthData.x));}
""";
    internal const string HeightVertex = """
cbuffer A:register(b0){row_major float4x4 World;row_major float4x4 ViewProjection;}
struct O{float4 P:SV_Position;float Z:TEXCOORD0;};O main(float3 p:POSITION){O o;float4 w=mul(float4(p,1),World);o.P=mul(w,ViewProjection);o.Z=w.z;return o;}
""";
    internal const string HeightPixel = """
float main(float4 p:SV_Position,float z:TEXCOORD0):SV_Target{return z;}
""";
    internal const string Expand = """
Texture2D<float> Image:register(t0);SamplerState Point:register(s0);
float main(float4 p:SV_Position):SV_Target{float2 uv=p.xy/256;float4 a=Image.GatherRed(Point,uv,int2(-1,1));float4 b=Image.GatherRed(Point,uv,int2(1,1));float4 c=Image.GatherRed(Point,uv,int2(1,-1));float4 d=Image.GatherRed(Point,uv,int2(-1,-1));float4 m=max(max(a,b),max(c,d));float v=max(max(m.x,m.y),max(m.z,m.w));return d.y==-65504?v:d.y;}
""";
}
