using System.Buffers.Binary;
using Illusion.Rendering.Textures;
using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Scene;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

/// <summary>Three-stage analytical water surface: definition UV raster, native analytical field,
/// screen-ray grid. User-owned lookup/material inputs only. Toolkit lighting; no dynamic
/// ripples, height/shoreline simulation, SSR, refraction or foam.</summary>
public sealed unsafe class NativeWaterSurface : IDisposable
{
    private const int FieldSize = 256;
    private const int ConstantFloats = 212;
    private readonly GpuContext _gpu;
    private readonly float[] _values = new float[ConstantFloats];
    private ComPtr<ID3D11Buffer> _constants, _vertices, _indices;
    private ComPtr<ID3D11VertexShader> _definitionVs, _gridVs;
    private ComPtr<ID3D11PixelShader> _definitionPs, _wavePs, _surfacePs;
    private ComPtr<ID3D11InputLayout> _definitionLayout, _gridLayout;
    private ComPtr<ID3D11ShaderResourceView> _lookup, _fineNoise;
    private float _fineScale, _fineIntensity;
    private bool _fineParametersValid;
    public float FineNormalScale => _fineScale;
    public float FineNormalIntensity => _fineIntensity;

    /// <summary>Refreshes native material parameters without recreating any GPU assets.
    /// Nonfinite values disable detail; later finite values restore an existing lookup.</summary>
    public bool SetFineNormalParameters(float scale, float intensity)
    {
        _fineParametersValid = float.IsFinite(scale) && float.IsFinite(intensity);
        if (_fineParametersValid) { _fineScale = scale; _fineIntensity = intensity; }
        return FineNormalsEnabled;
    }
    public bool FineNormalsConfigured => FineNormalsEnabled;
    public bool FineNormalsEnabled => !_disposed && _fineNoise.Handle != null && _fineParametersValid;

    /// <summary>Optional native RGBA8 256-square variance lookup, all eight authored mips.
    /// Invalid or missing inputs disable detail and retain analytical normals.</summary>
    public bool ConfigureFineNormals(ReadOnlyMemory<byte>? authoredDds, float scale, float intensity)
    {
        _fineNoise.Dispose(); _fineNoise = default;
        SetFineNormalParameters(scale, intensity);
        if (_disposed || authoredDds is not { } data || data.Length != 349648
            || !float.IsFinite(scale) || !float.IsFinite(intensity)) return false;
        uint U(int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.Span.Slice(at, 4));
        if (U(0) != 0x20534444 || U(12) != 256 || U(16) != 256 || U(28) != 8
            || U(84) != 0 || U(88) != 32 || U(92) != 0xff || U(96) != 0xff00
            || U(100) != 0xff0000 || U(104) != 0xff000000) return false;
        _fineNoise = DdsTexture.LoadMipmapped(_gpu, data.ToArray());
        return FineNormalsEnabled;
    }
    private ComPtr<ID3D11SamplerState> _wrapped, _pointClamp, _linearClamp;
    private ComPtr<ID3D11RasterizerState> _raster;
    private ComPtr<ID3D11DepthStencilState> _depthState;
    private ComPtr<ID3D11Texture2D> _depthTexture;
    private ComPtr<ID3D11DepthStencilView> _depthView;
    private FieldTarget? _mask, _color, _displacement, _normal;
    private bool _disposed;
    public LightingConstants Lighting { get; set; } = LightingConstants.Default;
    public bool DebugNormals { get; set; }
    public bool ShowNormals { get => DebugNormals; set => DebugNormals = value; }
    public const string Description = "Native analytical waves, camera-projected definition masks and screen-ray surface; toolkit lighting. Dynamic ripples/height, SSR, refraction and foam are absent.";

