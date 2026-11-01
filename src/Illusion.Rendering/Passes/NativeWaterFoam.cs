using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

/// <summary>Actual material parameters D252, D253, D254 and D255. No stock defaults are inferred.</summary>
public readonly record struct NativeWaterFoamParameters(Vector3 Breaking, Vector4 Noise, float Amplitude, Vector2 Decay);

/// <summary>Borrowed inputs for one offscreen frame. Resources/sampler remain caller-owned and must
/// survive Step. View/global arrays follow the verified native 33/55 float4 layouts. Depth is linear
/// in the units matched by the supplied native ray constants. Height is the expanded native capture.
/// A null WaveFoam explicitly means zero dynamic ripple foam; BlurSampler is an explicit caller boundary.</summary>
public readonly record struct NativeWaterFoamInputs(ReadOnlyMemory<float> ViewConstants,
    ReadOnlyMemory<float> GlobalConstants, ComPtr<ID3D11ShaderResourceView> Depth,
    ComPtr<ID3D11ShaderResourceView> Height, ComPtr<ID3D11ShaderResourceView> Displacement,
    ComPtr<ID3D11SamplerState> BlurSampler, ComPtr<ID3D11ShaderResourceView> WaveFoam = default);

/// <summary>Native foam generation, 13-tap Gaussian H/V and history equations. This opt-in offscreen
/// module does not paint a water surface or claim native forward shading. Each Step submits exactly
/// one frame; seeking/replay and work budgets belong to the caller. It leaves OM/SRVs unbound; callers
/// restore their render state. Output is borrowed, changes after Step/Reset and dies with this object.</summary>
public sealed unsafe class NativeWaterFoam : IDisposable
{
    public const int Size = 512;
    private readonly GpuContext _gpu;
    private readonly NativeWaterFoamParameters _parameters;
    private ComPtr<ID3D11VertexShader> _vertex;
    private ComPtr<ID3D11PixelShader> _generation, _blur, _history;
    private ComPtr<ID3D11Buffer> _materialBuffer, _viewBuffer, _globalBuffer, _decayBuffer, _blurBuffer;
    private ComPtr<ID3D11RasterizerState> _raster;
    private ComPtr<ID3D11SamplerState> _borderLinear, _borderMipLinear, _wrapLinear, _pointClamp;
    private ComPtr<ID3D11ShaderResourceView> _noise, _zero;
    private Field? _current, _temporary;
    private readonly Field?[] _histories = new Field?[2];
    private readonly float[] _view = new float[132], _global = new float[220], _blurValues = new float[264];
    private Vector2 _previousOrigin;
    private bool _hasOrigin, _disposed;
    private int _read;
    public bool Ready { get; private set; }
    public long Frames { get; private set; }
    public double Time { get; private set; }
    public uint Seed { get; private set; }
    public bool ZeroRippleInput { get; private set; } = true;
    public ComPtr<ID3D11ShaderResourceView> Output => _disposed ? default : _histories[_read]!.Resource;
    public const string Description = "Offscreen native foam equations with explicit caller depth/height, deterministic seeded noise and history; no water-surface foam blending.";

