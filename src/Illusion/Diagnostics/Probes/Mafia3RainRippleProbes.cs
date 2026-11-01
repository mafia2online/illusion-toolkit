using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestNativeRainRipple(Action<string, bool> check)
    {
        using var gpu = new GpuContext();
        using var rain = new NativeRainRipple(gpu, 8);
        ComPtr<ID3D11ShaderResourceView> State(Vector4 value)
        {
            var desc = new Texture2DDesc { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.FormatR32G32B32A32Float, SampleDesc = new(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource };
            var data = new SubresourceData { PSysMem = &value, SysMemPitch = 16 };
            ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, in data, &texture));
            try { ID3D11ShaderResourceView* view = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, &view)); return view; }
            finally { texture->Release(); }
        }
        float noiseValue = .25f;
        var noiseDesc = new Texture3DDesc { Width = 1, Height = 1, Depth = 1, MipLevels = 1, Format = Format.FormatR32Float, Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource };
        var noiseData = new SubresourceData { PSysMem = &noiseValue, SysMemPitch = 4, SysMemSlicePitch = 4 };
        ID3D11Texture3D* noiseTexture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture3D(in noiseDesc, in noiseData, &noiseTexture));
        using ComPtr<ID3D11Texture3D> ownedNoise = noiseTexture;
        ID3D11ShaderResourceView* noiseSrv = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)noiseTexture, (ShaderResourceViewDesc*)null, &noiseSrv));
        using ComPtr<ID3D11ShaderResourceView> noise = noiseSrv;
        var samplerDesc = new SamplerDesc { Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxLOD = float.MaxValue };
        ID3D11SamplerState* samplerState = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in samplerDesc, &samplerState));
        using ComPtr<ID3D11SamplerState> sampler = samplerState;
        Vector4[] Read(ComPtr<ID3D11ShaderResourceView> view)
        {
            ID3D11Resource* resource = null; view.Handle->GetResource(&resource);
            try
            {
                Texture2DDesc desc; ((ID3D11Texture2D*)resource)->GetDesc(&desc); desc.Usage = Usage.Staging; desc.BindFlags = 0; desc.CPUAccessFlags = (uint)CpuAccessFlag.Read;
                ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &texture));
                using ComPtr<ID3D11Texture2D> staging = texture; gpu.Context11.CopyResource((ID3D11Resource*)texture, resource);
                MappedSubresource map; SilkMarshal.ThrowHResult(gpu.Context11.Map((ID3D11Resource*)texture, 0, Map.Read, 0, &map));
                try
                {
                    var values = new Vector4[64];
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                    {
                        byte* row = (byte*)map.PData + y * map.RowPitch;
                        if (desc.Format == Format.FormatR32G32B32A32Float) values[y * 8 + x] = ((Vector4*)row)[x];
                        else if (desc.Format == Format.FormatR16G16B16A16Float)
                        { var v = (Half*)(row + x * 8); values[y * 8 + x] = new((float)v[0], (float)v[1], (float)v[2], (float)v[3]); }
                        else if (desc.Format == Format.FormatR8Unorm) values[y * 8 + x] = new(row[x] / 255f);
                        else { var v = row + x * 4; values[y * 8 + x] = new Vector4(v[0], v[1], v[2], v[3]) / 255; }
                    }
                    return values;
                }
                finally { gpu.Context11.Unmap((ID3D11Resource*)texture, 0); }
            }
            finally { resource->Release(); }
        }
        using var state = State(new(2, 3, 2, .4f));
        var input = new NativeRainRippleInputs(state, noise, sampler, 1, 1.016f, 0);
        check("native rain explicit input renders two finite analysis targets", rain.Evaluate(input) && Read(rain.HeightVelocity).All(Finite) && Read(rain.Normal).All(Finite));
        float dt = input.CurrentTime - input.PreviousTime;
        float velocity = (3 - 20 * dt) * MathF.Pow(2, dt * -1.736966f);
        var expected = new Vector4(2 + velocity * dt, velocity, 2 + velocity * dt, .4f - dt);
        check("native rain zero-weather state integrates height velocity and lifetime", Read(rain.HeightVelocity).All(v => Vector4.Distance(v, expected) < .00001f));
        check("native rain flat height derives neutral normal and pre-decay lifetime", Read(rain.Normal).All(v => v == new Vector4(.5f, .5f, 1, .4f)));
        rain.Evaluate(input with { CurrentTime = 2, PreviousTime = 1.8f }); var capped = Read(rain.HeightVelocity);
        rain.Evaluate(input with { CurrentTime = 2, PreviousTime = 1.95f });
        check("native rain time delta caps at fifty milliseconds", capped.Zip(Read(rain.HeightVelocity)).All(p => Vector4.Distance(p.First, p.Second) < .00001f));
        using var extreme = State(new(100, -100, -100, -2));
        rain.Evaluate(input with { PreviousState = extreme, CurrentTime = 1.2f, PreviousTime = 1 });
        check("native rain clamps height velocity and nonnegative lifetime", Read(rain.HeightVelocity).All(v => v == new Vector4(15, -15, 15, 0)));
        rain.Evaluate(input with { CurrentTime = .0025f, PreviousTime = 0, RainParameter = 10000 });
        check("native rain native threshold spawns impulse in both targets", Read(rain.HeightVelocity).All(v => v == new Vector4(5, 0, 5, 10)) && Read(rain.Normal).All(v => v.W == 1));
        var before = Read(rain.HeightVelocity);
        check("native rain invalid input refuses without modifying output", !rain.Evaluate(input with { CurrentTime = float.NaN }) && !rain.Evaluate(input with { RainParameter = -1 }) && !rain.Evaluate(input with { Noise = state }) && before.SequenceEqual(Read(rain.HeightVelocity)));
        check("native rain feedback target alias refuses before render", !rain.Evaluate(input with { PreviousState = rain.HeightVelocity }) && before.SequenceEqual(Read(rain.HeightVelocity)));
        using var nativePrecision = new NativeRainRipple(gpu, 8, nativePrecision: true);
        check("native rain verified half state and UNORM normal targets quantize kernel output", nativePrecision.Evaluate(input) &&
            Read(nativePrecision.HeightVelocity).All(v => HalfPrecision(v, expected)) &&
            Read(nativePrecision.Normal).All(v => Math.Abs(v.X - .5f) <= 1f / 255 && Math.Abs(v.Y - .5f) <= 1f / 255 && v.Z == 1 && Math.Abs(v.W - .4f) <= 1f / 255));
        // D3D target conversion permits device rounding, so compare one storage quantum rather
        // than incorrectly requiring the CPU Half round-to-nearest tie behavior.
        static bool HalfPrecision(Vector4 actual, Vector4 expected)
        {
            for (int i = 0; i < 4; i++) if (Math.Abs(actual[i] - expected[i]) > Math.Max(Math.Abs(expected[i]) / 1024, 1f / 16384)) return false;
            return true;
        }
        static bool Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);
    }
}
