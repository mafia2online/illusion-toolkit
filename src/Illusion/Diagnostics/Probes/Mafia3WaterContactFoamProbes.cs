using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static unsafe void TestNativeContactFoam(Action<string, bool> check)
    {
        using var gpu = new GpuContext();
        (ComPtr<ID3D11Texture2D> Texture, ComPtr<ID3D11ShaderResourceView> View) Field(int size, Format format, Vector4 value)
        {
            int components = format == Format.FormatR32G32B32A32Float ? 4 : 1;
            var raw = new byte[size * size * (format == Format.FormatR16Float ? 2 : 4 * components)];
            for (int i = 0; i < size * size; i++)
                if (format == Format.FormatR16Float) BitConverter.TryWriteBytes(raw.AsSpan(i * 2), BitConverter.HalfToUInt16Bits((Half)value.X));
                else for (int c = 0; c < components; c++) BitConverter.TryWriteBytes(raw.AsSpan((i * components + c) * 4), value[c]);
            fixed (byte* data = raw)
            {
                var desc = new Texture2DDesc { Width = (uint)size, Height = (uint)size, MipLevels = 1, ArraySize = 1, Format = format, SampleDesc = new(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource };
                var initial = new SubresourceData { PSysMem = data, SysMemPitch = (uint)(raw.Length / size) };
                ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, in initial, &texture));
                ID3D11ShaderResourceView* view = null;
                try { SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, &view)); return ((ComPtr<ID3D11Texture2D>)texture, (ComPtr<ID3D11ShaderResourceView>)view); }
                catch { texture->Release(); throw; }
            }
        }
        var depth = Field(512, Format.FormatR32Float, new(10));
        var height = Field(256, Format.FormatR16Float, Vector4.Zero);
        var displacement = Field(256, Format.FormatR32G32B32A32Float, new(.3f, .1f, .2f, 1));
        var samplerDesc = new SamplerDesc { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxLOD = float.MaxValue };
        ID3D11SamplerState* state = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in samplerDesc, &state));
        using ComPtr<ID3D11SamplerState> sampler = state;
        var parameters = new NativeWaterFoamParameters(new(.8f, .7f, .001f), new(.2f, 1, .01f, .0001f), 6, new(.7f, .99f));
        using var foam = new NativeWaterFoam(gpu, parameters);
        var view = new float[132]; var global = new float[220];
        view[2] = 10; view[7] = 1; view[32] = view[33] = 1;
        view[85] = view[86] = view[87] = 1; view[88] = view[93] = 1; view[98] = view[102] = -1;
        view[99] = view[103] = 10; view[120] = view[126] = 1; view[129] = -1;
        var input = new NativeWaterFoamInputs(view, global, depth.View, height.View, displacement.View, sampler);
        ushort[] Read()
        {
            ID3D11Resource* resource = null; foam.Output.Handle->GetResource(&resource);
            try
            {
                Texture2DDesc desc; ((ID3D11Texture2D*)resource)->GetDesc(&desc);
                desc.Usage = Usage.Staging; desc.BindFlags = 0; desc.CPUAccessFlags = (uint)CpuAccessFlag.Read;
                ID3D11Texture2D* staging = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &staging));
                using ComPtr<ID3D11Texture2D> owned = staging; gpu.Context11.CopyResource((ID3D11Resource*)staging, resource);
                MappedSubresource map; SilkMarshal.ThrowHResult(gpu.Context11.Map((ID3D11Resource*)staging, 0, Map.Read, 0, &map));
                var values = new ushort[512 * 512];
                try { for (int y = 0; y < 512; y++) new ReadOnlySpan<ushort>((byte*)map.PData + y * map.RowPitch, 512).CopyTo(values.AsSpan(y * 512)); }
                finally { gpu.Context11.Unmap((ID3D11Resource*)staging, 0); }
                return values;
            }
            finally { resource->Release(); }
        }
        using var contact = new NativeWaterContact(gpu, 512, nativePrecision: true);
        var previous = Field(1, Format.FormatR32G32B32A32Float, new(0, 0, .2f, 0));
        var oldFoam = Field(1, Format.FormatR32G32B32A32Float, Vector4.Zero);
        var stamp = Field(1, Format.FormatR32G32B32A32Float, new(.5f, 0, 0, 0));
        var wind = Field(1, Format.FormatR32G32B32A32Float, new(.5f));
        try
        {
            foam.Reset(17); if (!foam.Step(1f / 60, input, out string? reason)) throw new InvalidOperationException(reason);
            var baseline = Read();
            bool generated = contact.Evaluate(new(previous.View, oldFoam.View, oldFoam.View, stamp.View, wind.View,
                sampler, sampler, Vector4.Zero, new(48, 48), Vector2.Zero, 0, 1f / 60));
            foam.Reset(17); bool accepted = foam.Step(1f / 60, input with { WaveFoam = contact.Foam }, out reason);
            check("native contact R8 output composes into native foam input without conversion", generated && accepted && !foam.ZeroRippleInput);
            check("native contact contribution changes native foam history", !Read().AsSpan().SequenceEqual(baseline));
            foam.Reset(17); foam.Step(1f / 60, input, out _);
            check("native contact detach and reset restores exact native foam baseline", foam.ZeroRippleInput && Read().AsSpan().SequenceEqual(baseline));
        }
        finally
        {
            depth.View.Dispose(); depth.Texture.Dispose(); height.View.Dispose(); height.Texture.Dispose();
            displacement.View.Dispose(); displacement.Texture.Dispose(); previous.View.Dispose(); previous.Texture.Dispose();
            oldFoam.View.Dispose(); oldFoam.Texture.Dispose(); stamp.View.Dispose(); stamp.Texture.Dispose(); wind.View.Dispose(); wind.Texture.Dispose();
        }
    }
}