    public NativeWaterSurface(GpuContext gpu, IReadOnlyList<ReadOnlyMemory<byte>> lookupMips)
    {
        _gpu = gpu;
        try
        {
            CreateShaders(); CreateStates(); CreateGrid(); CreateLookup(lookupMips);
            var buffer = new BufferDesc { ByteWidth = ConstantFloats * 4, Usage = Usage.Default, BindFlags = (uint)BindFlag.ConstantBuffer };
            ID3D11Buffer* constants = null;
            SilkMarshal.ThrowHResult(gpu.Device11.CreateBuffer(in buffer, (SubresourceData*)null, &constants)); _constants = constants;
            _mask = new(gpu, Format.FormatR8G8B8A8Unorm); _color = new(gpu, Format.FormatR8G8B8A8Unorm);
            _displacement = new(gpu, Format.FormatR32G32B32A32Float); _normal = new(gpu, Format.FormatR32G32B32A32Float);
            var depth = new Texture2DDesc { Width = FieldSize, Height = FieldSize, MipLevels = 1, ArraySize = 1,
                Format = Format.FormatD24UnormS8Uint, SampleDesc = new(1, 0), Usage = Usage.Default, BindFlags = (uint)BindFlag.DepthStencil };
            ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in depth, (SubresourceData*)null, &texture)); _depthTexture = texture;
            ID3D11DepthStencilView* view = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilView((ID3D11Resource*)texture, (DepthStencilViewDesc*)null, &view)); _depthView = view;
        }
        catch { Dispose(); throw; }
    }

    public static bool Supports(GpuMesh mesh, GpuPart part) => mesh.RenderVisible && !mesh.Ghost && !mesh.Instanced
        && mesh.SkinBuffer.Handle == null && mesh.VertexBuffer.Handle != null && mesh.IndexBuffer.Handle != null
        && part.IndexCount > 0 && part.Surface?.NativeWaterDefinition is not null
        && part.SurfaceSrvs is { Length: >= 2 } views && views[0].Handle != null && views[1].Handle != null;
    public static bool Eligible(GpuMesh mesh, GpuPart part) => Supports(mesh, part);
    public static bool Eligible(GpuMesh mesh) => mesh.Parts.Any(part => Supports(mesh, part));

    public bool CanRender(Camera camera, ReadOnlySpan<float> packed, RenderMode mode)
    {
        if (_disposed || camera.Orthographic || mode == RenderMode.Wireframe || packed.Length != 128
            || !float.IsFinite(camera.Fov) || camera.Fov <= 0 || camera.Fov >= MathF.PI
            || !float.IsFinite(camera.AspectRatio) || camera.AspectRatio <= 0
            || !float.IsFinite(camera.Near) || !float.IsFinite(camera.Far) || camera.Near <= 0 || camera.Far <= camera.Near
            || !float.IsFinite(camera.Yaw) || !float.IsFinite(camera.Pitch)
            || !float.IsFinite(camera.Position.X) || !float.IsFinite(camera.Position.Y) || !float.IsFinite(camera.Position.Z)) return false;
        foreach (float value in packed) if (!float.IsFinite(value)) return false;
        return true;
    }
    public bool CanRender(Camera camera, IEnumerable<GpuMesh> definitions, ReadOnlySpan<float> packed, RenderMode mode)
        => CanRender(camera, packed, mode) && definitions.Any(Eligible);

    /// <returns>Actual GPU draw calls (two per supported definition part plus the final grid).</returns>
    public int Render(SharedRenderTarget target, IEnumerable<GpuMesh> definitions, ReadOnlySpan<float> packed,
        Camera camera, float time, Vector3 lightDir, RenderMode mode)
    {
        if (!CanRender(camera, packed, mode) || !float.IsFinite(time)) return 0;
        var draws = definitions.SelectMany(mesh => mesh.Parts.Where(part => Supports(mesh, part)).Select(part => (Mesh: mesh, Part: part))).ToArray();
        if (draws.Length == 0) return 0;
        var ctx = _gpu.Context11;
        var mainViewport = new Viewport(0, 0, target.Width, target.Height, 0, 1);
        var resources = stackalloc ID3D11ShaderResourceView*[5];
        for (int i = 0; i < 5; i++) resources[i] = null;
        var renderTargets = stackalloc ID3D11RenderTargetView*[2];
        var samplers = stackalloc ID3D11SamplerState*[3]; samplers[0] = _wrapped.Handle; samplers[1] = _pointClamp.Handle; samplers[2] = _linearClamp.Handle;
        var nullViews = stackalloc ID3D11ShaderResourceView*[5]; for (int i = 0; i < 5; i++) nullViews[i] = null;
        float* clear = stackalloc float[4]; for (int i = 0; i < 4; i++) clear[i] = 0;
        _values.AsSpan().Clear(); SetMatrix(16, camera.ViewProjection);
        SetVector(32, new(camera.Position, time));
        float tanY = MathF.Tan(camera.Fov * .5f), tanX = tanY * camera.AspectRatio;
        SetVector(36, new(camera.Right, tanX)); SetVector(40, new(camera.Forward, tanY));
        SetVector(44, new(Vector3.Cross(camera.Right, camera.Forward), 0));
        if (!float.IsFinite(lightDir.X) || !float.IsFinite(lightDir.Y) || !float.IsFinite(lightDir.Z) || lightDir.LengthSquared() < 1e-12f) lightDir = new(0, 0, -1);
        SetVector(48, new(Vector3.Normalize(lightDir), (float)mode));
        SetVector(52, new(1f / FieldSize, 1f / FieldSize, DebugNormals ? 1 : 0, 0));
        SetVector(56, new(1f / target.Width, 1f / target.Height, 0, 0)); packed.CopyTo(_values.AsSpan(60));
        SetVector(188, Lighting.SunColor); SetVector(192, Lighting.AmbientUp); SetVector(196, Lighting.AmbientDown);
        SetVector(200, Lighting.SpecParams); SetVector(204, Lighting.Gamma);
        SetVector(208, new(_fineScale, _fineIntensity, FineNormalsEnabled ? 1 : 0, 0));
        try
        {
            ctx.VSSetShaderResources(0, 5, nullViews); ctx.PSSetShaderResources(0, 5, nullViews);
            ctx.RSSetState(_raster.Handle); ctx.OMSetBlendState((ID3D11BlendState*)null, clear, uint.MaxValue);
            ctx.OMSetDepthStencilState(_depthState.Handle, 0);
            ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
            var cb = _constants.Handle; ctx.VSSetConstantBuffers(0, 1, &cb); ctx.PSSetConstantBuffers(0, 1, &cb);
            ctx.PSSetSamplers(0, 3, samplers); ctx.VSSetSamplers(0, 3, samplers);
            var fieldViewport = new Viewport(0, 0, FieldSize, FieldSize, 0, 1); ctx.RSSetViewports(1, &fieldViewport);
            ctx.IASetInputLayout(_definitionLayout.Handle); ctx.VSSetShader(_definitionVs.Handle, null, 0);
            renderTargets[0] = _mask!.View.Handle; renderTargets[1] = _color!.View.Handle;
            ctx.OMSetRenderTargets(2, renderTargets, _depthView.Handle);
            ctx.ClearRenderTargetView(renderTargets[0], clear); ctx.ClearRenderTargetView(renderTargets[1], clear);
            ctx.ClearDepthStencilView(_depthView.Handle, (uint)ClearFlag.Depth, 1, 0);
            ctx.PSSetShader(_definitionPs.Handle, null, 0);
            foreach (var draw in draws)
            {
                SetMatrix(0, draw.Mesh.World); Upload(); BindMesh(draw.Mesh);
                resources[0] = draw.Part.SurfaceSrvs![0].Handle; resources[1] = draw.Part.SurfaceSrvs[1].Handle;
                resources[2] = null; resources[3] = null; ctx.PSSetShaderResources(0, 5, resources);
                ctx.DrawIndexed(draw.Part.IndexCount, draw.Part.StartIndex, 0);
            }
            ctx.PSSetShaderResources(0, 5, nullViews);
            renderTargets[0] = _displacement!.View.Handle; renderTargets[1] = _normal!.View.Handle;
            ctx.OMSetRenderTargets(2, renderTargets, _depthView.Handle);
            ctx.ClearRenderTargetView(renderTargets[0], clear); ctx.ClearRenderTargetView(renderTargets[1], clear);
            ctx.ClearDepthStencilView(_depthView.Handle, (uint)ClearFlag.Depth, 1, 0);
            resources[0] = null; resources[1] = _mask.Resource.Handle; resources[2] = null; resources[3] = _lookup.Handle;
            ctx.PSSetShaderResources(0, 5, resources); ctx.PSSetShader(_wavePs.Handle, null, 0);
            foreach (var draw in draws)
            {
                SetMatrix(0, draw.Mesh.World); Upload(); BindMesh(draw.Mesh);
                ctx.DrawIndexed(draw.Part.IndexCount, draw.Part.StartIndex, 0);
            }
            ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
            ctx.PSSetShaderResources(0, 5, nullViews);
            var main = target.Rtv.Handle; ctx.OMSetRenderTargets(1, &main, target.Dsv.Handle); ctx.RSSetViewports(1, &mainViewport);
            resources[0] = _displacement.Resource.Handle; resources[1] = _normal.Resource.Handle; resources[2] = null; resources[3] = null;
            ctx.VSSetShaderResources(0, 5, resources); resources[0] = null; resources[1] = null; resources[2] = _color.Resource.Handle; resources[4] = _fineNoise.Handle;
            ctx.PSSetShaderResources(0, 5, resources);
            ctx.IASetInputLayout(_gridLayout.Handle); ctx.VSSetShader(_gridVs.Handle, null, 0); ctx.PSSetShader(_surfacePs.Handle, null, 0);
            var vertex = _vertices.Handle; uint stride = 4, offset = 0;
            ctx.IASetVertexBuffers(0, 1, &vertex, &stride, &offset); ctx.IASetIndexBuffer(_indices.Handle, Format.FormatR32Uint, 0);
            ctx.DrawIndexed(257 * 257 * 6, 0, 0);
            return draws.Length * 2 + 1;
        }
        finally
        {
            ctx.VSSetShaderResources(0, 5, nullViews); ctx.PSSetShaderResources(0, 5, nullViews);
            var main = target.Rtv.Handle; ctx.OMSetRenderTargets(1, &main, target.Dsv.Handle); ctx.RSSetViewports(1, &mainViewport);
        }
    }

    private void SetVector(int at, Vector4 value) { _values[at] = value.X; _values[at + 1] = value.Y; _values[at + 2] = value.Z; _values[at + 3] = value.W; }
    private void SetMatrix(int at, Matrix4x4 value)
    {
        _values[at] = value.M11; _values[at + 1] = value.M12; _values[at + 2] = value.M13; _values[at + 3] = value.M14;
        _values[at + 4] = value.M21; _values[at + 5] = value.M22; _values[at + 6] = value.M23; _values[at + 7] = value.M24;
        _values[at + 8] = value.M31; _values[at + 9] = value.M32; _values[at + 10] = value.M33; _values[at + 11] = value.M34;
        _values[at + 12] = value.M41; _values[at + 13] = value.M42; _values[at + 14] = value.M43; _values[at + 15] = value.M44;
    }
    private void Upload() { fixed (float* data = _values) _gpu.Context11.UpdateSubresource((ID3D11Resource*)_constants.Handle, 0, (Box*)null, data, 0, 0); }
    private void BindMesh(GpuMesh mesh)
    {
        var vertex = mesh.VertexBuffer.Handle; uint stride = 56, offset = 0;
        _gpu.Context11.IASetVertexBuffers(0, 1, &vertex, &stride, &offset);
        _gpu.Context11.IASetIndexBuffer(mesh.IndexBuffer.Handle, Format.FormatR32Uint, 0);
    }

    private void CreateShaders()
    {
        using var compiler = D3DCompiler.GetApi();
        using var definition = ShaderCompiler.Compile(compiler, NativeWaterSurfaceShader.Source, "DefinitionVS", "vs_5_0", "native-water-definition");
        using var grid = ShaderCompiler.Compile(compiler, NativeWaterSurfaceShader.Source, "GridVS", "vs_5_0", "native-water-grid");
        ID3D11VertexShader* vertex = null;
        SilkMarshal.ThrowHResult(_gpu.Device11.CreateVertexShader(definition.GetBufferPointer(), definition.GetBufferSize(), (ID3D11ClassLinkage*)null, &vertex)); _definitionVs = vertex;
        vertex = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateVertexShader(grid.GetBufferPointer(), grid.GetBufferSize(), (ID3D11ClassLinkage*)null, &vertex)); _gridVs = vertex;
        ComPtr<ID3D11PixelShader> Pixel(string entry)
        {
            using var code = ShaderCompiler.Compile(compiler, NativeWaterSurfaceShader.Source, entry, "ps_5_0", "native-water-" + entry);
            ID3D11PixelShader* shader = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreatePixelShader(code.GetBufferPointer(), code.GetBufferSize(), (ID3D11ClassLinkage*)null, &shader)); return shader;
        }
        _definitionPs = Pixel("DefinitionPS"); _wavePs = Pixel("WavePS"); _surfacePs = Pixel("SurfacePS");
        string[] names = ["POSITION", "NORMAL", "TEXCOORD", "TANGENT", "BINORMAL"];
        int[] offsets = [0, 12, 24, 32, 44]; var elements = stackalloc InputElementDesc[5];
        var allocated = new nint[5];
        try
        {
            for (int i = 0; i < 5; i++) { allocated[i] = SilkMarshal.StringToPtr(names[i]); elements[i] = ShaderCompiler.VertexElement((byte*)allocated[i], 0, i == 2 ? Format.FormatR32G32Float : Format.FormatR32G32B32Float, (uint)offsets[i]); }
            ID3D11InputLayout* layout = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateInputLayout(elements, 5, definition.GetBufferPointer(), definition.GetBufferSize(), &layout)); _definitionLayout = layout;
            elements[0] = ShaderCompiler.VertexElement((byte*)allocated[0], 0, Format.FormatR8G8B8A8Uint, 0);
            layout = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateInputLayout(elements, 1, grid.GetBufferPointer(), grid.GetBufferSize(), &layout)); _gridLayout = layout;
        }
        finally { foreach (nint name in allocated) if (name != 0) SilkMarshal.Free(name); }
    }

    private void CreateStates()
    {
        ComPtr<ID3D11SamplerState> Sampler(Filter filter, TextureAddressMode address, uint anisotropy = 1)
        {
            var desc = new SamplerDesc { Filter = filter, AddressU = address, AddressV = address, AddressW = address, MaxAnisotropy = anisotropy, MaxLOD = float.MaxValue };
            ID3D11SamplerState* state = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateSamplerState(in desc, &state)); return state;
        }
        _wrapped = Sampler(Filter.Anisotropic, TextureAddressMode.Wrap, 16);
        _pointClamp = Sampler(Filter.MinMagMipPoint, TextureAddressMode.Clamp);
        _linearClamp = Sampler(Filter.MinMagMipLinear, TextureAddressMode.Clamp);
        var raster = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true };
        ID3D11RasterizerState* rs = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateRasterizerState(in raster, &rs)); _raster = rs;
        var depth = new DepthStencilDesc { DepthEnable = true, DepthWriteMask = DepthWriteMask.All, DepthFunc = ComparisonFunc.LessEqual };
        ID3D11DepthStencilState* ds = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateDepthStencilState(in depth, &ds)); _depthState = ds;
    }

    private void CreateGrid()
    {
        var vertices = new uint[258 * 258]; const float origin = -.0021982791367918253f, scale = 3.064427073695697e-5f;
        for (int y = 0; y < 258; y++) for (int x = 0; x < 258; x++)
        {
            ushort u = (ushort)(int)((((x - .5f) / 256f) - origin) * (1 / scale) + .5f);
            ushort v = (ushort)(int)((((y - .5f) / 256f) - origin) * (1 / scale) + .5f);
            vertices[y * 258 + x] = (uint)u | ((uint)v << 16);
        }
        var indices = new uint[257 * 257 * 6]; int at = 0;
        for (int y = 0; y < 257; y++) for (int x = 0; x < 257; x++)
        { uint i = (uint)(y * 258 + x); indices[at++] = i + 1; indices[at++] = i; indices[at++] = i + 258; indices[at++] = i + 258; indices[at++] = i + 259; indices[at++] = i + 1; }
        fixed (uint* data = vertices) _vertices = GpuBuffers.CreateImmutable(_gpu, data, (uint)(vertices.Length * 4), BindFlag.VertexBuffer);
        fixed (uint* data = indices) _indices = GpuBuffers.CreateImmutable(_gpu, data, (uint)(indices.Length * 4), BindFlag.IndexBuffer);
    }

    private void CreateLookup(IReadOnlyList<ReadOnlyMemory<byte>> mips)
    {
        if (mips.Count < 1 || mips.Count > 7) throw new ArgumentException("Provide validated native sincos lookup mips.", nameof(mips));
        for (int i = 0; i < mips.Count; i++) if (mips[i].Length != Math.Max(1, 64 >> i) * 2) throw new ArgumentException("Invalid native sincos lookup mip size.", nameof(mips));
        var bytes = mips.SelectMany(m => m.ToArray()).ToArray();
        fixed (byte* data = bytes)
        {
            var initial = stackalloc SubresourceData[mips.Count]; int offset = 0;
            for (int i = 0; i < mips.Count; i++) { initial[i] = new() { PSysMem = data + offset, SysMemPitch = (uint)Math.Max(1, 64 >> i) * 2 }; offset += mips[i].Length; }
            var desc = new Texture2DDesc { Width = 64, Height = 1, MipLevels = (uint)mips.Count, ArraySize = 1, Format = Format.FormatR8G8Unorm, SampleDesc = new(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource };
            ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateTexture2D(in desc, initial, &texture));
            try { ID3D11ShaderResourceView* view = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, &view)); _lookup = view; }
            finally { texture->Release(); }
        }
    }

    private sealed class FieldTarget : IDisposable
    {
        public ComPtr<ID3D11Texture2D> Texture;
        public ComPtr<ID3D11RenderTargetView> View;
        public ComPtr<ID3D11ShaderResourceView> Resource;
        public FieldTarget(GpuContext gpu, Format format)
        {
            try
            {
                var desc = new Texture2DDesc { Width = FieldSize, Height = FieldSize, MipLevels = 1, ArraySize = 1, Format = format, SampleDesc = new(1, 0), Usage = Usage.Default, BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource) };
                ID3D11Texture2D* texture = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &texture)); Texture = texture;
                ID3D11RenderTargetView* view = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRenderTargetView((ID3D11Resource*)texture, (RenderTargetViewDesc*)null, &view)); View = view;
                ID3D11ShaderResourceView* resource = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, &resource)); Resource = resource;
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { Resource.Dispose(); View.Dispose(); Texture.Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _mask?.Dispose(); _color?.Dispose(); _displacement?.Dispose(); _normal?.Dispose(); _depthView.Dispose(); _depthTexture.Dispose();
        _constants.Dispose(); _vertices.Dispose(); _indices.Dispose(); _lookup.Dispose(); _fineNoise.Dispose(); _definitionLayout.Dispose(); _gridLayout.Dispose();
        _definitionVs.Dispose(); _gridVs.Dispose(); _definitionPs.Dispose(); _wavePs.Dispose(); _surfacePs.Dispose();
        _wrapped.Dispose(); _pointClamp.Dispose(); _linearClamp.Dispose(); _raster.Dispose(); _depthState.Dispose();
    }
}
