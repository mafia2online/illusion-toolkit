using Illusion.Rendering.Gpu;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

/// <summary>Borrowed native rain inputs. PreviousState is t050, Noise is the caller's t058
/// three-dimensional texture, Sampler is s021 and RainParameter is d698.x. No weather,
/// noise generation, feedback blur or engine texture format is inferred here.</summary>
public readonly record struct NativeRainRippleInputs(ComPtr<ID3D11ShaderResourceView> PreviousState,
    ComPtr<ID3D11ShaderResourceView> Noise, ComPtr<ID3D11SamplerState> Sampler,
    float PreviousTime, float CurrentTime, float RainParameter);

/// <summary>One offscreen native PS6243 evaluation, in float32 analysis targets, or verified native formats when nativePrecision is true. This is not
/// the complete rain feedback simulation or a water-contact ripple viewport binding.
/// Outputs are borrowed until disposal. Render state must be restored by the caller.</summary>
public sealed unsafe class NativeRainRipple : IDisposable
{
    private readonly GpuContext _gpu;
    private readonly int _size;
    private ComPtr<ID3D11VertexShader> _vertex;
    private ComPtr<ID3D11PixelShader> _pixel;
    private ComPtr<ID3D11Buffer> _constants;
    private ComPtr<ID3D11RasterizerState> _raster;
    private readonly ComPtr<ID3D11Texture2D>[] _textures = new ComPtr<ID3D11Texture2D>[2];
    private readonly ComPtr<ID3D11RenderTargetView>[] _targets = new ComPtr<ID3D11RenderTargetView>[2];
    private readonly ComPtr<ID3D11ShaderResourceView>[] _views = new ComPtr<ID3D11ShaderResourceView>[2];
    private bool _disposed;
    public ComPtr<ID3D11ShaderResourceView> HeightVelocity => _disposed ? default : _views[0];
    public ComPtr<ID3D11ShaderResourceView> Normal => _disposed ? default : _views[1];
    public int Size => _size;

    public NativeRainRipple(GpuContext gpu, int size, bool nativePrecision = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 4096);
        _gpu = gpu; _size = size;
        try
        {
            using var compiler = D3DCompiler.GetApi();
            using var vertex = ShaderCompiler.Compile(compiler, "struct O{float4 p:SV_Position;float4 uv:TEXCOORD0;};O main(uint id:SV_VertexID){O o;float2 uv=float2((id<<1)&2,id&2);o.p=float4(uv*float2(2,-2)+float2(-1,1),0,1);o.uv=float4(uv,0,0);return o;}", "main", "vs_5_0", "native-rain");
            ID3D11VertexShader* v = null;
            SilkMarshal.ThrowHResult(gpu.Device11.CreateVertexShader(vertex.GetBufferPointer(), vertex.GetBufferSize(), (ID3D11ClassLinkage*)null, &v)); _vertex = v;
            string source = "cbuffer C:register(b0){float4 F[55];}Texture2D<float4> Previous:register(t0);Texture3D<float4> Noise:register(t1);SamplerState Sampled:register(s0);" + NativeRainRippleShader.Functions + "NativeRainRippleOutput main(float4 p:SV_Position,float4 uv:TEXCOORD0){return EvaluateNativeRainRipple(Previous,Noise,Sampled,uv.xy,F[53].x,F[54].x,F[46].x);}";
            using var pixel = ShaderCompiler.Compile(compiler, source, "main", "ps_5_0", "native-rain");
            ID3D11PixelShader* p = null;
            SilkMarshal.ThrowHResult(gpu.Device11.CreatePixelShader(pixel.GetBufferPointer(), pixel.GetBufferSize(), (ID3D11ClassLinkage*)null, &p)); _pixel = p;
            var bd = new BufferDesc { ByteWidth = 880, Usage = Usage.Default, BindFlags = (uint)BindFlag.ConstantBuffer };
            ID3D11Buffer* b = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateBuffer(in bd, (SubresourceData*)null, &b)); _constants = b;
            var rd = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true };
            ID3D11RasterizerState* r = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rd, &r)); _raster = r;
            for (int i = 0; i < 2; i++)
            {
                var desc = new Texture2DDesc { Width = (uint)size, Height = (uint)size, MipLevels = 1, ArraySize = 1, Format = nativePrecision ? (i == 0 ? Format.FormatR16G16B16A16Float : Format.FormatR8G8B8A8Unorm) : Format.FormatR32G32B32A32Float, SampleDesc = new(1, 0), Usage = Usage.Default, BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource) };
                ID3D11Texture2D* t = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &t)); _textures[i] = t;
                ID3D11RenderTargetView* rt = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRenderTargetView((ID3D11Resource*)t, (RenderTargetViewDesc*)null, &rt)); _targets[i] = rt;
                ID3D11ShaderResourceView* sv = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)t, (ShaderResourceViewDesc*)null, &sv)); _views[i] = sv;
            }
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Rejects invalid inputs before issuing commands. Negative time deltas remain
    /// native behavior; the kernel only caps their upper bound at .05.</summary>
    public bool Evaluate(NativeRainRippleInputs input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.PreviousState.Handle == null || input.Noise.Handle == null || input.Sampler.Handle == null ||
            !float.IsFinite(input.PreviousTime) || !float.IsFinite(input.CurrentTime) || !float.IsFinite(input.RainParameter) || input.RainParameter < 0 ||
            !float.IsFinite(input.CurrentTime - input.PreviousTime)) return false;
        ShaderResourceViewDesc previousView, noiseView;
        input.PreviousState.Handle->GetDesc(&previousView); input.Noise.Handle->GetDesc(&noiseView);
        if (previousView.ViewDimension != D3DSrvDimension.D3D11SrvDimensionTexture2D ||
            noiseView.ViewDimension != D3DSrvDimension.D3D11SrvDimensionTexture3D) return false;
        ID3D11Resource* previous = null; input.PreviousState.Handle->GetResource(&previous);
        try { if (previous == (ID3D11Resource*)_textures[0].Handle || previous == (ID3D11Resource*)_textures[1].Handle) return false; }
        finally { if (previous != null) previous->Release(); }
        var values = stackalloc float[220]; new Span<float>(values, 220).Clear();
        values[184] = input.RainParameter; values[212] = input.PreviousTime; values[216] = input.CurrentTime;
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
        var sampler = input.Sampler.Handle; ctx.PSSetSamplers(0, 1, &sampler);
        var resources = stackalloc ID3D11ShaderResourceView*[2]; resources[0] = input.PreviousState.Handle; resources[1] = input.Noise.Handle;
        ctx.PSSetShaderResources(0, 2, resources); ctx.Draw(3, 0);
        resources[0] = resources[1] = null; ctx.PSSetShaderResources(0, 2, resources);
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
