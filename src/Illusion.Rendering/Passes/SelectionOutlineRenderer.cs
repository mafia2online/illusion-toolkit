using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

[StructLayout(LayoutKind.Sequential)]
internal struct OutlineMaskConstants
{
    public Matrix4x4 Wvp; // load as-is (HLSL mul(M,v) compensates for row-major)
}

[StructLayout(LayoutKind.Sequential)]
internal struct OutlineConstants
{
    public Vector4 Color; // rgb + alpha
    public Vector4 Texel; // x,y = 1/width, 1/height; z = outline half-width in pixels
}

/// <summary>
/// Blender-style selection highlight: a screen-space silhouette outline of the selected mesh's exact geometry
/// (not its bounding box). Two passes — (1) rasterize the mesh into an offscreen R8 mask (its 2D silhouette,
/// depth-less so the whole shape is captured even when occluded); (2) a fullscreen dilation pass paints a
/// constant-width contour where a background pixel is within the outline radius of the mask, leaving the
/// interior untouched. Alpha-blended on top of the scene, so it reads exactly like Blender's orange contour.
/// The mask target tracks the viewport size and is recreated on resize.
/// </summary>
public sealed unsafe class SelectionOutlineRenderer : IDisposable
{
    // Orange contour, ~2.5 px wide — the interior is never tinted (that's the whole point vs. an AABB box).
    private static readonly Vector4 OutlineColor = new(1.0f, 0.60f, 0.15f, 1.0f);
    private const float OutlineRadiusPx = 2.5f;

    private const string MaskHlsl = @"
cbuffer CB : register(b0) { float4x4 WVP; };
struct VSIn { float3 pos : POSITION; };
struct PSIn { float4 pos : SV_POSITION; };
PSIn VSMain(VSIn i){ PSIn o; o.pos = mul(WVP, float4(i.pos, 1.0)); return o; }
float4 PSMain(PSIn i) : SV_TARGET { return float4(1.0, 0.0, 0.0, 1.0); }"; // R = coverage

    private const string OutlineHlsl = @"
Texture2D    Mask : register(t0);
SamplerState Samp : register(s0);
cbuffer CB : register(b0) { float4 Color; float4 Texel; }; // Texel.xy = 1/size, Texel.z = radius (px)
struct VOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VOut VSMain(uint id : SV_VertexID)
{
    float2 t = float2((id << 1) & 2, id & 2); // (0,0),(2,0),(0,2) — oversized fullscreen triangle
    VOut o;
    o.pos = float4(t.x * 2.0 - 1.0, 1.0 - t.y * 2.0, 0.0, 1.0);
    o.uv  = t;
    return o;
}
float4 PSMain(VOut i) : SV_TARGET
{
    float2 px  = Texel.xy;
    float  r   = Texel.z;
    float  center = Mask.SampleLevel(Samp, i.uv, 0).r;   // ~1 inside the silhouette, ~0 outside
    // Ring of taps: a background pixel within r of the silhouette catches an inside tap → it's on the contour.
    float ring = 0.0;
    [unroll] for (int k = 0; k < 16; k++)
    {
        float ang = 6.28318530718 * (k / 16.0);
        float2 dir = float2(cos(ang), sin(ang));
        ring = max(ring, Mask.SampleLevel(Samp, i.uv + dir * px * r, 0).r);
    }
    float a = saturate(ring) * (1.0 - center);           // outside pixels adjacent to the silhouette; interior = 0
    if (a < 0.02) discard;
    return float4(Color.rgb, a * Color.a);
}";

    private readonly GpuContext _gpu;

    private ComPtr<ID3D11VertexShader> _maskVs;
    private ComPtr<ID3D11PixelShader> _maskPs;
    private ComPtr<ID3D11InputLayout> _maskLayout;
    private ComPtr<ID3D11Buffer> _maskCb;

    private ComPtr<ID3D11VertexShader> _outlineVs;
    private ComPtr<ID3D11PixelShader> _outlinePs;
    private ComPtr<ID3D11Buffer> _outlineCb;

    private ComPtr<ID3D11SamplerState> _sampler;
    private ComPtr<ID3D11BlendState> _blend;      // alpha over (contour on top of the scene)
    private ComPtr<ID3D11DepthStencilState> _noDepth;
    private ComPtr<ID3D11RasterizerState> _raster;

    // Offscreen silhouette mask (R8), tracks the viewport size.
    private ComPtr<ID3D11Texture2D> _maskTex;
    private ComPtr<ID3D11RenderTargetView> _maskRtv;
    private ComPtr<ID3D11ShaderResourceView> _maskSrv;
    private int _maskW, _maskH;

