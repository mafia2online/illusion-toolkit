using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Shaders;
using Illusion.Rendering.Textures;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Passes;

public readonly record struct PreviewBillboard(Vector2 Position, Vector2 HalfSize, Vector4 Color, Vector4 AtlasUv)
{
    public float Rotation { get; init; }
}

/// <summary>Isolated unlit atlas/alpha visualization of evaluated particles, independent of scene shaders.</summary>
public sealed unsafe class ParticleBillboardPreview : IDisposable
{
    private readonly GpuContext _gpu;
    private readonly TextureLibrary _textures;
    private readonly List<TextureLease> _leases = [];
    private ComPtr<ID3D11VertexShader> _vs;
    private ComPtr<ID3D11PixelShader> _ps;
    private ComPtr<ID3D11Buffer> _constants;
    private ComPtr<ID3D11SamplerState> _sampler;
    private ComPtr<ID3D11RasterizerState> _raster;
    private ComPtr<ID3D11BlendState> _blend;
    private ComPtr<ID3D11DepthStencilState> _depth;
    private ComPtr<ID3D11ShaderResourceView> _texture;
    private string? _texturePath;
    private bool _hasTexture;
    private struct Constants { public Vector4 Rect, Uv, Color, Rotation; }
    private const string Hlsl = """
        cbuffer Preview : register(b0) { float4 Rect; float4 Uv; float4 Color; float4 Rotation; };
        Texture2D Atlas : register(t0); SamplerState Samp : register(s0);
        struct Varying { float4 Position : SV_POSITION; float2 Uv : TEXCOORD0; };
        Varying VSMain(uint id : SV_VertexID) {
            const float2 corners[6] = {float2(-1,1),float2(1,1),float2(1,-1),float2(-1,1),float2(1,-1),float2(-1,-1)};
            float2 corner = corners[id]; Varying o;
            float2 offset = corner * Rect.zw;
            offset = float2(Rotation.x * offset.x - Rotation.y * offset.y, Rotation.y * offset.x + Rotation.x * offset.y);
            o.Position = float4(Rect.xy + offset * Rotation.zw, 0, 1);
            o.Uv = lerp(Uv.xy, Uv.zw, float2(corner.x * .5 + .5, .5 - corner.y * .5));
            return o;
        }
        float4 PSMain(Varying i) : SV_TARGET { return Atlas.Sample(Samp, i.Uv) * Color; }
        """;

    public ParticleBillboardPreview(GpuContext gpu)
    {
        _gpu = gpu; _textures = new(gpu);
        try
        {
            using var compiler = D3DCompiler.GetApi();
            using var vs = ShaderCompiler.Compile(compiler, Hlsl, "VSMain", "vs_5_0", "particle-preview");
            using var ps = ShaderCompiler.Compile(compiler, Hlsl, "PSMain", "ps_5_0", "particle-preview");
            (_vs, _ps) = ShaderCompiler.CreateShaders(gpu, vs, ps);
            _constants = GpuBuffers.CreateConstant<Constants>(gpu);
            var sampler = new SamplerDesc { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxLOD = float.MaxValue };
            ID3D11SamplerState* s = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in sampler, ref s)); _sampler = s;
            var raster = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 };
            ID3D11RasterizerState* r = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in raster, ref r)); _raster = r;
            var depth = new DepthStencilDesc { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero };
            ID3D11DepthStencilState* d = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in depth, ref d)); _depth = d;
            var blend = new BlendDesc();
            blend.RenderTarget[0] = new RenderTargetBlendDesc { BlendEnable = 1, SrcBlend = Blend.SrcAlpha, DestBlend = Blend.InvSrcAlpha,
                BlendOp = BlendOp.Add, SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.InvSrcAlpha, BlendOpAlpha = BlendOp.Add,
                RenderTargetWriteMask = (byte)ColorWriteEnable.All };
            ID3D11BlendState* b = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateBlendState(in blend, ref b)); _blend = b;
        }
        catch { Dispose(); throw; }
    }

    public void Render(SharedRenderTarget target, IReadOnlyList<PreviewBillboard> sprites, string? texturePath, float extent)
    {
        if (!float.IsFinite(extent) || extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        var ctx = _gpu.Context11;
        if (!_hasTexture || texturePath != _texturePath)
        {
            _textures.Release(_leases); _leases.Clear();
            if (texturePath is not null) _textures.AddFolder(System.IO.Path.GetDirectoryName(texturePath)!);
            _texture = _textures.Acquire(texturePath, _leases); _texturePath = texturePath; _hasTexture = true;
        }
        ID3D11RenderTargetView* rtv = target.Rtv.Handle; ctx.OMSetRenderTargets(1, &rtv, (ID3D11DepthStencilView*)null);
        float* background = stackalloc float[] { .025f, .03f, .04f, 1 }; ctx.ClearRenderTargetView(rtv, background);
        var viewport = new Viewport(0, 0, target.Width, target.Height, 0, 1); ctx.RSSetViewports(1, &viewport);
        ctx.RSSetState(_raster); ctx.OMSetDepthStencilState(_depth, 0); ctx.OMSetBlendState(_blend, null, uint.MaxValue);
        ctx.IASetInputLayout((ID3D11InputLayout*)null); ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        ctx.VSSetShader(_vs, (ID3D11ClassInstance**)null, 0); ctx.PSSetShader(_ps, (ID3D11ClassInstance**)null, 0);
        ID3D11Buffer* cb = _constants.Handle; ctx.VSSetConstantBuffers(0, 1, &cb); ctx.PSSetConstantBuffers(0, 1, &cb);
        ID3D11SamplerState* sampler = _sampler.Handle; ctx.PSSetSamplers(0, 1, &sampler);
        ID3D11ShaderResourceView* texture = _texture.Handle; ctx.PSSetShaderResources(0, 1, &texture);
        float horizontal = extent * target.Width / target.Height;
        foreach (var sprite in sprites)
        {
            var constants = new Constants { Rect = new(sprite.Position.X / horizontal, sprite.Position.Y / extent,
                sprite.HalfSize.X, sprite.HalfSize.Y), Uv = sprite.AtlasUv, Color = sprite.Color,
                Rotation = new(MathF.Cos(sprite.Rotation), MathF.Sin(sprite.Rotation), 1 / horizontal, 1 / extent) };
            GpuBuffers.UpdateConstant(ctx, _constants, ref constants); ctx.Draw(6, 0);
        }
        texture = null; ctx.PSSetShaderResources(0, 1, &texture); ctx.Flush();
    }

    public void Dispose()
    {
        _textures.Dispose(); _constants.Dispose(); _sampler.Dispose(); _raster.Dispose(); _blend.Dispose(); _depth.Dispose(); _ps.Dispose(); _vs.Dispose();
    }
}
