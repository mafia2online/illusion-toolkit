using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

/// <summary>Borrowed explicit temporal reflection inputs. Constants retain native CB0 float4
/// indexing: current rays1/2/3, previous rays5/6/7 (motion mode), camera4 and previous camera8,
/// prior world-to-clip rows21/22/24 (reprojection mode). MotionOrDepth is native motion XY/Z/W
/// in motion mode and native linear depth X in reprojection mode. No depth conversion is inferred.</summary>
public readonly record struct NativeReflectionTemporalInputs(
    ComPtr<ID3D11ShaderResourceView> CurrentRadiance,
    ComPtr<ID3D11ShaderResourceView> PreviousRadiance,
    ComPtr<ID3D11ShaderResourceView> Roughness,
    ComPtr<ID3D11ShaderResourceView> PreviousConfidence,
    ComPtr<ID3D11ShaderResourceView> MotionOrDepth,
    ComPtr<ID3D11ShaderResourceView> Rejection,
    ComPtr<ID3D11SamplerState> CurrentSampler,
    ComPtr<ID3D11SamplerState> HistorySampler,
    IReadOnlyList<Vector4> Constants);

/// <summary>One offscreen source-owned PS326/PS1403 evaluation into float32 analysis targets or native R11G11B10/R8 targets.
/// Outputs are borrowed. Caller restores render state and supplies radiance/depth/motion/history;
/// this does not enable native scene reflections or infer cubemap/BRDF bindings.</summary>
public sealed unsafe class NativeReflectionTemporal : IDisposable
{
    private readonly GpuContext _gpu;
    private readonly int _size;
    private readonly bool _reproject;
    private ComPtr<ID3D11VertexShader> _vertex;
    private ComPtr<ID3D11PixelShader> _pixel;
    private ComPtr<ID3D11Buffer> _constants;
    private ComPtr<ID3D11RasterizerState> _raster;
    private readonly ComPtr<ID3D11Texture2D>[] _textures = new ComPtr<ID3D11Texture2D>[2];
    private readonly ComPtr<ID3D11RenderTargetView>[] _targets = new ComPtr<ID3D11RenderTargetView>[2];
    private readonly ComPtr<ID3D11ShaderResourceView>[] _views = new ComPtr<ID3D11ShaderResourceView>[2];
    private bool _disposed;
    public ComPtr<ID3D11ShaderResourceView> Radiance => _disposed ? default : _views[0];
    public ComPtr<ID3D11ShaderResourceView> Confidence => _disposed ? default : _views[1];
    public int Size => _size;
    public bool Reproject => _reproject;
    public bool NativePrecision { get; }

