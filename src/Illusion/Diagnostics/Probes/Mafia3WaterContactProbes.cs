using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestNativeWaterContact(Action<string, bool> check)
    {
        check("native stamp quantizer accumulates clamps and truncates signed influences", NativeWaterStampQuantizer.Accumulate(0, .5f) == 127
            && NativeWaterStampQuantizer.Accumulate(200, .5f) == 255 && NativeWaterStampQuantizer.Accumulate(200, -.5f) == 72);
        byte[] sparse = NativeWaterStampQuantizer.BuildSparse([0, 512 * 512 - 1], [.5f, 1]);
        check("native sparse stamp preserves all unrequested cells", sparse[0] == 127 && sparse[^1] == 255 && sparse.Skip(1).SkipLast(1).All(v => v == 0));
        bool duplicate = false, invalid = false;
        try { NativeWaterStampQuantizer.BuildSparse([1, 1], [.5f, .5f]); } catch (ArgumentException) { duplicate = true; }
        try { NativeWaterStampQuantizer.BuildSparse([1], [float.NaN]); } catch (ArgumentException) { invalid = true; }
        check("native sparse stamp rejects ambiguous and nonfinite input before construction", duplicate && invalid);
        using var gpu = new GpuContext();
        using var rain = new NativeWaterContact(gpu, 8);
        ComPtr<ID3D11ShaderResourceView> State(Vector4 value)
        {
            var desc = new Texture2DDesc { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.FormatR32G32B32A32Float, SampleDesc = new(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource };
            var data = new SubresourceData { PSysMem = &value, SysMemPitch = 16 };
            ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, in data, &texture));
            try { ID3D11ShaderResourceView* view = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, &view)); return view; }
            finally { texture->Release(); }
        }
        var samplerDesc = new SamplerDesc { Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxLOD = float.MaxValue };
        ID3D11SamplerState* samplerState = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in samplerDesc, &samplerState));
        using ComPtr<ID3D11SamplerState> sampler = samplerState;
        Vector4[] Read(ComPtr<ID3D11ShaderResourceView> view)
        {
            ID3D11Resource* resource = null; view.Handle->GetResource(&resource);
            try
            {
                Texture2DDesc desc; ((ID3D11Texture2D*)resource)->GetDesc(&desc); desc.Usage = Usage.Staging; desc.BindFlags = 0; desc.CPUAccessFlags = (uint)CpuAccessFlag.Read; desc.MiscFlags = 0;
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
        using var state = State(new(0, 0, .2f, 0));
        using var foam = State(new(.1f));
        using var zero = State(Vector4.Zero);
        using var wind = State(new(.5f));
        var input = new NativeWaterContactInputs(state, foam, zero, zero, wind, sampler, sampler,
            Vector4.Zero, new(48, 48), Vector2.Zero, 1, 0);
        check("native contact zero delta preserves sampled state and foam", rain.Evaluate(input) && Read(rain.State).All(v => v == new Vector4(0, 0, .2f, 0)) && Read(rain.Foam).All(v => v == new Vector4(.1f)));
        rain.Evaluate(input with { DeltaTime = -.1f });
        check("native contact negative delta preserves state without invented integration", Read(rain.State).All(v => v == new Vector4(0, 0, .2f, 0)));
        using var stamp = State(new(.4f, 99, 0, 0));
        rain.Evaluate(input with { Stamp = stamp, DeltaTime = .016f });
        check("native contact stamp uses red channel and resets velocity with negative height", Read(rain.State).All(v => v == new Vector4(0, 0, -.4f, 0)) && Read(rain.Foam).All(v => Vector4.Distance(v, new Vector4(.2f)) < .000001f));
        rain.Evaluate(input with { DeltaTime = .016f });
        float damping = MathF.Pow(2, BitConverter.Int32BitsToSingle(unchecked((int)0xbd159c1f)) * .016f);
        float velocity = -5 * .016f * damping;
        var expected = new Vector4(0, 0, (.2f + velocity * .016f) * damping, velocity);
        float expectedFoam = (.1f - 2.5f * .016f * .016f) * damping;
        check("native contact integrates damped height velocity and independent foam", Read(rain.State).All(v => Vector4.Distance(v, expected) < .00001f) && Read(rain.Foam).All(v => Vector4.Distance(v, new Vector4(expectedFoam)) < .00001f));
        check("native contact analysis outputs remain finite", Read(rain.State).All(Finite) && Read(rain.Foam).All(Finite));
        var before = Read(rain.State);
        check("native contact invalid inputs and feedback alias refuse without writes", !rain.Evaluate(input with { DeltaTime = float.NaN }) && !rain.Evaluate(input with { Origins = new(float.PositiveInfinity) }) && !rain.Evaluate(input with { Stamp = rain.State }) && before.SequenceEqual(Read(rain.State)));
        using var nativePrecision = new NativeWaterContact(gpu, 8, nativePrecision: true);
        check("native contact verified half state and R8 foam targets quantize independently", nativePrecision.Evaluate(input with { DeltaTime = .016f }) &&
            Read(nativePrecision.State).All(v => HalfPrecision(v, expected)) &&
            Read(nativePrecision.Foam).All(v => Math.Abs(v.X - expectedFoam) <= 1f / 255));
        TestNativeContactFoam(check);
        TestNativeContactMipChain(gpu, check);
        static bool HalfPrecision(Vector4 actual, Vector4 expected)
        {
            for (int i = 0; i < 4; i++) if (Math.Abs(actual[i] - expected[i]) > Math.Max(Math.Abs(expected[i]) / 1024, 1f / 16384)) return false;
            return true;
        }
        static bool Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);
    }
}