    public NativeWaterFoam(GpuContext gpu, NativeWaterFoamParameters parameters)
    {
        if (!ValidParameters(parameters)) throw new ArgumentException("Finite native foam parameters with positive division thresholds/decay are required.", nameof(parameters));
        _gpu = gpu; _parameters = parameters;
        try
        {
            CreateShaders();
            var rasterDesc = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true };
            ID3D11RasterizerState* raster = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rasterDesc, &raster)); _raster = raster;
            _materialBuffer = ConstantBuffer(12); _viewBuffer = ConstantBuffer(132);
            _globalBuffer = ConstantBuffer(220); _decayBuffer = ConstantBuffer(4); _blurBuffer = ConstantBuffer(264);
            _borderLinear = Sampler(Filter.MinMagLinearMipPoint, TextureAddressMode.Border);
            _borderMipLinear = Sampler(Filter.MinMagMipLinear, TextureAddressMode.Border);
            _wrapLinear = Sampler(Filter.MinMagLinearMipPoint, TextureAddressMode.Wrap);
            _pointClamp = Sampler(Filter.MinMagMipPoint, TextureAddressMode.Clamp);
            _current = new(gpu, Format.FormatR8G8B8A8Unorm); _temporary = new(gpu, Format.FormatR8G8B8A8Unorm);
            _histories[0] = new(gpu, Format.FormatR16Float); _histories[1] = new(gpu, Format.FormatR16Float);
            _zero = ZeroTexture(); Reset(1);
        }
        catch { Dispose(); throw; }
    }

    private static bool ValidParameters(NativeWaterFoamParameters p)
    {
        float[] values = [p.Breaking.X, p.Breaking.Y, p.Breaking.Z, p.Noise.X, p.Noise.Y, p.Noise.Z, p.Noise.W, p.Amplitude, p.Decay.X, p.Decay.Y];
        return values.All(float.IsFinite) && p.Breaking.X > 0 && p.Noise.X > 0 && p.Decay.X > 0 && p.Decay.Y > 0;
    }

    /// <summary>Resets clock/history and regenerates native low-byte CRT-random 32-cube noise with an
    /// explicit seed. This reproduces the procedure, not the live game's prior global CRT state.</summary>
    public void Reset(uint seed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gpu.Context11.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        Unbind(); _noise.Dispose(); _noise = CreateNoise(seed);
        float* clear = stackalloc float[4]; for (int i = 0; i < 4; i++) clear[i] = 0;
        foreach (var field in _histories) _gpu.Context11.ClearRenderTargetView(field!.View.Handle, clear);
        _gpu.Context11.ClearRenderTargetView(_current!.View.Handle, clear);
        _gpu.Context11.ClearRenderTargetView(_temporary!.View.Handle, clear);
        _read = 0; Ready = false; Frames = 0; Time = 0; Seed = seed; _hasOrigin = false; ZeroRippleInput = true;
    }

    /// <summary>Consumes one caller-controlled delta. Overrides only output viewport, simulation
    /// time/delta and current/previous camera-origin uniforms. Refusals leave history and clock intact.</summary>
    public bool Step(float dt, in NativeWaterFoamInputs input, out string? refusal)
    {
        refusal = Preflight(dt, input); if (refusal is not null) return false;
        input.ViewConstants.Span.CopyTo(_view); input.GlobalConstants.Span.CopyTo(_global);
        _view[40] = Size; _view[41] = Size; _view[42] = 1f / Size; _view[43] = 1f / Size;
        var origin = new Vector2(_view[0], _view[1]); var previous = _hasOrigin ? _previousOrigin : origin;
        _global[144] = origin.X; _global[145] = origin.Y; _global[146] = previous.X; _global[147] = previous.Y;
        _global[148] = dt; _global[216] = (float)(Time + dt);
        float[] material = [_parameters.Breaking.X, _parameters.Breaking.Y, _parameters.Breaking.Z, 0,
            _parameters.Noise.X, _parameters.Noise.Y, _parameters.Noise.Z, _parameters.Noise.W, _parameters.Amplitude, 0, 0, 0];
        Upload(_materialBuffer, material); Upload(_viewBuffer, _view); Upload(_globalBuffer, _global);
        Upload(_decayBuffer, [_parameters.Decay.X, _parameters.Decay.Y, 0, 0]);
        var ctx = _gpu.Context11; var viewport = new Viewport(0, 0, Size, Size, 0, 1); ctx.RSSetViewports(1, &viewport); ctx.RSSetState(_raster.Handle);
        ctx.IASetInputLayout((ID3D11InputLayout*)null); ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        ctx.VSSetShader(_vertex.Handle, null, 0); ctx.OMSetDepthStencilState((ID3D11DepthStencilState*)null, 0);
        float* blend = stackalloc float[4]; for (int i = 0; i < 4; i++) blend[i] = 0;
        ctx.OMSetBlendState((ID3D11BlendState*)null, blend, uint.MaxValue);
        var resources = stackalloc ID3D11ShaderResourceView*[5]; var samplers = stackalloc ID3D11SamplerState*[4];
        var buffers = stackalloc ID3D11Buffer*[3]; buffers[0] = _materialBuffer.Handle; buffers[1] = _viewBuffer.Handle; buffers[2] = _globalBuffer.Handle;
        try
        {
            Unbind(); buffers[0] = _materialBuffer.Handle; ctx.PSSetConstantBuffers(0, 3, buffers);
            resources[0] = input.Depth.Handle; resources[1] = input.WaveFoam.Handle == null ? _zero.Handle : input.WaveFoam.Handle;
            resources[2] = input.Displacement.Handle; resources[3] = input.Height.Handle; resources[4] = _noise.Handle;
            samplers[0] = _borderMipLinear.Handle; samplers[1] = _wrapLinear.Handle; samplers[2] = _pointClamp.Handle; samplers[3] = _borderLinear.Handle;
            ctx.PSSetSamplers(0, 4, samplers); ctx.PSSetShaderResources(0, 5, resources); Draw(_current!, _generation);
            Unbind(); buffers[0] = _blurBuffer.Handle; ctx.PSSetConstantBuffers(0, 1, buffers);
            samplers[0] = input.BlurSampler.Handle; ctx.PSSetSamplers(0, 1, samplers);
            BlurWeights(true); resources[0] = _current!.Resource.Handle; ctx.PSSetShaderResources(0, 1, resources); Draw(_temporary!, _blur);
            Unbind(); BlurWeights(false); resources[0] = _temporary!.Resource.Handle; ctx.PSSetShaderResources(0, 1, resources); Draw(_current!, _blur);
            Unbind(); buffers[0] = _decayBuffer.Handle; ctx.PSSetConstantBuffers(0, 3, buffers);
            resources[0] = _current.Resource.Handle; resources[1] = _histories[_read]!.Resource.Handle;
            samplers[0] = _pointClamp.Handle; samplers[1] = _borderLinear.Handle; ctx.PSSetSamplers(0, 2, samplers);
            ctx.PSSetShaderResources(0, 2, resources); Draw(_histories[_read ^ 1]!, _history);
            _read ^= 1; Frames++; Time += dt; _previousOrigin = origin; _hasOrigin = true; Ready = true;
            ZeroRippleInput = input.WaveFoam.Handle == null; return true;
        }
        finally { Unbind(); ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null); }
    }

    private string? Preflight(float dt, in NativeWaterFoamInputs input)
    {
        if (_disposed) return "The foam module is disposed.";
        if (!float.IsFinite(dt) || dt < 0 || !double.IsFinite(Time + dt) || Time + dt > float.MaxValue) return "Finite nonnegative frame delta/time is required.";
        if (input.ViewConstants.Length != 132 || input.GlobalConstants.Length != 220) return "Native view/global arrays must contain 132/220 floats.";
        foreach (float v in input.ViewConstants.Span) if (!float.IsFinite(v)) return "Native view constants must be finite.";
        foreach (float v in input.GlobalConstants.Span) if (!float.IsFinite(v)) return "Native global constants must be finite.";
        if (input.ViewConstants.Span[7] <= 0 || input.ViewConstants.Span[85] <= 0 || input.ViewConstants.Span[86] <= 0 || input.ViewConstants.Span[87] <= 0) return "Positive depth/ray scales are required.";
        if (Math.Max(Math.Log(_parameters.Decay.X), Math.Log(_parameters.Decay.Y)) * dt > Math.Log(65504)) return "Native history decay would overflow R16 float.";
        if (!ValidTexture(input.Depth, 0, Format.FormatR32Float)) return "A single-sample linear R32_FLOAT depth texture is required.";
        if (!ValidTexture(input.Height, 256, Format.FormatR16Float)) return "The native 256-square R16_FLOAT expanded water-height texture is required.";
        if (!ValidTexture(input.Displacement, 256, Format.FormatR32G32B32A32Float)) return "The native 256-square RGBA32_FLOAT displacement field is required.";
        if (input.WaveFoam.Handle != null && !ValidTexture(input.WaveFoam, Size, Format.FormatR8Unorm)) return "Optional ripple foam must be a 512-square R8_UNORM texture.";
        if (input.BlurSampler.Handle == null) return "Provide an explicit caller blur sampler; its descriptor is not inferred.";
        SamplerDesc sampler; input.BlurSampler.Handle->GetDesc(&sampler);
        if (((int)sampler.Filter & 0x80) != 0) return "A non-comparison blur sampler is required.";
        return null;
    }

    private static bool ValidTexture(ComPtr<ID3D11ShaderResourceView> view, int size, Format format)
    {
        if (view.Handle == null) return false;
        ShaderResourceViewDesc vd; view.Handle->GetDesc(&vd);
        if (vd.ViewDimension != D3DSrvDimension.D3D11SrvDimensionTexture2D || vd.Format != format || vd.Texture2D.MostDetailedMip != 0) return false;
        ID3D11Resource* resource = null; view.Handle->GetResource(&resource);
        if (resource == null) return false;
        try
        {
            ResourceDimension dimension; resource->GetType(&dimension); if (dimension != ResourceDimension.Texture2D) return false;
            Texture2DDesc desc; ((ID3D11Texture2D*)resource)->GetDesc(&desc);
            return desc.Width is > 0 and <= 16384 && desc.Height is > 0 and <= 16384 && desc.ArraySize == 1
                && desc.SampleDesc.Count == 1 && (size == 0 || desc.Width == size && desc.Height == size);
        }
        finally { resource->Release(); }
    }

    private void Draw(Field destination, ComPtr<ID3D11PixelShader> shader)
    {
        var target = destination.View.Handle; _gpu.Context11.OMSetRenderTargets(1, &target, (ID3D11DepthStencilView*)null);
        _gpu.Context11.PSSetShader(shader.Handle, null, 0); _gpu.Context11.Draw(3, 0);
        _gpu.Context11.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
    }
    private void Unbind()
    {
        var empty = stackalloc ID3D11ShaderResourceView*[5]; for (int i = 0; i < 5; i++) empty[i] = null;
        _gpu.Context11.PSSetShaderResources(0, 5, empty); _gpu.Context11.VSSetShaderResources(0, 5, empty);
    }
    private void Upload(ComPtr<ID3D11Buffer> buffer, ReadOnlySpan<float> values)
    { fixed (float* data = values) _gpu.Context11.UpdateSubresource((ID3D11Resource*)buffer.Handle, 0, (Box*)null, data, 0, 0); }
    private void BlurWeights(bool horizontal)
    {
        _blurValues.AsSpan().Clear(); _blurValues[0] = 13; float[] weights = new float[13];
        float x = -1, increment = 2f / 12, sigma = .4f, denominator = (sigma + sigma) * sigma, prefactor = 1 / (sigma * 2.5066282749176025f), sum = 0;
        for (int i = 0; i < 13; i++) { weights[i] = MathF.Exp(-(x * x) / denominator) * prefactor; sum += weights[i]; x += increment; }
        for (int i = 0; i < 13; i++) { int at = (i + 2) * 4; _blurValues[at + (horizontal ? 0 : 1)] = (i - 6) / (float)Size; _blurValues[at + 2] = weights[i] / sum; }
        Upload(_blurBuffer, _blurValues);
    }
    private ComPtr<ID3D11Buffer> ConstantBuffer(int floats)
    {
        var desc = new BufferDesc { ByteWidth = (uint)floats * 4, Usage = Usage.Default, BindFlags = (uint)BindFlag.ConstantBuffer };
        ID3D11Buffer* buffer = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateBuffer(in desc, (SubresourceData*)null, &buffer)); return buffer;
    }
    private ComPtr<ID3D11SamplerState> Sampler(Filter filter, TextureAddressMode address)
    {
        var desc = new SamplerDesc { Filter = filter, AddressU = address, AddressV = address, AddressW = address, MaxLOD = float.MaxValue };
        ID3D11SamplerState* state = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateSamplerState(in desc, &state)); return state;
    }
    private void CreateShaders()
    {
        using var compiler = D3DCompiler.GetApi(); using var vs = ShaderCompiler.Compile(compiler, NativeWaterFoamShader.Vertex, "main", "vs_5_0", "native-foam");
        ID3D11VertexShader* vertex = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateVertexShader(vs.GetBufferPointer(), vs.GetBufferSize(), (ID3D11ClassLinkage*)null, &vertex)); _vertex = vertex;
        ComPtr<ID3D11PixelShader> Pixel(string source)
        {
            using var code = ShaderCompiler.Compile(compiler, source, "main", "ps_5_0", "native-foam"); ID3D11PixelShader* pixel = null;
            SilkMarshal.ThrowHResult(_gpu.Device11.CreatePixelShader(code.GetBufferPointer(), code.GetBufferSize(), (ID3D11ClassLinkage*)null, &pixel)); return pixel;
        }
        _generation = Pixel(NativeWaterFoamShader.Generation); _blur = Pixel(NativeWaterFoamShader.Blur); _history = Pixel(NativeWaterFoamShader.History);
    }
    private ComPtr<ID3D11ShaderResourceView> CreateNoise(uint seed)
    {
        var bytes = new byte[32 * 32 * 32]; uint state = seed;
        for (int i = 0; i < bytes.Length; i++) { state = unchecked(state * 214013 + 2531011); bytes[i] = (byte)(state >> 16); }
        fixed (byte* data = bytes)
        {
            var desc = new Texture3DDesc { Width = 32, Height = 32, Depth = 32, MipLevels = 1, Format = Format.FormatR8Unorm, Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource };
            var initial = new SubresourceData { PSysMem = data, SysMemPitch = 32, SysMemSlicePitch = 1024 }; ID3D11Texture3D* texture = null;
            SilkMarshal.ThrowHResult(_gpu.Device11.CreateTexture3D(in desc, in initial, &texture));
            try { ID3D11ShaderResourceView* view = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, &view)); return view; }
            finally { texture->Release(); }
        }
    }
    private ComPtr<ID3D11ShaderResourceView> ZeroTexture()
    {
        byte value = 0; var desc = new Texture2DDesc { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.FormatR8Unorm, SampleDesc = new(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource };
        var initial = new SubresourceData { PSysMem = &value, SysMemPitch = 1 }; ID3D11Texture2D* texture = null;
        SilkMarshal.ThrowHResult(_gpu.Device11.CreateTexture2D(in desc, in initial, &texture));
        try { ID3D11ShaderResourceView* view = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, &view)); return view; }
        finally { texture->Release(); }
    }
    private sealed class Field : IDisposable
    {
        public ComPtr<ID3D11Texture2D> Texture;
        public ComPtr<ID3D11RenderTargetView> View;
        public ComPtr<ID3D11ShaderResourceView> Resource;
        public Field(GpuContext gpu, Format format)
        {
            try
            {
                var desc = new Texture2DDesc { Width = Size, Height = Size, MipLevels = 1, ArraySize = 1, Format = format, SampleDesc = new(1, 0), Usage = Usage.Default, BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource) };
                ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &texture)); Texture = texture;
                ID3D11RenderTargetView* view = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRenderTargetView((ID3D11Resource*)texture, (RenderTargetViewDesc*)null, &view)); View = view;
                ID3D11ShaderResourceView* resource = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, &resource)); Resource = resource;
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { Resource.Dispose(); View.Dispose(); Texture.Dispose(); }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; Ready = false;
        _raster.Dispose(); _vertex.Dispose(); _generation.Dispose(); _blur.Dispose(); _history.Dispose();
        _materialBuffer.Dispose(); _viewBuffer.Dispose(); _globalBuffer.Dispose(); _decayBuffer.Dispose(); _blurBuffer.Dispose();
        _borderLinear.Dispose(); _borderMipLinear.Dispose(); _wrapLinear.Dispose(); _pointClamp.Dispose(); _noise.Dispose(); _zero.Dispose();
        _current?.Dispose(); _temporary?.Dispose(); foreach (var field in _histories) field?.Dispose();
    }
}