    public NativeReflectionTemporal(GpuContext gpu, int size, bool reproject = false, bool nativePrecision = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 4096);
        _gpu = gpu; _size = size; _reproject = reproject; NativePrecision = nativePrecision;
        try
        {
            using var compiler = D3DCompiler.GetApi();
            using var vertex = ShaderCompiler.Compile(compiler, "struct O{float4 p:SV_Position;float4 uv:TEXCOORD0;};O main(uint id:SV_VertexID){O o;float2 uv=float2((id<<1)&2,id&2);o.p=float4(uv*float2(2,-2)+float2(-1,1),0,1);o.uv=float4(uv,0,0);return o;}", "main", "vs_5_0", "native-reflection-temporal");
            ID3D11VertexShader* v = null;
            SilkMarshal.ThrowHResult(gpu.Device11.CreateVertexShader(vertex.GetBufferPointer(), vertex.GetBufferSize(), (ID3D11ClassLinkage*)null, &v)); _vertex = v;
            string source = "cbuffer C:register(b0){float4 F[25];}Texture2D<float4> Current:register(t0);Texture2D<float4> Previous:register(t1);Texture2D<float4> Roughness:register(t2);Texture2D<float4> Confidence:register(t3);Texture2D<float4> MotionOrDepth:register(t4);Texture2D<float4> Rejection:register(t5);SamplerState CurrentSampler:register(s0);SamplerState HistorySampler:register(s1);" + NativeReflectionTemporalShader.Functions +
                (reproject ? "NativeReflectionTemporalOutput main(float4 p:SV_Position,float4 uv:TEXCOORD0){return EvaluateNativeReflectionTemporalReproject(MotionOrDepth,Current,Previous,Roughness,Confidence,Rejection,CurrentSampler,HistorySampler,uv.xy,F[1].xyz,F[2].xyz,F[3].xyz,F[4].xyz,F[8].xyz,F[21],F[22],F[24]);}"
                : "NativeReflectionTemporalOutput main(float4 p:SV_Position,float4 uv:TEXCOORD0){return EvaluateNativeReflectionTemporal(Current,Previous,Roughness,Confidence,MotionOrDepth,Rejection,CurrentSampler,HistorySampler,uv.xy,F[1].xyz,F[2].xyz,F[3].xyz,F[5].xyz,F[6].xyz,F[7].xyz);}");
            using var pixel = ShaderCompiler.Compile(compiler, source, "main", "ps_5_0", "native-reflection-temporal");
            ID3D11PixelShader* p = null;
            SilkMarshal.ThrowHResult(gpu.Device11.CreatePixelShader(pixel.GetBufferPointer(), pixel.GetBufferSize(), (ID3D11ClassLinkage*)null, &p)); _pixel = p;
            var bd = new BufferDesc { ByteWidth = 400, Usage = Usage.Default, BindFlags = (uint)BindFlag.ConstantBuffer };
            ID3D11Buffer* b = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateBuffer(in bd, (SubresourceData*)null, &b)); _constants = b;
            var rd = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true };
            ID3D11RasterizerState* r = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rd, &r)); _raster = r;
            for (int i = 0; i < 2; i++)
            {
                var desc = new Texture2DDesc { Width = (uint)size, Height = (uint)size, MipLevels = 1, ArraySize = 1, Format = nativePrecision ? (i == 0 ? Format.FormatR11G11B10Float : Format.FormatR8Unorm) : Format.FormatR32G32B32A32Float, SampleDesc = new(1, 0), Usage = Usage.Default, BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource) };
                ID3D11Texture2D* t = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &t)); _textures[i] = t;
                ID3D11RenderTargetView* rt = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRenderTargetView((ID3D11Resource*)t, (RenderTargetViewDesc*)null, &rt)); _targets[i] = rt;
                ID3D11ShaderResourceView* sv = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)t, (ShaderResourceViewDesc*)null, &sv)); _views[i] = sv;
            }
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Validates all borrowed inputs before issuing commands. This evaluates one frame;
    /// history lifetime, camera-cut policy and radiance provenance remain explicit caller inputs.</summary>
    public bool Evaluate(NativeReflectionTemporalInputs input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.CurrentSampler.Handle == null || input.HistorySampler.Handle == null ||
            input.Constants == null || input.Constants.Count != 25) return false;
        var views = new[] { input.CurrentRadiance, input.PreviousRadiance, input.Roughness,
            input.PreviousConfidence, input.MotionOrDepth, input.Rejection };
        foreach (var view in views)
        {
            if (view.Handle == null) return false;
            ShaderResourceViewDesc vd; view.Handle->GetDesc(&vd);
            if (vd.ViewDimension != D3DSrvDimension.D3D11SrvDimensionTexture2D) return false;
            ID3D11Resource* resource = null; view.Handle->GetResource(&resource);
            try { if (resource == (ID3D11Resource*)_textures[0].Handle || resource == (ID3D11Resource*)_textures[1].Handle) return false; }
            finally { if (resource != null) resource->Release(); }
        }
        var values = stackalloc float[100];
        for (int i = 0; i < 25; i++)
        {
            var value = input.Constants[i];
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W)) return false;
            values[i * 4] = value.X; values[i * 4 + 1] = value.Y;
            values[i * 4 + 2] = value.Z; values[i * 4 + 3] = value.W;
        }
        var ctx = _gpu.Context11;
        ctx.UpdateSubresource((ID3D11Resource*)_constants.Handle, 0, (Box*)null, values, 0, 0);
        var targets = stackalloc ID3D11RenderTargetView*[2]; targets[0] = _targets[0].Handle; targets[1] = _targets[1].Handle;
        ctx.OMSetRenderTargets(2, targets, (ID3D11DepthStencilView*)null);
        var viewport = new Viewport(0, 0, _size, _size, 0, 1); ctx.RSSetViewports(1, &viewport); ctx.RSSetState(_raster.Handle);
        ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, uint.MaxValue);
        ctx.OMSetDepthStencilState((ID3D11DepthStencilState*)null, 0);
        ctx.IASetInputLayout((ID3D11InputLayout*)null); ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        ctx.VSSetShader(_vertex.Handle, null, 0); ctx.PSSetShader(_pixel.Handle, null, 0);
        var buffer = _constants.Handle; ctx.PSSetConstantBuffers(0, 1, &buffer);
        var samplers = stackalloc ID3D11SamplerState*[2];
        samplers[0] = input.CurrentSampler.Handle; samplers[1] = input.HistorySampler.Handle;
        ctx.PSSetSamplers(0, 2, samplers);
        var resources = stackalloc ID3D11ShaderResourceView*[6];
        for (int i = 0; i < 6; i++) resources[i] = views[i].Handle;
        ctx.PSSetShaderResources(0, 6, resources); ctx.Draw(3, 0);
        for (int i = 0; i < 6; i++) resources[i] = null;
        ctx.PSSetShaderResources(0, 6, resources);
        ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var view in _views) view.Dispose(); foreach (var target in _targets) target.Dispose(); foreach (var texture in _textures) texture.Dispose();
        _constants.Dispose(); _raster.Dispose(); _pixel.Dispose(); _vertex.Dispose();
    }
}