    public SelectionOutlineRenderer(GpuContext gpu)
    {
        _gpu = gpu;
        using D3DCompiler compiler = D3DCompiler.GetApi();

        ComPtr<ID3D10Blob> maskVsCode = ShaderCompiler.Compile(compiler, MaskHlsl, "VSMain", "vs_5_0", "outline-mask");
        ComPtr<ID3D10Blob> maskPsCode = ShaderCompiler.Compile(compiler, MaskHlsl, "PSMain", "ps_5_0", "outline-mask");
        (_maskVs, _maskPs) = ShaderCompiler.CreateShaders(gpu, maskVsCode, maskPsCode);

        // Mask VS reads only POSITION from the shared MeshVertex buffer (stride = sizeof(MeshVertex)).
        byte* posName = (byte*)SilkMarshal.StringToPtr("POSITION");
        InputElementDesc elem = ShaderCompiler.VertexElement(posName, 0, Format.FormatR32G32B32Float, 0);
        ID3D11InputLayout* layout = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateInputLayout(
            &elem, 1, maskVsCode.GetBufferPointer(), maskVsCode.GetBufferSize(), ref layout));
        _maskLayout = layout;
        SilkMarshal.Free((nint)posName);
        maskVsCode.Dispose();
        maskPsCode.Dispose();

        ComPtr<ID3D10Blob> outVsCode = ShaderCompiler.Compile(compiler, OutlineHlsl, "VSMain", "vs_5_0", "outline");
        ComPtr<ID3D10Blob> outPsCode = ShaderCompiler.Compile(compiler, OutlineHlsl, "PSMain", "ps_5_0", "outline");
        (_outlineVs, _outlinePs) = ShaderCompiler.CreateShaders(gpu, outVsCode, outPsCode);
        outVsCode.Dispose();
        outPsCode.Dispose();

        _maskCb = GpuBuffers.CreateConstant<OutlineMaskConstants>(gpu);
        _outlineCb = GpuBuffers.CreateConstant<OutlineConstants>(gpu);

