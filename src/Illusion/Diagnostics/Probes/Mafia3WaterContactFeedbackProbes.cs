using System.IO;
using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestNativeWaterContactFeedback(Action<string, bool> check)
    {
        using var gpu = new GpuContext();
        ComPtr<ID3D11ShaderResourceView> Field(int size, Format format, Vector4 value)
        {
            int stride = format == Format.FormatR16Float ? 2 : format == Format.FormatR32Float ? 4 : 16;
            var bytes = new byte[size * size * stride];
            for (int i = 0; i < size * size; i++)
                if (stride == 2) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), BitConverter.HalfToUInt16Bits((Half)value.X));
                else for (int c = 0; c < stride / 4; c++) BitConverter.TryWriteBytes(bytes.AsSpan(i * stride + c * 4), value[c]);
            fixed (byte* data = bytes)
            {
                var desc = new Texture2DDesc { Width = (uint)size, Height = (uint)size, MipLevels = 1, ArraySize = 1, Format = format, SampleDesc = new(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource };
                var initial = new SubresourceData { PSysMem = data, SysMemPitch = (uint)(size * stride) };
                ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, in initial, &texture));
                try { ID3D11ShaderResourceView* view = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, &view)); return view; }
                finally { texture->Release(); }
            }
        }
        byte[] Read(ComPtr<ID3D11ShaderResourceView> view)
        {
            ID3D11Resource* source = null; view.Handle->GetResource(&source);
            try
            {
                Texture2DDesc desc; ((ID3D11Texture2D*)source)->GetDesc(&desc);
                int stride = desc.Format == Format.FormatR16Float ? 2 : desc.Format == Format.FormatR8Unorm ? 1 : desc.Format == Format.FormatR8G8B8A8Unorm ? 4 : 8;
                desc.MipLevels = 1; desc.Usage = Usage.Staging; desc.BindFlags = 0; desc.CPUAccessFlags = (uint)CpuAccessFlag.Read; desc.MiscFlags = 0;
                ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &texture));
                using ComPtr<ID3D11Texture2D> staging = texture;
                gpu.Context11.CopySubresourceRegion((ID3D11Resource*)texture, 0, 0, 0, 0, source, 0, (Box*)null);
                MappedSubresource map; SilkMarshal.ThrowHResult(gpu.Context11.Map((ID3D11Resource*)texture, 0, Map.Read, 0, &map));
                try
                {
                    var result = new byte[desc.Width * desc.Height * stride];
                    for (int y = 0; y < desc.Height; y++) new ReadOnlySpan<byte>((byte*)map.PData + y * map.RowPitch, (int)desc.Width * stride).CopyTo(result.AsSpan(y * (int)desc.Width * stride));
                    return result;
                }
                finally { gpu.Context11.Unmap((ID3D11Resource*)texture, 0); }
            }
            finally { source->Release(); }
        }
        var indices = (from y in Enumerable.Range(240, 32) from x in Enumerable.Range(240, 32) select y * 512 + x).ToArray();
        using var contact = new NativeWaterContactPreview(gpu, Vector2.Zero, indices, Enumerable.Repeat(.8f, indices.Length).ToArray(), 6);
        using var foam = new NativeWaterFoam(gpu, new(new(.8f, .7f, .001f), new(.2f, 1, .01f, .0001f), 6, new(.7f, .99f)));
        using var depth = Field(512, Format.FormatR32Float, new(10));
        using var height = Field(256, Format.FormatR16Float, Vector4.Zero);
        using var displacement = Field(256, Format.FormatR32G32B32A32Float, new(.3f, .1f, .2f, 1));
        var samplerDesc = new SamplerDesc { Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Mirror, AddressV = TextureAddressMode.Mirror, AddressW = TextureAddressMode.Mirror, MaxLOD = float.MaxValue };
        ID3D11SamplerState* samplerPointer = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in samplerDesc, &samplerPointer));
        using ComPtr<ID3D11SamplerState> sampler = samplerPointer;
        var view = new float[132]; var global = new float[220];
        view[2] = 10; view[7] = 1; view[32] = view[33] = 1;
        view[85] = view[86] = view[87] = 1; view[88] = view[93] = 1; view[98] = view[102] = -1;
        view[99] = view[103] = 10; view[120] = view[126] = 1; view[129] = -1;
        var input = new NativeWaterFoamInputs(view, global, depth, height, displacement, sampler);
        check("native contact feedback source is initially cleared RGBA8", Read(foam.GeneratedSource).All(v => v == 0) && contact.ZeroFoamSource);
        byte[] Replay(bool incorrectCurrentFoam)
        {
            foam.Reset(1); contact.Seek(6, true);
            for (int frame = 0; frame < 6; frame++)
            {
                var oldContactFoam = contact.Foam;
                if (contact.AdvanceWithFoamSource(foam.GeneratedSource, foam.GeneratedSourceOrigin) != 1) throw new InvalidDataException(contact.Error);
                var sampledFoam = incorrectCurrentFoam ? contact.Foam : oldContactFoam;
                if (!foam.Step(1f / 60, input with { WaveFoam = sampledFoam }, out string? refusal)) throw new InvalidDataException(refusal);
            }
            return Read(contact.State).Concat(Read(contact.Foam)).Concat(Read(foam.GeneratedSource)).Concat(Read(foam.Output)).ToArray();
        }
        byte[] correct = Replay(false);
        check("native contact and auxiliary foam advance one coherent six-frame clock", contact.Frame == 6 && foam.Frames == 6 && !contact.ZeroFoamSource && contact.ZeroWind && !foam.ZeroRippleInput);
        check("native contact feedback replay restores all four stored outputs exactly", correct.SequenceEqual(Replay(false)));
        check("native old-contact-foam timing is observably distinct from current-frame feedback", !correct.SequenceEqual(Replay(true)));
        Replay(false); byte[] before = Read(contact.State); int previousFrame = contact.Frame;
        contact.Seek(7, false);
        check("native contact invalid auxiliary format and origin refuse without state writes", contact.AdvanceWithFoamSource(height, Vector2.Zero) == 0 &&
            contact.AdvanceWithFoamSource(foam.GeneratedSource, new(float.NaN, 0)) == 0 && contact.Frame == previousFrame && before.SequenceEqual(Read(contact.State)));
        check("native contact valid auxiliary input recovers after refusal", contact.AdvanceWithFoamSource(foam.GeneratedSource, foam.GeneratedSourceOrigin) == 1 && contact.Error is null && contact.Frame == 7);
        foam.Reset(1); contact.Seek(1, true);
        check("native contact reset clears coupled source and histories", Read(foam.GeneratedSource).All(v => v == 0) && contact.Frame == 0 && contact.ZeroFoamSource && foam.Frames == 0);
        check("native independent contact remains explicit zero-source mode", contact.Advance() == 1 && contact.ZeroFoamSource && contact.ZeroWind);
    }
}
