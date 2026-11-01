using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

/// <summary>Borrowed native PS1754 inputs. The five textures are t050/t051/t052/t256/t247;
/// samplers are s021/s028. Origins is d272 (currentXY, previousXY), CaptureCenter is
/// d273.zw, WindOrigin is d217.xy, WindStrength is d119.w and DeltaTime is d274.x.
/// Capture/stamps/wind and feedback evolution must be supplied by the caller.</summary>
public readonly record struct NativeWaterContactInputs(ComPtr<ID3D11ShaderResourceView> PreviousState,
    ComPtr<ID3D11ShaderResourceView> PreviousFoam, ComPtr<ID3D11ShaderResourceView> HeightCapture,
    ComPtr<ID3D11ShaderResourceView> Stamp, ComPtr<ID3D11ShaderResourceView> Wind,
    ComPtr<ID3D11SamplerState> WrappedSampler, ComPtr<ID3D11SamplerState> FieldSampler,
    Vector4 Origins, Vector2 CaptureCenter, Vector2 WindOrigin, float WindStrength, float DeltaTime);

/// <summary>One offscreen native water-contact field evaluation in float32 analysis targets, or verified native formats when nativePrecision is true.
/// It does not generate actor stamps or establish engine target quantization. Output resources
/// are borrowed until disposal; render state must be restored by the caller.</summary>
public sealed unsafe class NativeWaterContact : IDisposable
{
    private readonly GpuContext _gpu;
    private readonly int _size;
    private readonly bool _nativePrecision;
    private ComPtr<ID3D11VertexShader> _vertex;
    private ComPtr<ID3D11PixelShader> _pixel;
    private ComPtr<ID3D11Buffer> _constants, _viewConstants;
    private ComPtr<ID3D11RasterizerState> _raster;
    private readonly ComPtr<ID3D11Texture2D>[] _textures = new ComPtr<ID3D11Texture2D>[2];
    private readonly ComPtr<ID3D11RenderTargetView>[] _targets = new ComPtr<ID3D11RenderTargetView>[2];
    private readonly ComPtr<ID3D11ShaderResourceView>[] _views = new ComPtr<ID3D11ShaderResourceView>[2];
    private bool _disposed;
    public ComPtr<ID3D11ShaderResourceView> State => _disposed ? default : _views[0];
    public ComPtr<ID3D11ShaderResourceView> Foam => _disposed ? default : _views[1];
    public int Size => _size;

