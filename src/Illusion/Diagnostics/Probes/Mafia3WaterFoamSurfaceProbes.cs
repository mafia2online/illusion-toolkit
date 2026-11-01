using System.Numerics;
using System.Reflection;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestNativeWaterFoamSurface(Action<string, bool> check)
    {
        using var gpu = new GpuContext(); using var compiler = D3DCompiler.GetApi();
        var compile = typeof(SceneRenderer).Assembly.GetType("Illusion.Rendering.Shaders.ShaderCompiler")!.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static)!;
        const string vertexSource = "float4 main(uint id:SV_VertexID):SV_Position{float2 uv=float2((id<<1)&2,id&2);return float4(uv*float2(2,-2)+float2(-1,1),0,1);}";
        using var vertexCode = (ComPtr<ID3D10Blob>)compile.Invoke(null, [compiler, vertexSource, "main", "vs_5_0", "foam-material-probe"])!;
        ID3D11VertexShader* vertex = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateVertexShader(vertexCode.GetBufferPointer(), vertexCode.GetBufferSize(), (ID3D11ClassLinkage*)null, &vertex)); using ComPtr<ID3D11VertexShader> vs = vertex;
        string source = NativeWaterFoamSurfaceShader.Functions + """
Texture2D<float4> Variance:register(t0);Texture2D<float4> Detail:register(t1);
Texture2D<float4> Normal:register(t2);Texture2D<float4> History:register(t3);SamplerState Point:register(s0);
NativeWaterFoamMaterial Material(){return EvaluateNativeWaterFoamMaterial(Variance,Detail,Normal,History,
Point,Point,0,float3(0,0,1),0,0,0,0,0,float4(.6,.3,.9,.5),float4(.2,.175,.5,1),1,1,0);}
float4 Diffuse():SV_Target{NativeWaterFoamMaterial m=Material();return float4(m.DiffuseDiagnostic,m.Roughness);}
float4 Normals():SV_Target{NativeWaterFoamMaterial m=Material();return float4(m.Normal,m.Weight);}
""";
        ComPtr<ID3D11PixelShader> Pixel(string entry)
        {
            using var code = (ComPtr<ID3D10Blob>)compile.Invoke(null, [compiler, source, entry, "ps_5_0", "foam-material-probe"])!;
            ID3D11PixelShader* shader = null; SilkMarshal.ThrowHResult(gpu.Device11.CreatePixelShader(code.GetBufferPointer(), code.GetBufferSize(), (ID3D11ClassLinkage*)null, &shader)); return shader;
        }
        using var diffuse = Pixel("Diffuse"); using var normals = Pixel("Normals");
        ComPtr<ID3D11ShaderResourceView> Texture(Vector4 value)
        {
            var desc = new Texture2DDesc { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.FormatR32G32B32A32Float, SampleDesc = new(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource };
            var data = new SubresourceData { PSysMem = &value, SysMemPitch = 16 };
            ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, in data, &texture));
            try { ID3D11ShaderResourceView* srv = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, &srv)); return srv; }
            finally { texture->Release(); }
        }
        using var variance = Texture(new(.5f)); using var detail = Texture(new(.75f, .13f, .2f, 1)); using var normal = Texture(new(.25f, .75f, 1, 1));
        var sd = new SamplerDesc { Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxLOD = float.MaxValue };
        ID3D11SamplerState* sampler = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in sd, &sampler)); using ComPtr<ID3D11SamplerState> point = sampler;
        var desc = new Texture2DDesc { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.FormatR32G32B32A32Float, SampleDesc = new(1, 0), Usage = Usage.Default, BindFlags = (uint)BindFlag.RenderTarget };
        ID3D11Texture2D* target = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &target)); using ComPtr<ID3D11Texture2D> owned = target;
        ID3D11RenderTargetView* rtv = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRenderTargetView((ID3D11Resource*)target, (RenderTargetViewDesc*)null, &rtv)); using ComPtr<ID3D11RenderTargetView> view = rtv;
        desc.Usage = Usage.Staging; desc.BindFlags = 0; desc.CPUAccessFlags = (uint)CpuAccessFlag.Read;
        ID3D11Texture2D* staging = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &staging)); using ComPtr<ID3D11Texture2D> stagingOwned = staging;
        var rd = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true };
        ID3D11RasterizerState* raster = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rd, &raster)); using ComPtr<ID3D11RasterizerState> rasterState = raster;
        var ctx = gpu.Context11; var viewport = new Silk.NET.Direct3D11.Viewport(0, 0, 1, 1, 0, 1); ctx.RSSetViewports(1, &viewport); ctx.RSSetState(rasterState.Handle);
        ctx.IASetInputLayout((ID3D11InputLayout*)null); ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist); ctx.VSSetShader(vs.Handle, null, 0);
        var rt = view.Handle; ctx.OMSetRenderTargets(1, &rt, (ID3D11DepthStencilView*)null); var sample = point.Handle; ctx.PSSetSamplers(0, 1, &sample);
        Vector4 Draw(ComPtr<ID3D11PixelShader> shader, ComPtr<ID3D11ShaderResourceView> history)
        {
            ID3D11ShaderResourceView** resources = stackalloc ID3D11ShaderResourceView*[4] { variance.Handle, detail.Handle, normal.Handle, history.Handle };
            ctx.PSSetShaderResources(0, 4, resources); ctx.PSSetShader(shader.Handle, null, 0); ctx.Draw(3, 0);
            ctx.CopyResource((ID3D11Resource*)stagingOwned.Handle, (ID3D11Resource*)owned.Handle);
            MappedSubresource map; SilkMarshal.ThrowHResult(ctx.Map((ID3D11Resource*)stagingOwned.Handle, 0, Map.Read, 0, &map));
            Vector4 value = *(Vector4*)map.PData; ctx.Unmap((ID3D11Resource*)stagingOwned.Handle, 0); return value;
        }
        foreach (float weight in new[] { 0f, .125f, .5f, 1f })
        {
            using var history = Texture(new(weight));
            Vector3 expected = Vector3.Clamp(weight * (weight + .25f) * new Vector3(.6f, .3f, .9f), Vector3.Zero, Vector3.One);
            float rough = (1 - MathF.Sqrt(.5f)) * (1 - weight) + .5f * weight;
            Vector4 color = Draw(diffuse, history);
            check($"native foam material GPU red-detail diffuse/roughness weight {weight}", Vector4.Distance(color, new(expected, rough)) < .00001f);
            Vector4 n = Draw(normals, history); Vector3 expectedNormal = Vector3.Normalize(new(-.5f * weight, .5f * weight, 1));
            check($"native foam material GPU authored normal/history weight {weight}", Vector4.Distance(n, new(expectedNormal, weight)) < .00001f);
        }
        ID3D11ShaderResourceView** empty = stackalloc ID3D11ShaderResourceView*[4]; for (int i = 0; i < 4; i++) empty[i] = null;
        ctx.PSSetShaderResources(0, 4, empty); ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
    }
}
