using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

/// <summary>Offscreen unlit visualization of the verified 16-wave analytical field.
/// The caller supplies packed constants and its own native lookup bytes.</summary>
public sealed unsafe class WaterWavePreview : IDisposable
{
    private readonly GpuContext _gpu;
    private ComPtr<ID3D11VertexShader> _vs;
    private ComPtr<ID3D11PixelShader> _ps;
    private ComPtr<ID3D11Buffer> _constants;
    private ComPtr<ID3D11ShaderResourceView> _lookup;
    private ComPtr<ID3D11SamplerState> _sampler;
    private ComPtr<ID3D11RasterizerState> _raster;
    private ComPtr<ID3D11DepthStencilState> _depth;
    private struct Constants { public fixed float Waves[128]; public Vector4 Weights, Domain, Clock; }
    private static readonly string Hlsl = """
        cbuffer Preview : register(b0) { float4 Packed[32]; float4 Weights; float4 Domain; float4 Clock; };
        Texture2D<float2> Lookup : register(t0); SamplerState Wrapped : register(s0);
        struct Varying { float4 Position : SV_POSITION; float2 Uv : TEXCOORD0; };
        Varying VSMain(uint id : SV_VertexID) {
            float2 uv = float2((id << 1) & 2, id & 2); Varying o;
            o.Position = float4(uv * float2(2,-2) + float2(-1,1),0,1); o.Uv = uv; return o;
        }
        """ + AnalyticalWaterShader.Functions + """
        float4 PSMain(Varying i) : SV_TARGET {
            float2 world = Domain.xy + float2(i.Uv.x-.5, .5-i.Uv.y) * Domain.zw;
            AnalyticalWaterField field = EvaluateAnalyticalWater(Lookup, Wrapped, Packed, world, Clock.x, Weights);
            float3 display = Clock.y < .5 ? field.Normal * .5 + .5 : saturate(field.Displacement * Clock.z + .5);
            return float4(display,1);
        }
        """;

    public WaterWavePreview(GpuContext gpu, IReadOnlyList<ReadOnlyMemory<byte>> lookupMips)
    {
        _gpu = gpu;
        if (lookupMips.Count is < 1 or > 7 || lookupMips.Where((m, i) => m.Length != Math.Max(1, 64 >> i) * 2).Any())
            throw new ArgumentException("Supply native 64x1 R8G8 lookup mip data.", nameof(lookupMips));
        try
        {
            using var compiler = D3DCompiler.GetApi();
            using var vs = ShaderCompiler.Compile(compiler, Hlsl, "VSMain", "vs_5_0", "analytical-water-preview");
            using var ps = ShaderCompiler.Compile(compiler, Hlsl, "PSMain", "ps_5_0", "analytical-water-preview");
            (_vs, _ps) = ShaderCompiler.CreateShaders(gpu, vs, ps); _constants = GpuBuffers.CreateConstant<Constants>(gpu);
            var handles = new GCHandle[lookupMips.Count];
            try
            {
                var levels = stackalloc SubresourceData[lookupMips.Count];
                for (int i = 0; i < lookupMips.Count; i++)
                {
                    handles[i] = GCHandle.Alloc(lookupMips[i].ToArray(), GCHandleType.Pinned);
                    levels[i] = new SubresourceData { PSysMem = (void*)handles[i].AddrOfPinnedObject(), SysMemPitch = (uint)Math.Max(1, 64 >> i) * 2 };
                }
                var description = new Texture2DDesc { Width = 64, Height = 1, MipLevels = (uint)lookupMips.Count, ArraySize = 1,
                    Format = Format.FormatR8G8Unorm, SampleDesc = new(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource };
                ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in description, levels, ref texture));
                using ComPtr<ID3D11Texture2D> owned = texture;
                ID3D11ShaderResourceView* view = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, ref view)); _lookup = view;
            }
            finally { foreach (var handle in handles) if (handle.IsAllocated) handle.Free(); }
            var sampler = new SamplerDesc { Filter = Filter.Anisotropic, MaxAnisotropy = 16,
                AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxLOD = float.MaxValue };
            ID3D11SamplerState* s = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in sampler, ref s)); _sampler = s;
            var raster = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 };
            ID3D11RasterizerState* r = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in raster, ref r)); _raster = r;
            var depth = new DepthStencilDesc { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero };
            ID3D11DepthStencilState* d = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in depth, ref d)); _depth = d;
        }
        catch { Dispose(); throw; }
    }

    public void Render(SharedRenderTarget target, ReadOnlySpan<float> packed, float time, Vector4 weights, Vector2 center,
        float span = 128, string mode = "normals", float displacementScale = 1)
    {
        if (packed.Length != 128 || !float.IsFinite(time) || !float.IsFinite(span) || span <= 0 || !float.IsFinite(displacementScale)
            || displacementScale <= 0 || mode is not ("normals" or "displacement") || !float.IsFinite(center.X) || !float.IsFinite(center.Y)
            || !float.IsFinite(weights.X) || !float.IsFinite(weights.Y) || !float.IsFinite(weights.Z) || !float.IsFinite(weights.W))
            throw new ArgumentException("Supply finite analytical wave coordinates, weights and display controls.");
        var constants = new Constants { Weights = weights, Domain = new(center, span * target.Width / target.Height, span),
            Clock = new(time, mode == "normals" ? 0 : 1, displacementScale, 0) };
        for (int i = 0; i < packed.Length; i++) { if (!float.IsFinite(packed[i])) throw new ArgumentException("Non-finite packed wave data.", nameof(packed)); constants.Waves[i] = packed[i]; }
        var ctx = _gpu.Context11; ID3D11RenderTargetView* rtv = target.Rtv.Handle;
        ctx.OMSetRenderTargets(1, &rtv, (ID3D11DepthStencilView*)null); var viewport = new Viewport(0, 0, target.Width, target.Height, 0, 1); ctx.RSSetViewports(1, &viewport);
        ctx.RSSetState(_raster); ctx.OMSetDepthStencilState(_depth, 0); ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, uint.MaxValue);
        ctx.IASetInputLayout((ID3D11InputLayout*)null); ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        ctx.VSSetShader(_vs, (ID3D11ClassInstance**)null, 0); ctx.PSSetShader(_ps, (ID3D11ClassInstance**)null, 0);
        ID3D11Buffer* cb = _constants.Handle; ctx.PSSetConstantBuffers(0, 1, &cb); GpuBuffers.UpdateConstant(ctx, _constants, ref constants);
        ID3D11SamplerState* samplerState = _sampler.Handle; ctx.PSSetSamplers(0, 1, &samplerState);
        ID3D11ShaderResourceView* lookup = _lookup.Handle; ctx.PSSetShaderResources(0, 1, &lookup); ctx.Draw(3, 0);
        lookup = null; ctx.PSSetShaderResources(0, 1, &lookup); ctx.Flush();
    }
    public void Dispose() { _depth.Dispose(); _raster.Dispose(); _sampler.Dispose(); _lookup.Dispose(); _constants.Dispose(); _ps.Dispose(); _vs.Dispose(); }
}
