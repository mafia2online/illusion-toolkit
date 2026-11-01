using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Scene;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

/// <summary>Opt-in preparation of borrowed opaque depth and native water-definition height.
/// Owns its typeless depth copy, linear-depth target, height capture/expansion and GPU state.
/// Does not advance simulation or activate surface rendering. Prepare leaves OM/SRV0 unbound;
/// callers restore their state. Output SRVs remain borrowed until next Prepare or disposal.</summary>
public sealed unsafe class NativeWaterFoamContext : IDisposable
{
    private readonly GpuContext _gpu;
    private ComPtr<ID3D11VertexShader> _fullscreen, _heightVertex;
    private ComPtr<ID3D11PixelShader> _linearize, _heightPixel, _expand;
    private ComPtr<ID3D11InputLayout> _layout;
    private ComPtr<ID3D11Buffer> _depthConstants, _heightConstants;
    private ComPtr<ID3D11RasterizerState> _raster;
    private ComPtr<ID3D11DepthStencilState> _depthState;
    private ComPtr<ID3D11SamplerState> _point;
    private ComPtr<ID3D11Texture2D> _copy, _heightDepth;
    private ComPtr<ID3D11ShaderResourceView> _copyView;
    private ComPtr<ID3D11DepthStencilView> _heightDsv;
    private Field? _linear;
    private readonly Field?[] _height = new Field?[2];
    private bool _disposed;
    public bool Ready { get; private set; }
    public int DefinitionPartsDrawn { get; private set; }
    public NativeWaterFoamCamera? CameraConstants { get; private set; }
    public ComPtr<ID3D11ShaderResourceView> LinearDepth => _disposed || _linear is null ? default : _linear.Resource;
    public ComPtr<ID3D11ShaderResourceView> Height => _disposed ? default : _height[0]!.Resource;

