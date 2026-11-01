using System.Numerics;
using Illusion.Rendering.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

/// <summary>Bounded explicit sparse-stamp preview. Capture and wind are deliberately zero;
/// this is an independent preview clock, not actor physics or joint native foam replay.</summary>
public sealed unsafe class NativeWaterContactPreview : IDisposable
{
    private readonly NativeWaterContact[] _passes;
    private readonly ComPtr<ID3D11ShaderResourceView> _stamp, _zero;
    private ComPtr<ID3D11SamplerState> _windSampler, _fieldSampler;
    private bool _disposed;
    public Vector2 Origin { get; }
    public int Frame { get; private set; }
    public int TargetFrame { get; private set; }
    public bool Ready => !_disposed && Frame == TargetFrame;
    public bool ZeroHeightCapture => true;
    public bool ZeroWind => true;
    public string? Error { get; private set; }
    public ComPtr<ID3D11ShaderResourceView> State => !_disposed && Frame > 0 ? _passes[(Frame - 1) & 1].State : default;
    public ComPtr<ID3D11ShaderResourceView> Foam => !_disposed && Frame > 0 ? _passes[(Frame - 1) & 1].Foam : default;
    public NativeWaterContactPreview(GpuContext gpu, Vector2 origin, IReadOnlyList<int> texels, IReadOnlyList<float> influences, int frame)
    {
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || frame is < 0 or > 36000) throw new ArgumentException("Finite explicit contact origin and frame 0–36000 are required.");
        byte[] stamp = NativeWaterStampQuantizer.BuildSparse(texels, influences);
        Origin = origin; TargetFrame = frame; _passes = new NativeWaterContact[2];
        try
        {
            _passes[0] = new(gpu, 512, nativePrecision: true); _passes[1] = new(gpu, 512, nativePrecision: true);
            var desc = new Texture2DDesc { Width = 512, Height = 512, ArraySize = 1, MipLevels = 1, Format = Format.FormatR8Unorm, SampleDesc = new(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource };
            fixed (byte* bytes = stamp) { var initial = new SubresourceData { PSysMem = bytes, SysMemPitch = 512 }; _stamp = Texture(gpu, desc, initial); }
            Vector4 zero = default; desc.Width = desc.Height = 1; desc.Format = Format.FormatR32G32B32A32Float;
            var empty = new SubresourceData { PSysMem = &zero, SysMemPitch = 16 }; _zero = Texture(gpu, desc, empty);
            _windSampler = Sampler(gpu, Filter.MinMagLinearMipPoint, TextureAddressMode.Wrap);
            _fieldSampler = Sampler(gpu, Filter.MinMagMipPoint, TextureAddressMode.Border);
        }
        catch { Dispose(); throw; }
    }
    public bool Seek(int frame, bool reset)
    {
        if (_disposed || frame is < 0 or > 36000) return false;
        if (reset || frame < Frame) Frame = 0;
        TargetFrame = frame; Error = null; return true;
    }
    public int Advance(int maxSteps = 4)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (maxSteps is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(maxSteps));
        int count = 0;
        while (Frame < TargetFrame && count < maxSteps)
        {
            int write = Frame & 1;
            var state = Frame == 0 ? _zero : _passes[write ^ 1].State;
            var foam = Frame == 0 ? _zero : _passes[write ^ 1].Foam;
            var input = new NativeWaterContactInputs(state, foam, _zero, Frame == 0 ? _stamp : _zero,
                _zero, _windSampler, _fieldSampler, new(Origin.X, Origin.Y, Origin.X, Origin.Y), Origin, Vector2.Zero, 0, 1f / 60);
            if (!_passes[write].Evaluate(input)) { Error = "Native contact preview input was refused."; break; }
            Frame++; count++;
        }
        return count;
    }
    private static ComPtr<ID3D11ShaderResourceView> Texture(GpuContext gpu, Texture2DDesc desc, SubresourceData initial)
    {
        ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, in initial, &texture));
        try { ID3D11ShaderResourceView* view = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, &view)); return view; }
        finally { texture->Release(); }
    }
    private static ComPtr<ID3D11SamplerState> Sampler(GpuContext gpu, Filter filter, TextureAddressMode address)
    {
        var desc = new SamplerDesc { Filter = filter, AddressU = address, AddressV = address, AddressW = address, MaxLOD = float.MaxValue };
        ID3D11SamplerState* sampler = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in desc, &sampler)); return sampler;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var pass in _passes) pass?.Dispose(); _stamp.Dispose(); _zero.Dispose(); _windSampler.Dispose(); _fieldSampler.Dispose();
    }
}
