using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Rendering.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Shaders;

/// <summary>
/// The bone palette: one matrix per bone, taking a vertex from the pose the mesh was authored in to the pose
/// the bone is in now. At rest every entry is the identity, so a model nobody has touched draws exactly as
/// the unskinned shader draws it — which is the test that the whole path is right.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct BonePalette
{
    /// <summary>Bones past this are not skinned. A car's rig is 83, the shared human biped 64.</summary>
    public const int MaxBones = 128;

    public fixed float M[MaxBones * 16];

    public static BonePalette Identity
    {
        get
        {
            var palette = default(BonePalette);
            for (int i = 0; i < MaxBones; i++) palette.Set(i, Matrix4x4.Identity);
            return palette;
        }
    }

    public void Set(int bone, in Matrix4x4 m)
    {
        if ((uint)bone >= MaxBones) return;
        fixed (float* dst = M)
        fixed (Matrix4x4* src = &m)
        {
            System.Buffer.MemoryCopy(src, dst + (bone * 16), 64, 64);
        }
    }

    /// <summary>What one bone currently does to the mesh — the entry <see cref="Set"/> wrote.</summary>
    public readonly Matrix4x4 Get(int bone)
    {
        if ((uint)bone >= MaxBones) return Matrix4x4.Identity;
        Matrix4x4 m = default;
        fixed (float* src = M)
        {
            System.Buffer.MemoryCopy(src + (bone * 16), &m, 64, 64);
        }
        return m;
    }
}

