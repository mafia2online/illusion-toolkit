namespace Illusion.Rendering.Shaders;

/// <summary>Source reconstruction of the PC FAST wind field PS4727. History and noise inputs are explicit caller-owned resources.</summary>
internal static class NativeVegetationFieldShader
{
    public const string Source = """
cbuffer Material:register(b0){float4 M[2];}
cbuffer Global:register(b1){float4 G[135];}
Texture2D<float4> PreviousVelocity:register(t0);
Texture2D<float4> PreviousOffset:register(t1);
Texture3D<float4> Noise:register(t2);
SamplerState LinearWrap:register(s0);
struct FieldOutput {float4 Velocity:SV_Target0;float4 Offset:SV_Target1;float4 Force:SV_Target2;};
FieldOutput main(float4 position:SV_Position,float4 uv:TEXCOORD0)
{
    float dt=G[54].x-G[53].x;
    float damping=exp2(log2(M[1].z)*dt);
    float2 world=(uv.xy*float2(1,-1)+float2(-.5,.5))*128+G[34].xy;
    world+=G[33].xy;
    float2 previousUv=uv.xy+float2(G[33].z,-G[33].w);
    float4 oldOffset=PreviousOffset.Sample(LinearWrap,previousUv);
    float2 offset=(oldOffset.xy-.5)*2;
    float2 velocity=PreviousVelocity.Sample(LinearWrap,previousUv).xy-.5;
    float noiseValue=0;
    for(int i=0;i<4;i++)
    {
        float3 samplePosition=float3(world,1);
        float3 p=float3(dot(G[123+i*3].xyw,samplePosition),dot(G[124+i*3].xyw,samplePosition),dot(G[125+i*3].xyw,samplePosition));
        noiseValue+=G[57][i]*(Noise.SampleLevel(LinearWrap,p,0).x-.5);
    }
    noiseValue=saturate(noiseValue);
    float2 force=noiseValue*G[13].xy*G[13].w*M[1].y;
    velocity=(velocity*4+dt*(-M[1].x*offset+force))*damping;
    float2 collision=0;float collisionAmount=0;
    for(int j=0;j<8;j++)
    {
        float2 delta=world-G[83+j].xy;
        float distance=length(delta);
        float2 direction=delta/distance;
        float factor=saturate(1-distance/(M[0].x*G[83+j].w));
        collision+=direction*factor;
        collisionAmount=max(collisionAmount,factor);
    }
    float obbAmount=0;
    for(int k=0;k<4;k++)if(G[71+k].y*G[71+k].x>0)
    {
        float2 delta=world-G[67+k].xy;
        float2 local=float2(dot(G[71+k].zw,delta),dot(float2(-G[71+k].w,G[71+k].z),delta));
        local=(local+local)*G[71+k].xy;
        local*=local;local*=local;local=1-local*local;
        obbAmount+=max(min(local.y,local.x),0);
    }
    float edge=1-saturate((length(uv.xy*2-1)-.9)*9.999998);
    FieldOutput output;
    output.Offset.z=max(obbAmount,edge*oldOffset.z);
    float2 advanced=velocity*dt+offset;
    float blend=min(collisionAmount*10,1);
    float response=(3-2*blend)*blend*blend+oldOffset.w;
    output.Offset.w=saturate(damping*response);
    output.Offset.xy=(collision*M[0].y+advanced)*.5+.5;
    output.Velocity=float4(velocity*.25+.5,0,0);
    output.Force=float4(force,0,0);
    return output;
}
""";
}
