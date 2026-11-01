using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestNativeWaterFoam(Action<string, bool> check)
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
        try
        {
            foam.Reset(17); check("native offscreen foam reset clears clock and readiness", !foam.Ready && foam.Frames == 0 && foam.Time == 0 && Read().All(v => v == 0));
            for (int i = 0; i < 12; i++) if (!foam.Step(1f / 60, input, out string? reason)) throw new InvalidOperationException(reason);
            var first = Read();
            check("native offscreen foam produces finite nonzero history", foam.Ready && first.All(v => Half.IsFinite(BitConverter.UInt16BitsToHalf(v))) && first.Any(v => v != 0));
            check("native offscreen foam advances exactly one frame per call", foam.Frames == 12 && Math.Abs(foam.Time - 12d * (1f / 60)) < 1e-12 && foam.ZeroRippleInput);
            foam.Reset(17); for (int i = 0; i < 12; i++) foam.Step(1f / 60, input, out _);
            check("native offscreen foam fixed-step reset replay is byte exact", Read().AsSpan().SequenceEqual(first));
            var before = Read(); long frames = foam.Frames; double time = foam.Time;
            check("native offscreen foam refuses negative delta before writes", !foam.Step(-1, input, out _) && foam.Frames == frames && foam.Time == time && Read().AsSpan().SequenceEqual(before));
            check("native offscreen foam refuses infinite delta before writes", !foam.Step(float.PositiveInfinity, input, out _) && Read().AsSpan().SequenceEqual(before));
            global[1] = float.NaN;
            check("native offscreen foam refuses nonfinite global constants", !foam.Step(1f / 60, input, out _) && Read().AsSpan().SequenceEqual(before)); global[1] = 0;
            check("native offscreen foam refuses incomplete constant layouts", !foam.Step(1f / 60, input with { ViewConstants = view.AsMemory(0, 128) }, out _) && Read().AsSpan().SequenceEqual(before));
            view[12] = float.NaN;
            check("native offscreen foam refuses nonfinite view constants", !foam.Step(1f / 60, input, out _) && Read().AsSpan().SequenceEqual(before)); view[12] = 0;
            check("native offscreen foam refuses absent depth", !foam.Step(1f / 60, input with { Depth = default }, out _) && Read().AsSpan().SequenceEqual(before));
            check("native offscreen foam refuses wrong height format", !foam.Step(1f / 60, input with { Height = depth.View }, out _));
            check("native offscreen foam refuses inferred blur sampler", !foam.Step(1f / 60, input with { BlurSampler = default }, out _));
            view[0] = 4; view[91] = -4; foam.Step(1f / 60, input, out _); var moved = Read();
            check("native offscreen foam camera origin reprojects history", !moved.AsSpan().SequenceEqual(before));
            foam.Reset(17); view[0] = view[91] = 0; for (int i = 0; i < 12; i++) foam.Step(1f / 60, input, out _);
            view[0] = 4; view[91] = -4; foam.Step(1f / 60, input, out _);
            check("native offscreen foam moving-camera replay is byte exact", Read().AsSpan().SequenceEqual(moved));
            foam.Reset(19); view[0] = view[91] = 0; for (int i = 0; i < 12; i++) foam.Step(1f / 60, input, out _);
            check("native offscreen foam exposes deterministic noise seed", foam.Seed == 19 && !Read().AsSpan().SequenceEqual(first));
            foam.Dispose();
            check("native offscreen foam disposed output cannot be borrowed", foam.Output.Handle == null && !foam.Ready && !foam.Step(1f / 60, input, out _));
        }
        finally
        {
            depth.View.Dispose(); depth.Texture.Dispose(); height.View.Dispose(); height.Texture.Dispose();
            displacement.View.Dispose(); displacement.Texture.Dispose();
        }
    }
}
