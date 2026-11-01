using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestNativeReflectionTemporal(Action<string, bool> check)
    {
        using var gpu = new GpuContext();

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
                        else if (desc.Format == Format.FormatR11G11B10Float)
                        {
                            uint bits = ((uint*)row)[x];
                            float Channel(uint value, int mantissa) => (float)BitConverter.UInt16BitsToHalf((ushort)(value << (10 - mantissa)));
                            values[y * 8 + x] = new(Channel(bits & 2047, 6), Channel((bits >> 11) & 2047, 6), Channel(bits >> 22, 5), 1);
                        }
                        else if (desc.Format == Format.FormatR8Unorm) values[y * 8 + x] = new(row[x] / 255f);
                        else { var v = row + x * 4; values[y * 8 + x] = new Vector4(v[0], v[1], v[2], v[3]) / 255; }
                    }
                    return values;
                }
                finally { gpu.Context11.Unmap((ID3D11Resource*)texture, 0); }
            }
            finally { resource->Release(); }
        }
        using var current = State(new(2, 3, 4, 1));
        using var previous = State(new(.1f, .2f, .3f, 1));
        using var confidence = State(new(.1f, 0, 0, 0));
        using var roughness = State(Vector4.Zero);
        using var rejection = State(Vector4.Zero);
        using var invalidMotion = State(Vector4.Zero);
        using var depth = State(Vector4.One);
        using var empty = State(Vector4.Zero);
        var camera = new Vector4[25];
        camera[1] = new(-1, 1, 1, 0); camera[2] = new(2, 0, 0, 0); camera[3] = new(0, -2, 0, 0);
        camera[5] = camera[1]; camera[6] = camera[2]; camera[7] = camera[3];
        camera[21] = Vector4.UnitX; camera[22] = Vector4.UnitY; camera[24] = Vector4.UnitZ;
        using var temporal = new NativeReflectionTemporal(gpu, 8);
        var input = new NativeReflectionTemporalInputs(current, previous, roughness, confidence,
            invalidMotion, rejection, sampler, sampler, camera);
        check("native temporal invalid motion returns current radiance", temporal.Evaluate(input) &&
            Read(temporal.Radiance).All(v => Vector4.Distance(v, new(2, 3, 4, 1)) < .00001f));
        check("native temporal invalid motion clears history confidence", Read(temporal.Confidence).All(v => v == Vector4.Zero));
        using var validMotion = State(new(0, 0, 1, 1));
        input = input with { MotionOrDepth = validMotion };
        check("native temporal spatial constant clips outlying history to local variance", temporal.Evaluate(input) &&
            Read(temporal.Radiance).All(v => Vector4.Distance(v, new(2, 3, 4, 1)) < .01f));
        check("native temporal confidence decays by native fixed sixtieth", Read(temporal.Confidence).All(v =>
            Math.Abs(v.X - (.1f - 1f / 60)) < .000001f && v.Y == 0 && v.Z == 0 && v.W == 0));
        using var reproject = new NativeReflectionTemporal(gpu, 8, reproject: true);
        var projected = input with { MotionOrDepth = depth, PreviousRadiance = empty };
        check("native reprojection empty history returns current radiance", reproject.Evaluate(projected) &&
            Read(reproject.Radiance).All(v => Vector4.Distance(v, new(2, 3, 4, 1)) < .00001f));
        var outside = camera.ToArray(); outside[21].W = 4;
        check("native reprojection outside prior frustum rejects history", reproject.Evaluate(projected with { Constants = outside, PreviousRadiance = previous }) &&
            Read(reproject.Radiance).All(v => Vector4.Distance(v, new(2, 3, 4, 1)) < .00001f));
        using var precision = new NativeReflectionTemporal(gpu, 8, nativePrecision: true);
        ShaderResourceViewDesc radianceDesc; precision.Radiance.Handle->GetDesc(&radianceDesc);
        ShaderResourceViewDesc confidenceDesc; precision.Confidence.Handle->GetDesc(&confidenceDesc);
        check("native temporal precision allocates R11G11B10 radiance and R8 confidence", precision.NativePrecision &&
            radianceDesc.Format == Format.FormatR11G11B10Float && confidenceDesc.Format == Format.FormatR8Unorm);
        check("native temporal precision retains radiance within one native mantissa step", precision.Evaluate(input with { MotionOrDepth = invalidMotion }) &&
            Read(precision.Radiance).All(v => Math.Abs(v.X - 2) <= .03125f && Math.Abs(v.Y - 3) <= .03125f && Math.Abs(v.Z - 4) <= .125f && v.W == 1));
        check("native temporal precision stores decayed confidence in native UNORM", precision.Evaluate(input) &&
            Read(precision.Confidence).All(v => Math.Abs(v.X - 21f / 255) < .000001f));
        var before = Read(temporal.Radiance); var nonfinite = camera.ToArray(); nonfinite[24].W = float.NaN;
        check("native temporal invalid camera and missing inputs preserve output", !temporal.Evaluate(input with { Constants = nonfinite }) &&
            !temporal.Evaluate(input with { Constants = [] }) && !temporal.Evaluate(input with { CurrentRadiance = default }) && before.SequenceEqual(Read(temporal.Radiance)));
        check("native temporal output alias refuses before draw", !temporal.Evaluate(input with { CurrentRadiance = temporal.Radiance }) &&
            !temporal.Evaluate(input with { PreviousConfidence = temporal.Confidence }) && before.SequenceEqual(Read(temporal.Radiance)));
    }
}
