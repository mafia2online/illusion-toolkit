using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Rendering.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Shaders;

[StructLayout(LayoutKind.Sequential)]
public struct FrameConstants
{
    public Matrix4x4 Wvp;    // load as-is: HLSL mul(M, v) with column-select compensates for row-major
    public Matrix4x4 World;  // — likewise
    public Vector4 LightDir;
    public Vector4 BaseColor;

    /// <summary>Multiplies the sampled albedo, per material part. White for anything with a diffuse map; a
    /// car body has none and paints itself with a colour instead — see <c>MafiaMaterials.GetMaterialTextures</c>.
    /// Unlike the fields above this one changes WITHIN a mesh, so the draw pass rewrites the buffer whenever a
    /// part's tint differs from the last.</summary>
    public Vector4 Tint;

    public LightingConstants Lighting;  // shared Mafia-look block; HLSL side = ShaderCompiler.LightingCbufferTail
}

/// <summary>Textured mesh shader: the VS transforms and builds the world tangent frame; the PS is the shared
/// Mafia-look lighting (see <see cref="ShaderCompiler.MafiaLitPs"/>).</summary>
public sealed unsafe class MeshShader : MeshShaderBase
{
    private const string Hlsl = ShaderCompiler.SurfaceTextures + @"
cbuffer CB : register(b0)
{
    float4x4 WVP;
    float4x4 World;
    float4   LightDir;
    float4   BaseColor;
    float4   Tint;" + ShaderCompiler.LightingCbufferTail + @"
};
struct VSIn  { float3 pos : POSITION; float3 nrm : NORMAL; float2 uv : TEXCOORD; float3 tan : TANGENT; float3 bin : BINORMAL; float4 color : COLOR; float2 decalUv : TEXCOORD2; };" + ShaderCompiler.PsInStruct + @"
PSIn VSMain(VSIn i)
{
    PSIn o;
    o.pos  = mul(WVP, float4(i.pos, 1.0));
    o.nrm  = mul((float3x3)World, i.nrm);
    o.uv   = i.uv;
    o.color = i.color; o.decalUv = i.decalUv;
    o.wpos = mul(World, float4(i.pos, 1.0)).xyz;                   // world position for the specular view vector
    o.tan  = mul((float3x3)World, i.tan);                          // tangent frame → world for normal mapping
    o.bin  = mul((float3x3)World, i.bin);
    return o;
}" + ShaderCompiler.MafiaLitPs;

    public MeshShader(GpuContext gpu)
    {
        using D3DCompiler compiler = D3DCompiler.GetApi();

        ComPtr<ID3D10Blob> vsCode = ShaderCompiler.Compile(compiler, Hlsl, "VSMain", "vs_5_0", "mesh");
        ComPtr<ID3D10Blob> psCode = ShaderCompiler.Compile(compiler, Hlsl, "PSMain", "ps_5_0", "mesh");
        (_vs, _ps) = ShaderCompiler.CreateShaders(gpu, vsCode, psCode);

        byte* posName = (byte*)SilkMarshal.StringToPtr("POSITION");
        byte* nrmName = (byte*)SilkMarshal.StringToPtr("NORMAL");
        byte* uvName = (byte*)SilkMarshal.StringToPtr("TEXCOORD");
        byte* tanName = (byte*)SilkMarshal.StringToPtr("TANGENT");
        byte* binName = (byte*)SilkMarshal.StringToPtr("BINORMAL");
        byte* colorName = (byte*)SilkMarshal.StringToPtr("COLOR");
        var elems = stackalloc InputElementDesc[7];
        elems[0] = ShaderCompiler.VertexElement(posName, 0, Format.FormatR32G32B32Float, 0);
        elems[1] = ShaderCompiler.VertexElement(nrmName, 0, Format.FormatR32G32B32Float, 12);
        elems[2] = ShaderCompiler.VertexElement(uvName, 0, Format.FormatR32G32Float, 24);
        elems[3] = ShaderCompiler.VertexElement(tanName, 0, Format.FormatR32G32B32Float, 32);
        elems[4] = ShaderCompiler.VertexElement(binName, 0, Format.FormatR32G32B32Float, 44);
        elems[5] = ShaderCompiler.VertexElement(colorName, 0, Format.FormatR32G32B32A32Float, 0); elems[5].InputSlot = 2;
        elems[6] = ShaderCompiler.VertexElement(uvName, 2, Format.FormatR32G32Float, 0); elems[6].InputSlot = 3;
        ID3D11InputLayout* layout = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateInputLayout(
            elems, 7, vsCode.GetBufferPointer(), vsCode.GetBufferSize(), ref layout));
        _layout = layout;
        SilkMarshal.Free((nint)posName);
        SilkMarshal.Free((nint)nrmName);
        SilkMarshal.Free((nint)uvName);
        SilkMarshal.Free((nint)tanName);
        SilkMarshal.Free((nint)binName);
        SilkMarshal.Free((nint)colorName);

        vsCode.Dispose();
        psCode.Dispose();

        _cb = GpuBuffers.CreateConstant<FrameConstants>(gpu);
    }

    public void UpdateConstants(ComPtr<ID3D11DeviceContext> ctx, ref FrameConstants c)
        => GpuBuffers.UpdateConstant(ctx, _cb, ref c);
}