    public NativeWaterFoamContext(GpuContext gpu)
    {
        _gpu = gpu;
        try
        {
            using var compiler = D3DCompiler.GetApi();
            using var fullscreen = ShaderCompiler.Compile(compiler, NativeWaterFoamContextShader.Fullscreen, "main", "vs_5_0", "foam-context-fullscreen");
            using var height = ShaderCompiler.Compile(compiler, NativeWaterFoamContextShader.HeightVertex, "main", "vs_5_0", "foam-height");
            ID3D11VertexShader* vs = null;
            SilkMarshal.ThrowHResult(gpu.Device11.CreateVertexShader(fullscreen.GetBufferPointer(), fullscreen.GetBufferSize(), (ID3D11ClassLinkage*)null, &vs)); _fullscreen = vs;
            vs = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateVertexShader(height.GetBufferPointer(), height.GetBufferSize(), (ID3D11ClassLinkage*)null, &vs)); _heightVertex = vs;
            _linearize = Pixel(compiler, NativeWaterFoamContextShader.Depth);
            _heightPixel = Pixel(compiler, NativeWaterFoamContextShader.HeightPixel);
            _expand = Pixel(compiler, NativeWaterFoamContextShader.Expand);
            nint semantic = SilkMarshal.StringToPtr("POSITION");
            try
            {
                var e = new InputElementDesc { SemanticName = (byte*)semantic, Format = Format.FormatR32G32B32Float, InputSlotClass = InputClassification.PerVertexData };
                ID3D11InputLayout* layout = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateInputLayout(&e, 1, height.GetBufferPointer(), height.GetBufferSize(), &layout)); _layout = layout;
            }
            finally { SilkMarshal.Free(semantic); }
            _depthConstants = Buffer(16); _heightConstants = Buffer(128);
            var rd = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true };
            ID3D11RasterizerState* raster = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rd, &raster)); _raster = raster;
            var dd = new DepthStencilDesc { DepthEnable = true, DepthWriteMask = DepthWriteMask.All, DepthFunc = ComparisonFunc.LessEqual };
            ID3D11DepthStencilState* depth = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in dd, &depth)); _depthState = depth;
            var sd = new SamplerDesc { Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxLOD = float.MaxValue };
            ID3D11SamplerState* sampler = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in sd, &sampler)); _point = sampler;
            _height[0] = new(gpu, 256, 256, Format.FormatR16Float); _height[1] = new(gpu, 256, 256, Format.FormatR16Float);
            var hd = new Texture2DDesc { Width = 256, Height = 256, MipLevels = 1, ArraySize = 1, Format = Format.FormatD32Float, SampleDesc = new(1, 0), Usage = Usage.Default, BindFlags = (uint)BindFlag.DepthStencil };
            ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in hd, (SubresourceData*)null, &texture)); _heightDepth = texture;
            ID3D11DepthStencilView* dsv = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilView((ID3D11Resource*)texture, (DepthStencilViewDesc*)null, &dsv)); _heightDsv = dsv;
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Validates the full batch before writes. Captures only static native-definition parts,
    /// using the native 512-world-unit top-down region, two-unit snapped origin and two hole expansions.</summary>
    public bool Prepare(SharedRenderTarget opaque, Camera camera, IEnumerable<GpuMesh> definitions, out string? refusal)
    {
        refusal = null;
        Ready = false; DefinitionPartsDrawn = 0; CameraConstants = null;
        if (_disposed) { refusal = "Foam context is disposed."; return false; }
        if (opaque.DepthTex.Handle == null) { refusal = "Opaque depth is absent."; return false; }
        Texture2DDesc source; opaque.DepthTex.GetDesc(&source);
        if (source.Format != Format.FormatD24UnormS8Uint || source.SampleDesc.Count != 1 || source.ArraySize != 1 || source.MipLevels != 1
            || source.Width != opaque.Width || source.Height != opaque.Height || source.Width == 0 || source.Height == 0 || source.Width > 16384 || source.Height > 16384)
        { refusal = "Foam depth requires the known single-sample D24S8 viewport."; return false; }
        var parts = new List<(GpuMesh Mesh, GpuPart Part)>(); float minimum = float.PositiveInfinity;
        Vector2 origin = new(MathF.Floor(camera.Position.X * .5f) * 2, MathF.Floor(camera.Position.Y * .5f) * 2);
        foreach (GpuMesh mesh in definitions)
        {
            if (!mesh.Visible || mesh.Ghost || mesh.Instanced || mesh.SkinBuffer.Handle != null || mesh.VertexBuffer.Handle == null || mesh.IndexBuffer.Handle == null) continue;
            var selected = mesh.Parts.Where(p => p.Surface?.NativeWaterDefinition is not null && p.IndexCount != 0).ToArray();
            if (selected.Length == 0) continue;
            if (!Finite(mesh.World) || !Finite(mesh.BoundsMin) || !Finite(mesh.BoundsMax)
                || mesh.World.M14 != 0 || mesh.World.M24 != 0 || mesh.World.M34 != 0 || mesh.World.M44 != 1)
            { refusal = "A definition requires finite affine placement/bounds."; return false; }
            // GpuMesh bounds are already world-space (upload transforms the source bounds).
            Vector3 min = mesh.BoundsMin, max = mesh.BoundsMax;
            if (min.X > max.X || min.Y > max.Y || min.Z > max.Z || min.Z < -65504 || max.Z > 65504)
            { refusal = "Definition bounds exceed the known finite R16 height range."; return false; }
            if (!Finite(min) || !Finite(max)) { refusal = "A definition world bound exceeds finite range."; return false; }
            if ((max.X - min.X) * (max.Y - min.Y) <= 225 || max.X < origin.X - 256 || min.X > origin.X + 256 || max.Y < origin.Y - 256 || min.Y > origin.Y + 256) continue;
            BufferDesc ib; mesh.IndexBuffer.GetDesc(&ib);
            uint indexBytes = ib.ByteWidth;
            if (selected.Any(p => ((ulong)p.StartIndex + p.IndexCount) * 4 > indexBytes)) { refusal = "A definition index range exceeds its buffer."; return false; }
            minimum = MathF.Min(minimum, min.Z); foreach (GpuPart part in selected) parts.Add((mesh, part));
        }
        if (parts.Count == 0) { refusal = "No native water definitions overlap the height-capture region."; return false; }
        NativeWaterFoamCamera adapter;
        try { adapter = new(camera, opaque.Width, opaque.Height, minimum); }
        catch (ArgumentException ex) { refusal = ex.Message; return false; }
        EnsureDepth((int)source.Width, (int)source.Height);
        var ctx = _gpu.Context11;
        Unbind(); ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        ctx.CopyResource((ID3D11Resource*)_copy.Handle, (ID3D11Resource*)opaque.DepthTex.Handle);
        try
        {
            ctx.RSSetState(_raster.Handle); ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, uint.MaxValue);
            ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
            var depthData = new Vector4(adapter.Near, adapter.Far, 0, 0);
            ctx.UpdateSubresource((ID3D11Resource*)_depthConstants.Handle, 0, (Box*)null, &depthData, 0, 0);
            var buffer = _depthConstants.Handle; ctx.PSSetConstantBuffers(0, 1, &buffer);
            Fullscreen(_linear!, _linearize, _copyView);
            var viewport = new Viewport(0, 0, 256, 256, 0, 1); ctx.RSSetViewports(1, &viewport);
            ctx.IASetInputLayout(_layout.Handle); ctx.VSSetShader(_heightVertex.Handle, null, 0); ctx.PSSetShader(_heightPixel.Handle, null, 0);
            ctx.OMSetDepthStencilState(_depthState.Handle, 0); var rt = _height[0]!.View.Handle;
            ctx.OMSetRenderTargets(1, &rt, _heightDsv.Handle);
            float* clear = stackalloc float[4] { -65504, -65504, -65504, -65504 }; ctx.ClearRenderTargetView(rt, clear);
            ctx.ClearDepthStencilView(_heightDsv.Handle, (uint)ClearFlag.Depth, 1, 0);
            buffer = _heightConstants.Handle; ctx.VSSetConstantBuffers(0, 1, &buffer);
            foreach (var row in parts)
            {
                var data = new HeightConstants { World = row.Mesh.World, ViewProjection = adapter.HeightViewProjection };
                ctx.UpdateSubresource((ID3D11Resource*)_heightConstants.Handle, 0, (Box*)null, &data, 0, 0);
                var vb = row.Mesh.VertexBuffer.Handle; uint stride = (uint)sizeof(MeshVertex), offset = 0;
                ctx.IASetVertexBuffers(0, 1, &vb, &stride, &offset); ctx.IASetIndexBuffer(row.Mesh.IndexBuffer.Handle, Format.FormatR32Uint, 0);
                ctx.DrawIndexed(row.Part.IndexCount, row.Part.StartIndex, 0);
            }
            ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
            Fullscreen(_height[1]!, _expand, _height[0]!.Resource);
            Fullscreen(_height[0]!, _expand, _height[1]!.Resource);
            CameraConstants = adapter; DefinitionPartsDrawn = parts.Count; Ready = true; return true;
        }
        finally { Unbind(); ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null); }
    }

    private struct HeightConstants { public Matrix4x4 World, ViewProjection; }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static bool Finite(Matrix4x4 p) => Finite(new Vector3(p.M11, p.M12, p.M13)) && float.IsFinite(p.M14)
        && Finite(new Vector3(p.M21, p.M22, p.M23)) && float.IsFinite(p.M24) && Finite(new Vector3(p.M31, p.M32, p.M33))
        && float.IsFinite(p.M34) && Finite(new Vector3(p.M41, p.M42, p.M43)) && float.IsFinite(p.M44);
    private void Unbind() { ID3D11ShaderResourceView* view = null; _gpu.Context11.PSSetShaderResources(0, 1, &view); }
    private void Fullscreen(Field output, ComPtr<ID3D11PixelShader> shader, ComPtr<ID3D11ShaderResourceView> input)
    {
        var ctx = _gpu.Context11; Unbind(); ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        var vp = new Viewport(0, 0, output.Width, output.Height, 0, 1); ctx.RSSetViewports(1, &vp);
        ctx.IASetInputLayout((ID3D11InputLayout*)null); ctx.VSSetShader(_fullscreen.Handle, null, 0); ctx.PSSetShader(shader.Handle, null, 0);
        ctx.OMSetDepthStencilState((ID3D11DepthStencilState*)null, 0);
        var sampler = _point.Handle; ctx.PSSetSamplers(0, 1, &sampler); var srv = input.Handle; ctx.PSSetShaderResources(0, 1, &srv);
        var rt = output.View.Handle; ctx.OMSetRenderTargets(1, &rt, (ID3D11DepthStencilView*)null); ctx.Draw(3, 0); Unbind();
    }
    private void EnsureDepth(int width, int height)
    {
        if (_linear is { } old && old.Width == width && old.Height == height) return;
        _linear?.Dispose(); _copyView.Dispose(); _copy.Dispose();
        _linear = new(_gpu, width, height, Format.FormatR32Float);
        var desc = new Texture2DDesc { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.FormatR24G8Typeless, SampleDesc = new(1, 0), Usage = Usage.Default, BindFlags = (uint)BindFlag.ShaderResource };
        ID3D11Texture2D* t = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &t)); _copy = t;
        var sd = new ShaderResourceViewDesc { Format = Format.FormatR24UnormX8Typeless, ViewDimension = D3DSrvDimension.D3DSrvDimensionTexture2D };
        sd.Texture2D.MipLevels = 1;
        ID3D11ShaderResourceView* v = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateShaderResourceView((ID3D11Resource*)t, in sd, &v)); _copyView = v;
    }
    private ComPtr<ID3D11PixelShader> Pixel(D3DCompiler compiler, string source)
    {
        using var code = ShaderCompiler.Compile(compiler, source, "main", "ps_5_0", "foam-context");
        ID3D11PixelShader* shader = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreatePixelShader(code.GetBufferPointer(), code.GetBufferSize(), (ID3D11ClassLinkage*)null, &shader)); return shader;
    }
    private ComPtr<ID3D11Buffer> Buffer(uint size)
    {
        var desc = new BufferDesc { ByteWidth = size, Usage = Usage.Default, BindFlags = (uint)BindFlag.ConstantBuffer };
        ID3D11Buffer* b = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateBuffer(in desc, (SubresourceData*)null, &b)); return b;
    }
    private sealed class Field : IDisposable
    {
        public readonly int Width, Height;
        public ComPtr<ID3D11Texture2D> Texture;
        public ComPtr<ID3D11RenderTargetView> View;
        public ComPtr<ID3D11ShaderResourceView> Resource;
        public Field(GpuContext gpu, int width, int height, Format format)
        {
            Width = width; Height = height;
            try
            {
                var desc = new Texture2DDesc { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = format, SampleDesc = new(1, 0), Usage = Usage.Default, BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource) };
                ID3D11Texture2D* t = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &t)); Texture = t;
                ID3D11RenderTargetView* v = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRenderTargetView((ID3D11Resource*)t, (RenderTargetViewDesc*)null, &v)); View = v;
                ID3D11ShaderResourceView* s = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)t, (ShaderResourceViewDesc*)null, &s)); Resource = s;
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { Resource.Dispose(); View.Dispose(); Texture.Dispose(); }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; Ready = false; CameraConstants = null;
        _linear?.Dispose(); foreach (var f in _height) f?.Dispose(); _copyView.Dispose(); _copy.Dispose(); _heightDsv.Dispose(); _heightDepth.Dispose();
        _point.Dispose(); _depthState.Dispose(); _raster.Dispose(); _depthConstants.Dispose(); _heightConstants.Dispose();
        _layout.Dispose(); _fullscreen.Dispose(); _heightVertex.Dispose(); _linearize.Dispose(); _heightPixel.Dispose(); _expand.Dispose();
    }
}