/// <summary>
/// The mesh shader for a skinned model. Identical to <see cref="MeshShader"/> — same lighting, same textures,
/// same constants at b0 — with two additions: a second vertex stream carrying four bone influences per vertex,
/// and the bone palette at b1. The blend is plain linear skinning; the weights sum to one on every vertex the
/// game ships (measured, <c>--probe-skinning</c>).
/// </summary>
public sealed unsafe class SkinnedMeshShader : MeshShaderBase
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
cbuffer Bones : register(b1) { float4x4 Bone[128]; };
struct VSIn
{
    float3 pos : POSITION; float3 nrm : NORMAL; float2 uv : TEXCOORD;
    float3 tan : TANGENT;  float3 bin : BINORMAL;
    uint4  idx : BLENDINDICES; float4 wgt : BLENDWEIGHT;
    float4 color : COLOR; float2 decalUv : TEXCOORD2;
};" + ShaderCompiler.PsInStruct + @"
PSIn VSMain(VSIn i)
{
    // One blended matrix, then the ordinary transform — cheaper than skinning each channel separately and
    // it keeps the tangent frame consistent with the position.
    float4x4 skin = Bone[i.idx.x] * i.wgt.x
                  + Bone[i.idx.y] * i.wgt.y
                  + Bone[i.idx.z] * i.wgt.z
                  + Bone[i.idx.w] * i.wgt.w;
    float3 p = mul(skin, float4(i.pos, 1.0)).xyz;
    float3x3 skin3 = (float3x3)skin;

    PSIn o;
    o.pos  = mul(WVP, float4(p, 1.0));
    o.nrm  = mul((float3x3)World, mul(skin3, i.nrm));
    o.uv   = i.uv;
    o.color = i.color; o.decalUv = i.decalUv;
    o.wpos = mul(World, float4(p, 1.0)).xyz;
    o.tan  = mul((float3x3)World, mul(skin3, i.tan));
    o.bin  = mul((float3x3)World, mul(skin3, i.bin));
    return o;
}" + ShaderCompiler.MafiaLitPs;

    private ComPtr<ID3D11Buffer> _bones;

    public SkinnedMeshShader(GpuContext gpu)
    {
        using D3DCompiler compiler = D3DCompiler.GetApi();

        ComPtr<ID3D10Blob> vsCode = ShaderCompiler.Compile(compiler, Hlsl, "VSMain", "vs_5_0", "skinnedmesh");
        ComPtr<ID3D10Blob> psCode = ShaderCompiler.Compile(compiler, Hlsl, "PSMain", "ps_5_0", "skinnedmesh");
        (_vs, _ps) = ShaderCompiler.CreateShaders(gpu, vsCode, psCode);

        byte* posName = (byte*)SilkMarshal.StringToPtr("POSITION");
        byte* nrmName = (byte*)SilkMarshal.StringToPtr("NORMAL");
        byte* uvName = (byte*)SilkMarshal.StringToPtr("TEXCOORD");
        byte* tanName = (byte*)SilkMarshal.StringToPtr("TANGENT");
        byte* binName = (byte*)SilkMarshal.StringToPtr("BINORMAL");
        byte* idxName = (byte*)SilkMarshal.StringToPtr("BLENDINDICES");
        byte* wgtName = (byte*)SilkMarshal.StringToPtr("BLENDWEIGHT");
        byte* colorName = (byte*)SilkMarshal.StringToPtr("COLOR");
        var elems = stackalloc InputElementDesc[9];
        elems[0] = ShaderCompiler.VertexElement(posName, 0, Format.FormatR32G32B32Float, 0);
        elems[1] = ShaderCompiler.VertexElement(nrmName, 0, Format.FormatR32G32B32Float, 12);
        elems[2] = ShaderCompiler.VertexElement(uvName, 0, Format.FormatR32G32Float, 24);
        elems[3] = ShaderCompiler.VertexElement(tanName, 0, Format.FormatR32G32B32Float, 32);
        elems[4] = ShaderCompiler.VertexElement(binName, 0, Format.FormatR32G32B32Float, 44);
        // Stream 1: the skin. Kept off the main vertex buffer so an unskinned district pays nothing for it.
        // VertexElement fixes InputSlot at 0 — every other shader here is single-stream — so these two are
        // moved onto slot 1 by hand. Its second parameter is the SEMANTIC index, not the slot.
        elems[5] = ShaderCompiler.VertexElement(idxName, 0, Format.FormatR8G8B8A8Uint, 0);
        elems[5].InputSlot = 1;
        elems[6] = ShaderCompiler.VertexElement(wgtName, 0, Format.FormatR32G32B32A32Float, 4);
        elems[6].InputSlot = 1;
        elems[7] = ShaderCompiler.VertexElement(colorName, 0, Format.FormatR32G32B32A32Float, 0); elems[7].InputSlot = 2;
        elems[8] = ShaderCompiler.VertexElement(uvName, 2, Format.FormatR32G32Float, 0); elems[8].InputSlot = 3;
        ID3D11InputLayout* layout = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateInputLayout(
            elems, 9, vsCode.GetBufferPointer(), vsCode.GetBufferSize(), ref layout));
        _layout = layout;
        SilkMarshal.Free((nint)posName);
        SilkMarshal.Free((nint)nrmName);
        SilkMarshal.Free((nint)uvName);
        SilkMarshal.Free((nint)tanName);
        SilkMarshal.Free((nint)binName);
        SilkMarshal.Free((nint)idxName);
        SilkMarshal.Free((nint)wgtName);
        SilkMarshal.Free((nint)colorName);

        vsCode.Dispose();
        psCode.Dispose();

        _cb = GpuBuffers.CreateConstant<FrameConstants>(gpu);
        _bones = GpuBuffers.CreateConstant<BonePalette>(gpu);
    }

    public void UpdateConstants(ComPtr<ID3D11DeviceContext> ctx, ref FrameConstants c)
        => GpuBuffers.UpdateConstant(ctx, _cb, ref c);

    /// <summary>Uploads the palette and binds it at b1. Call before the mesh's draws.</summary>
    public void UpdateBones(ComPtr<ID3D11DeviceContext> ctx, ref BonePalette palette)
    {
        GpuBuffers.UpdateConstant(ctx, _bones, ref palette);
        var cb = _bones.Handle;
        ctx.VSSetConstantBuffers(1, 1, &cb);
    }

    public new void Dispose()
    {
        _bones.Dispose();
        base.Dispose();
    }
}
