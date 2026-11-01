using System.Numerics;
using System.Reflection;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static unsafe void TestNativeContactMipChain(GpuContext gpu, Action<string, bool> check)
    {
        using var contact = new NativeWaterContact(gpu, 512, nativePrecision: true);
        byte[] stamp = new byte[512 * 512]; stamp.AsSpan(0, stamp.Length / 2).Fill(255);
        var desc = new Texture2DDesc { Width = 512, Height = 512, MipLevels = 1, ArraySize = 1, Format = Format.FormatR8Unorm, SampleDesc = new(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource };
        ComPtr<ID3D11ShaderResourceView> Texture(Texture2DDesc d, SubresourceData data)
        {
            ID3D11Texture2D* t = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in d, in data, &t));
            try { ID3D11ShaderResourceView* v = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)t, (ShaderResourceViewDesc*)null, &v)); return v; }
            finally { t->Release(); }
        }
        ComPtr<ID3D11ShaderResourceView> stamps;
        fixed (byte* bytes = stamp) stamps = Texture(desc, new SubresourceData { PSysMem = bytes, SysMemPitch = 512 });
        using var ownedStamp = stamps;
        Vector4 zeroValue = default; desc.Width = desc.Height = 1; desc.Format = Format.FormatR32G32B32A32Float;
        using var zero = Texture(desc, new SubresourceData { PSysMem = &zeroValue, SysMemPitch = 16 });
        ComPtr<ID3D11SamplerState> Sampler(Filter filter, TextureAddressMode address)
        {
            var sd = new SamplerDesc { Filter = filter, MaxAnisotropy = 16, AddressU = address, AddressV = address, AddressW = address, MaxLOD = float.MaxValue };
            ID3D11SamplerState* s = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in sd, &s)); return s;
        }
        using var point = Sampler(Filter.MinMagMipPoint, TextureAddressMode.Clamp);
        using var anisotropicBorder = Sampler(Filter.Anisotropic, TextureAddressMode.Border);
        if (!contact.Evaluate(new(zero, zero, zero, stamps, zero, point, point, Vector4.Zero, Vector2.Zero, Vector2.Zero, 0, 1f / 60))) throw new InvalidOperationException("Native mip input was refused.");
        ID3D11Resource* source = null; contact.State.Handle->GetResource(&source);
        using ComPtr<ID3D11Resource> ownedSource = source;
        Texture2DDesc sourceDesc; ((ID3D11Texture2D*)source)->GetDesc(&sourceDesc);
        ShaderResourceViewDesc full; contact.State.Handle->GetDesc(&full);
        check("native contact state exposes complete generated ten-level mip chain", sourceDesc.MipLevels == 10 && full.Texture2D.MostDetailedMip == 0 && full.Texture2D.MipLevels == 10);
        var partialDesc = full; partialDesc.Texture2D.MipLevels = 1;
        ID3D11ShaderResourceView* partial = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView(source, in partialDesc, &partial));
        using ComPtr<ID3D11ShaderResourceView> baseOnly = partial;
        using var compiler = D3DCompiler.GetApi();
        var compile = typeof(SceneRenderer).Assembly.GetType("Illusion.Rendering.Shaders.ShaderCompiler")!.GetMethod("Compile", BindingFlags.Static | BindingFlags.Public)!;
        using var vsCode = (ComPtr<ID3D10Blob>)compile.Invoke(null, [compiler, "float4 main(uint id:SV_VertexID):SV_Position{float2 uv=float2((id<<1)&2,id&2);return float4(uv*float2(2,-2)+float2(-1,1),0,1);}", "main", "vs_5_0", "native-contact-mips"])!;
        using var psCode = (ComPtr<ID3D10Blob>)compile.Invoke(null, [compiler, "Texture2D<float4> State:register(t0);SamplerState Sampled:register(s0);float4 main(float4 pos:SV_Position):SV_Target{return State.Sample(Sampled,pos.xy-.375);}", "main", "ps_5_0", "native-contact-mips"])!;
        ID3D11VertexShader* vShader = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateVertexShader(vsCode.GetBufferPointer(), vsCode.GetBufferSize(), (ID3D11ClassLinkage*)null, &vShader));
        using ComPtr<ID3D11VertexShader> vertex = vShader;
        ID3D11PixelShader* pShader = null; SilkMarshal.ThrowHResult(gpu.Device11.CreatePixelShader(psCode.GetBufferPointer(), psCode.GetBufferSize(), (ID3D11ClassLinkage*)null, &pShader));
        using ComPtr<ID3D11PixelShader> pixel = pShader;
        desc.Usage = Usage.Default; desc.BindFlags = (uint)BindFlag.RenderTarget;
        ID3D11Texture2D* target = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &target));
        using ComPtr<ID3D11Texture2D> ownedTarget = target;
        ID3D11RenderTargetView* rtv = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRenderTargetView((ID3D11Resource*)target, (RenderTargetViewDesc*)null, &rtv));
        using ComPtr<ID3D11RenderTargetView> targetView = rtv;
        desc.Usage = Usage.Staging; desc.BindFlags = 0; desc.CPUAccessFlags = (uint)CpuAccessFlag.Read;
        ID3D11Texture2D* staging = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &staging));
        using ComPtr<ID3D11Texture2D> readback = staging;
        Vector4 Draw(ComPtr<ID3D11ShaderResourceView> input, ComPtr<ID3D11SamplerState> sampler)
        {
            var ctx = gpu.Context11; var viewport = new Silk.NET.Direct3D11.Viewport(0, 0, 1, 1, 0, 1); ctx.RSSetViewports(1, &viewport);
            var render = targetView.Handle; ctx.OMSetRenderTargets(1, &render, (ID3D11DepthStencilView*)null);
            ctx.VSSetShader(vertex.Handle, null, 0); ctx.PSSetShader(pixel.Handle, null, 0);
            var view = input.Handle; ctx.PSSetShaderResources(0, 1, &view); var sampled = sampler.Handle; ctx.PSSetSamplers(0, 1, &sampled); ctx.Draw(3, 0);
            ctx.CopyResource((ID3D11Resource*)readback.Handle, (ID3D11Resource*)ownedTarget.Handle);
            MappedSubresource map; SilkMarshal.ThrowHResult(ctx.Map((ID3D11Resource*)readback.Handle, 0, Map.Read, 0, &map));
            try { return *(Vector4*)map.PData; } finally { ctx.Unmap((ID3D11Resource*)readback.Handle, 0); }
        }
        Vector4 minified = Draw(contact.State, point), onlyBase = Draw(baseOnly, point);
        check("native contact actual GPU minification consumes generated mip instead of base texels", minified == new Vector4(0, 0, -.5f, 0) && onlyBase == new Vector4(0, 0, -1, 0));
        Vector4 bordered = Draw(contact.State, anisotropicBorder);
        check("native contact minified anisotropic zero border preserves finite footprint", Vector4.Distance(bordered, new Vector4(0, 0, -.1953125f, 0)) < .0001f);
        var context = gpu.Context11; ID3D11ShaderResourceView* empty = null; context.PSSetShaderResources(0, 1, &empty); context.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
    }
}
