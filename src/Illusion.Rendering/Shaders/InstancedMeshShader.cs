using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Rendering.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Shaders;

[StructLayout(LayoutKind.Sequential)]
public struct InstancedConstants
{
    public Matrix4x4 ViewProj;   // row-major (like in System.Numerics); in HLSL — row_major
    public Vector4 LightDir;
    public Vector4 BaseColor;    // shared MafiaLitPs: .a=1 → texture, .a=0 → flat .rgb (Solid/Wireframe)

    /// <summary>Albedo tint, declared only because the pixel shader is shared with <see cref="MeshShader"/>.
    /// Instanced content is crash props, which all carry a diffuse map, so this stays white.</summary>
    public Vector4 Tint;

    public LightingConstants Lighting;  // shared Mafia-look block; HLSL side = ShaderCompiler.LightingCbufferTail
}

/// <summary>
/// Instanced variant of <see cref="MeshShader"/> for city_crash: the instance's world matrix comes
/// per-instance from the second vertex slot (4×float4), not from the cbuffer. Geometry is uploaded once,
/// drawn via DrawIndexedInstanced. PS is the shared Mafia-look lighting.
/// </summary>
public sealed unsafe class InstancedMeshShader : MeshShaderBase
{
    private const string Hlsl = ShaderCompiler.SurfaceTextures + VegetationWindBindings.Hlsl + @"
cbuffer CB : register(b0)
{
    row_major float4x4 ViewProj;
    float4   LightDir;
    float4   BaseColor;
    float4   Tint;" + ShaderCompiler.LightingCbufferTail + @"
};
struct VSIn
{
    float3 pos : POSITION; float3 nrm : NORMAL; float2 uv : TEXCOORD; float3 tan : TANGENT; float3 bin : BINORMAL;
    float4 color : COLOR; float4 color1 : COLOR1; float2 decalUv : TEXCOORD2;
    float4 w0 : WORLD0; float4 w1 : WORLD1; float4 w2 : WORLD2; float4 w3 : WORLD3;
};" + ShaderCompiler.PsInStruct + @"
PSIn VSMain(VSIn i)
{
    PSIn o;
    float4x4 world = float4x4(i.w0, i.w1, i.w2, i.w3);   // rows = System.Numerics row-major
    float4 wp = mul(float4(i.pos, 1.0), world);
    if (NativeWindFlags.x > .5) wp.xyz = ApplyNativeVegetationWind(i.pos,i.color,i.color1.w,world);          // row-vector: pos * World
    o.pos  = mul(wp, ViewProj);
    o.nrm  = mul(i.nrm, (float3x3)world);
    o.uv   = i.uv;
    o.color = i.color; o.decalUv = i.decalUv;
    o.wpos = wp.xyz;                                      // world position for the specular view vector
    o.tan  = mul(i.tan, (float3x3)world);                // tangent frame → world (per-instance) for normal mapping
    o.bin  = mul(i.bin, (float3x3)world);
    return o;
}" + ShaderCompiler.MafiaLitPs;

    public InstancedMeshShader(GpuContext gpu)
    {
        using D3DCompiler compiler = D3DCompiler.GetApi();

        ComPtr<ID3D10Blob> vsCode = ShaderCompiler.Compile(compiler, Hlsl, "VSMain", "vs_5_0", "instanced-mesh");
        ComPtr<ID3D10Blob> psCode = ShaderCompiler.Compile(compiler, Hlsl, "PSMain", "ps_5_0", "instanced-mesh");
        (_vs, _ps) = ShaderCompiler.CreateShaders(gpu, vsCode, psCode);

        byte* posName = (byte*)SilkMarshal.StringToPtr("POSITION");
        byte* nrmName = (byte*)SilkMarshal.StringToPtr("NORMAL");
        byte* uvName = (byte*)SilkMarshal.StringToPtr("TEXCOORD");
        byte* tanName = (byte*)SilkMarshal.StringToPtr("TANGENT");
        byte* binName = (byte*)SilkMarshal.StringToPtr("BINORMAL");
        byte* wName = (byte*)SilkMarshal.StringToPtr("WORLD");
        byte* colorName = (byte*)SilkMarshal.StringToPtr("COLOR");

        var elems = stackalloc InputElementDesc[12];
        elems[0] = ShaderCompiler.VertexElement(posName, 0, Format.FormatR32G32B32Float, 0);
        elems[1] = ShaderCompiler.VertexElement(nrmName, 0, Format.FormatR32G32B32Float, 12);
        elems[2] = ShaderCompiler.VertexElement(uvName, 0, Format.FormatR32G32Float, 24);
        elems[3] = ShaderCompiler.VertexElement(tanName, 0, Format.FormatR32G32B32Float, 32);
        elems[4] = ShaderCompiler.VertexElement(binName, 0, Format.FormatR32G32B32Float, 44);
        // Per-instance world matrix — slot 1, 4 rows of float4, step 1 instance.
        for (uint r = 0; r < 4; r++)
        {
            elems[5 + r] = new InputElementDesc
            {
                SemanticName = wName,
                SemanticIndex = r,
                Format = Format.FormatR32G32B32A32Float,
                InputSlot = 1,
                AlignedByteOffset = r * 16,
                InputSlotClass = InputClassification.PerInstanceData,
                InstanceDataStepRate = 1,
            };
        }
        elems[9] = ShaderCompiler.VertexElement(colorName, 0, Format.FormatR32G32B32A32Float, 0); elems[9].InputSlot = 2;
        elems[10] = ShaderCompiler.VertexElement(uvName, 2, Format.FormatR32G32Float, 0); elems[10].InputSlot = 3;
        elems[11] = ShaderCompiler.VertexElement(colorName, 1, Format.FormatR32G32B32A32Float, 0); elems[11].InputSlot = 4;
        ID3D11InputLayout* layout = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateInputLayout(
            elems, 12, vsCode.GetBufferPointer(), vsCode.GetBufferSize(), ref layout));
        _layout = layout;

        SilkMarshal.Free((nint)posName);
        SilkMarshal.Free((nint)nrmName);
        SilkMarshal.Free((nint)uvName);
        SilkMarshal.Free((nint)tanName);
        SilkMarshal.Free((nint)binName);
        SilkMarshal.Free((nint)wName);
        SilkMarshal.Free((nint)colorName);
        vsCode.Dispose();
        psCode.Dispose();

        _cb = GpuBuffers.CreateConstant<InstancedConstants>(gpu);
    }

    public void UpdateConstants(ComPtr<ID3D11DeviceContext> ctx, ref InstancedConstants c)
        => GpuBuffers.UpdateConstant(ctx, _cb, ref c);
}