        // Clamp so ring taps near the border don't wrap the silhouette across screen edges.
        var sampDesc = new SamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunc.Never,
            MaxLOD = float.MaxValue,
        };
        ID3D11SamplerState* samp = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in sampDesc, ref samp));
        _sampler = samp;

        var bd = new BlendDesc();
        bd.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = Blend.SrcAlpha,
            DestBlend = Blend.InvSrcAlpha,
            BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One,
            DestBlendAlpha = Blend.Zero,
            BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ID3D11BlendState* blend = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateBlendState(in bd, ref blend));
        _blend = blend;

        var dsd = new DepthStencilDesc { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.Always };
        ID3D11DepthStencilState* nd = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in dsd, ref nd));
        _noDepth = nd;

        var rsd = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 };
        ID3D11RasterizerState* rs = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rsd, ref rs));
        _raster = rs;
    }

    // (Re)creates the R8 silhouette mask to match the current viewport size.
    private void EnsureMask(int width, int height)
    {
        if (_maskTex.Handle != null && _maskW == width && _maskH == height) return;
        DisposeMask();

        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatR8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource),
        };
        ID3D11Texture2D* tex = null;
        SilkMarshal.ThrowHResult(_gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, ref tex));
        _maskTex = tex;

        ID3D11RenderTargetView* rtv = null;
        SilkMarshal.ThrowHResult(_gpu.Device11.CreateRenderTargetView(
            (ID3D11Resource*)_maskTex.Handle, (RenderTargetViewDesc*)null, &rtv));
        _maskRtv = rtv;

        ID3D11ShaderResourceView* srv = null;
        SilkMarshal.ThrowHResult(_gpu.Device11.CreateShaderResourceView(
            (ID3D11Resource*)_maskTex.Handle, (ShaderResourceViewDesc*)null, &srv));
        _maskSrv = srv;

        _maskW = width;
        _maskH = height;
    }

    /// <summary>
    /// Draws the silhouette contour of <paramref name="meshes"/> onto <paramref name="target"/>. No-op when the
    /// selection is empty. Rebinds the target RTV itself, restores opaque blending, and leaves the mask SRV
    /// unbound — the caller only has to re-set its own rasterizer/depth state for the next frame.
    /// </summary>
    /// <param name="placements">Selected copies of an instanced prototype: the same geometry drawn at one copy's
    /// matrix. An instanced mesh has no World of its own, so a crash placement can only be outlined this way.</param>
    public void Render(SharedRenderTarget target, Matrix4x4 viewProj, IReadOnlyList<GpuMesh> meshes,
        IReadOnlyList<(GpuMesh Mesh, Matrix4x4 World)>? placements = null)
    {
        // Only outline VISIBLE geometry: the mesh pass skips hidden meshes, so a hidden selection must not
        // leave a contour floating around empty space. Checked at draw time (not just on select) because the
        // eye toggle can hide/show the selected mesh after it was selected — the outline follows automatically.
        bool any = false;
        foreach (GpuMesh m in meshes)
            if (m.RenderVisible && m.VertexBuffer.Handle != null && m.IndexBuffer.Handle != null) { any = true; break; }
        if (!any && placements != null)
        {
            foreach ((GpuMesh m, _) in placements)
                if (m.RenderVisible && m.VertexBuffer.Handle != null && m.IndexBuffer.Handle != null) { any = true; break; }
        }
        if (!any) return;

        var ctx = _gpu.Context11;
        EnsureMask(target.Width, target.Height);

        var vp = new Viewport(0, 0, target.Width, target.Height, 0, 1);
        ctx.RSSetViewports(1, &vp);

        // ── Pass 1: rasterize the silhouette into the mask (opaque, depth-less, both faces). ──
        var maskRtv = _maskRtv.Handle;
        ctx.OMSetRenderTargets(1, &maskRtv, (ID3D11DepthStencilView*)null);
        var clear = stackalloc float[4] { 0f, 0f, 0f, 0f };
        ctx.ClearRenderTargetView((ID3D11RenderTargetView*)_maskRtv.Handle, clear);

        ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, 0xffffffff);
        ctx.OMSetDepthStencilState(_noDepth, 0);
        ctx.RSSetState(_raster);
        ctx.IASetInputLayout(_maskLayout);
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        ctx.VSSetShader(_maskVs, (ID3D11ClassInstance**)null, 0);
        ctx.PSSetShader(_maskPs, (ID3D11ClassInstance**)null, 0);
        var maskCb = _maskCb.Handle;
        ctx.VSSetConstantBuffers(0, 1, &maskCb);

        uint stride = (uint)sizeof(MeshVertex);
        uint offset = 0;
        foreach (GpuMesh mesh in meshes)
        {
            if (!mesh.RenderVisible || mesh.VertexBuffer.Handle == null || mesh.IndexBuffer.Handle == null) continue;
            var consts = new OutlineMaskConstants { Wvp = mesh.World * viewProj };
            GpuBuffers.UpdateConstant(ctx, _maskCb, ref consts);

            var vb = mesh.VertexBuffer.Handle;
            ctx.IASetVertexBuffers(0, 1, &vb, &stride, &offset);
            ctx.IASetIndexBuffer(mesh.IndexBuffer, Format.FormatR32Uint, 0);
            ctx.DrawIndexed((uint)(mesh.TriangleCount * 3), 0, 0);
        }

        foreach ((GpuMesh mesh, Matrix4x4 world) in placements ?? [])
        {
            if (!mesh.RenderVisible || mesh.VertexBuffer.Handle == null || mesh.IndexBuffer.Handle == null) continue;
            var consts = new OutlineMaskConstants { Wvp = world * viewProj };
            GpuBuffers.UpdateConstant(ctx, _maskCb, ref consts);

            var vb = mesh.VertexBuffer.Handle;
            ctx.IASetVertexBuffers(0, 1, &vb, &stride, &offset);
            ctx.IASetIndexBuffer(mesh.IndexBuffer, Format.FormatR32Uint, 0);
            ctx.DrawIndexed((uint)(mesh.TriangleCount * 3), 0, 0);
        }

        // ── Pass 2: dilation contour over the scene target (alpha blend). ──
        var sceneRtv = target.Rtv.Handle;
        ctx.OMSetRenderTargets(1, &sceneRtv, (ID3D11DepthStencilView*)null);

        var oc = new OutlineConstants
        {
            Color = OutlineColor,
            Texel = new Vector4(1f / target.Width, 1f / target.Height, OutlineRadiusPx, 0f),
        };
        GpuBuffers.UpdateConstant(ctx, _outlineCb, ref oc);

        ctx.OMSetBlendState(_blend, (float*)null, 0xffffffff);
        ctx.IASetInputLayout(default(ComPtr<ID3D11InputLayout>));
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        ctx.VSSetShader(_outlineVs, (ID3D11ClassInstance**)null, 0);
        ctx.PSSetShader(_outlinePs, (ID3D11ClassInstance**)null, 0);
        var outCb = _outlineCb.Handle;
        ctx.PSSetConstantBuffers(0, 1, &outCb);
        var samp = _sampler.Handle;
        ctx.PSSetSamplers(0, 1, &samp);
        var srv = _maskSrv.Handle;
        ctx.PSSetShaderResources(0, 1, &srv);
        ctx.Draw(3, 0);

        // Unbind the mask SRV so next frame can bind it as an RTV again; restore opaque blending.
        ID3D11ShaderResourceView* nullSrv = null;
        ctx.PSSetShaderResources(0, 1, &nullSrv);
        ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, 0xffffffff);
    }

    private void DisposeMask()
    {
        _maskSrv.Dispose();
        _maskRtv.Dispose();
        _maskTex.Dispose();
        _maskSrv = default;
        _maskRtv = default;
        _maskTex = default;
        _maskW = _maskH = 0;
    }

    public void Dispose()
    {
        DisposeMask();
        _raster.Dispose();
        _noDepth.Dispose();
        _blend.Dispose();
        _sampler.Dispose();
        _outlineCb.Dispose();
        _maskCb.Dispose();
        _outlinePs.Dispose();
        _outlineVs.Dispose();
        _maskLayout.Dispose();
        _maskPs.Dispose();
        _maskVs.Dispose();
    }
}