    public NativeWaterContact(GpuContext gpu, int size, bool nativePrecision = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 4096);
        _gpu = gpu; _size = size; _nativePrecision = nativePrecision;
        try
        {
            using var compiler = D3DCompiler.GetApi();
            using var vertex = ShaderCompiler.Compile(compiler, "struct O{float4 p:SV_Position;float4 uv:TEXCOORD0;};O main(uint id:SV_VertexID){O o;float2 uv=float2((id<<1)&2,id&2);o.p=float4(uv*float2(2,-2)+float2(-1,1),0,1);o.uv=float4(uv,0,0);return o;}", "main", "vs_5_0", "native-water-contact");
            ID3D11VertexShader* v = null;
            SilkMarshal.ThrowHResult(gpu.Device11.CreateVertexShader(vertex.GetBufferPointer(), vertex.GetBufferSize(), (ID3D11ClassLinkage*)null, &v)); _vertex = v;
            string source = "cbuffer A:register(b0){float4 A[11];}cbuffer B:register(b1){float4 B[38];}Texture2D<float4> Previous:register(t0);Texture2D<float4> Foam:register(t1);Texture2D<float4> Capture:register(t2);Texture2D<float4> Stamp:register(t3);Texture2D<float4> Wind:register(t4);SamplerState Wrapped:register(s0);SamplerState Bordered:register(s1);" + NativeWaterContactShader.Functions + "NativeWaterContactOutput main(float4 p:SV_Position){float2 uv=mad(p.xy*A[10].zw,A[8].xy,A[8].zw);return EvaluateNativeWaterContact(Previous,Foam,Capture,Stamp,Wind,Wrapped,Bordered,uv,B[35],B[36].zw,B[33].xy,B[13].w,B[37].x);}";
            using var pixel = ShaderCompiler.Compile(compiler, source, "main", "ps_5_0", "native-water-contact");
            ID3D11PixelShader* p = null;
            SilkMarshal.ThrowHResult(gpu.Device11.CreatePixelShader(pixel.GetBufferPointer(), pixel.GetBufferSize(), (ID3D11ClassLinkage*)null, &p)); _pixel = p;
            var bd = new BufferDesc { ByteWidth = 176, Usage = Usage.Default, BindFlags = (uint)BindFlag.ConstantBuffer };
            ID3D11Buffer* b = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateBuffer(in bd, (SubresourceData*)null, &b)); _constants = b;
            bd.ByteWidth = 608; b = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateBuffer(in bd, (SubresourceData*)null, &b)); _viewConstants = b;
            var rd = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true };
            ID3D11RasterizerState* r = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rd, &r)); _raster = r;
            for (int i = 0; i < 2; i++)
            {
                var desc = new Texture2DDesc { Width = (uint)size, Height = (uint)size, MipLevels = 1, ArraySize = 1, Format = nativePrecision ? (i == 0 ? Format.FormatR16G16B16A16Float : Format.FormatR8Unorm) : Format.FormatR32G32B32A32Float, SampleDesc = new(1, 0), Usage = Usage.Default, BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource) };
                if (nativePrecision && i == 0) { desc.MipLevels = 0; desc.MiscFlags = 1; } // D3D11_RESOURCE_MISC_GENERATE_MIPS
                ID3D11Texture2D* t = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &t)); _textures[i] = t;
                ID3D11RenderTargetView* rt = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRenderTargetView((ID3D11Resource*)t, (RenderTargetViewDesc*)null, &rt)); _targets[i] = rt;
                ID3D11ShaderResourceView* sv = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)t, (ShaderResourceViewDesc*)null, &sv)); _views[i] = sv;
            }
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Evaluates one native step. Zero/negative DeltaTime preserves sampled state,
    /// exactly as the original shader; this method never manufactures a contact stamp.</summary>
    public bool Evaluate(NativeWaterContactInputs input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var borrowed = new[] { input.PreviousState, input.PreviousFoam, input.HeightCapture, input.Stamp, input.Wind };
        float[] valuesToCheck = [input.Origins.X, input.Origins.Y, input.Origins.Z, input.Origins.W,
            input.CaptureCenter.X, input.CaptureCenter.Y, input.WindOrigin.X, input.WindOrigin.Y,
            input.WindStrength, input.DeltaTime];
        if (input.WrappedSampler.Handle == null || input.FieldSampler.Handle == null || !valuesToCheck.All(float.IsFinite)) return false;
        foreach (var view in borrowed)
        {
            if (view.Handle == null) return false;
            ShaderResourceViewDesc desc; view.Handle->GetDesc(&desc);
            if (desc.ViewDimension != D3DSrvDimension.D3D11SrvDimensionTexture2D) return false;
            ID3D11Resource* resource = null; view.Handle->GetResource(&resource);
            try { if (resource == (ID3D11Resource*)_textures[0].Handle || resource == (ID3D11Resource*)_textures[1].Handle) return false; }
            finally { if (resource != null) resource->Release(); }
        }
        var screen = stackalloc float[44]; new Span<float>(screen, 44).Clear();
        screen[32] = screen[33] = 1; screen[42] = screen[43] = 1f / _size;
        var values = stackalloc float[152]; new Span<float>(values, 152).Clear();
        values[140] = input.Origins.X; values[141] = input.Origins.Y; values[142] = input.Origins.Z; values[143] = input.Origins.W;
        values[146] = input.CaptureCenter.X; values[147] = input.CaptureCenter.Y;
        values[132] = input.WindOrigin.X; values[133] = input.WindOrigin.Y;
        values[55] = input.WindStrength; values[148] = input.DeltaTime;
        var ctx = _gpu.Context11;
        ctx.UpdateSubresource((ID3D11Resource*)_constants.Handle, 0, (Box*)null, screen, 0, 0);
        ctx.UpdateSubresource((ID3D11Resource*)_viewConstants.Handle, 0, (Box*)null, values, 0, 0);
        var targets = stackalloc ID3D11RenderTargetView*[2]; targets[0] = _targets[0].Handle; targets[1] = _targets[1].Handle;
        ctx.OMSetRenderTargets(2, targets, (ID3D11DepthStencilView*)null);
        var viewport = new Viewport(0, 0, _size, _size, 0, 1); ctx.RSSetViewports(1, &viewport); ctx.RSSetState(_raster.Handle);
        ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, uint.MaxValue);
        ctx.OMSetDepthStencilState((ID3D11DepthStencilState*)null, 0);
        ctx.IASetInputLayout((ID3D11InputLayout*)null); ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        ctx.VSSetShader(_vertex.Handle, null, 0); ctx.PSSetShader(_pixel.Handle, null, 0);
        var buffers = stackalloc ID3D11Buffer*[2]; buffers[0] = _constants.Handle; buffers[1] = _viewConstants.Handle;
        ctx.PSSetConstantBuffers(0, 2, buffers);
        var samplers = stackalloc ID3D11SamplerState*[2]; samplers[0] = input.WrappedSampler.Handle; samplers[1] = input.FieldSampler.Handle;
        ctx.PSSetSamplers(0, 2, samplers);
        var resources = stackalloc ID3D11ShaderResourceView*[5];
        for (int i = 0; i < 5; i++) resources[i] = borrowed[i].Handle;
        ctx.PSSetShaderResources(0, 5, resources); ctx.Draw(3, 0);
        for (int i = 0; i < 5; i++) resources[i] = null;
        ctx.PSSetShaderResources(0, 5, resources);
        ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        if (_nativePrecision) ctx.GenerateMips(_views[0].Handle);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var view in _views) view.Dispose(); foreach (var target in _targets) target.Dispose(); foreach (var texture in _textures) texture.Dispose();
        _viewConstants.Dispose(); _constants.Dispose(); _raster.Dispose(); _pixel.Dispose(); _vertex.Dispose();
    }
}
