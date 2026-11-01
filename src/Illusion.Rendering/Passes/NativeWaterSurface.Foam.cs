using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Scene;
using Illusion.Rendering.Textures;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Passes;

public sealed unsafe partial class NativeWaterSurface
{
    private NativeWaterFoam? _foam;
    private NativeWaterFoamContext? _foamContext;
    private ComPtr<ID3D11ShaderResourceView> _foamDetail, _foamNormal;
    private ComPtr<ID3D11SamplerState> _foamBlur;
    private byte[]? _foamDetailBytes, _foamNormalBytes;
    private NativeWaterFoamParameters _foamParameters;
    private Vector4 _foamColor, _foamDetailParameters;
    private Vector2 _foamOrigin;
    private readonly float[] _foamGlobals = new float[220];
    private int _foamMode, _foamTarget, _foamSignature;
    private bool _foamHasSignature;
    private bool _foamInputsDirty;
    private readonly record struct FoamCamera(Vector3 Position, float Yaw, float Pitch, float Fov, float Near, float Far, float Aspect, bool Orthographic);
    private FoamCamera _foamCamera;
    private int _foamWidth, _foamHeight;
    private static FoamCamera View(Camera camera) => new(camera.Position, camera.Yaw, camera.Pitch, camera.Fov, camera.Near, camera.Far, camera.AspectRatio, camera.Orthographic);
    private long _foamSourceRevision, _foamConsumedRevision = -1;
    private uint _foamSeed = 1;
    public bool FoamEnabled { get; private set; }
    public bool FoamConfigured => !_foamInputsDirty && _foamDetailBytes is not null && _foamNormalBytes is not null && FineNormalsConfigured && _foamDetailParameters.W >= 0;
    public bool FoamReady => FoamEnabled && FoamConfigured && _foam is not null && _foam.Frames == _foamTarget
        && _foamConsumedRevision == _foamSourceRevision && FoamError is null;
    public int FoamFrame => (int)(_foam?.Frames ?? 0);
    public int FoamTargetFrame => _foamTarget;
    public uint FoamSeed => _foamSeed;
    public string FoamMode => _foamMode switch { 1 => "normals", 2 => "roughness", 3 => "surface", _ => "diffuse" };
    public string? FoamError { get; private set; }
    /// <summary>CPU-only invalidation for opaque geometry/material edits, undo/redo and streaming.
    /// Many notifications coalesce into one reset on the next bounded render update.</summary>
    public void InvalidateFoamSource() => _foamSourceRevision = unchecked(_foamSourceRevision + 1);
    public void InvalidateFoamInputs() { _foamInputsDirty = true; InvalidateFoamSource(); }
    public bool FoamReadyFor(Camera camera) => FoamReady && _foamCamera == View(camera);

    public bool ConfigureFoam(byte[]? detail, byte[]? normal, NativeWaterFoamParameters parameters, Vector4 color, Vector4 controls)
    {
        _foamInputsDirty = true;
        float[] values = [color.X, color.Y, color.Z, color.W, controls.X, controls.Y, controls.Z, controls.W,
            parameters.Breaking.X, parameters.Breaking.Y, parameters.Breaking.Z, parameters.Noise.X,
            parameters.Noise.Y, parameters.Noise.Z, parameters.Noise.W, parameters.Amplitude, parameters.Decay.X, parameters.Decay.Y];
        if (detail is null || normal is null || values.Any(v => !float.IsFinite(v)) || controls.W < 0
            || parameters.Breaking.X <= 0 || parameters.Noise.X <= 0 || parameters.Decay.X <= 0 || parameters.Decay.Y <= 0)
        { FoamError = "Native foam requires finite material parameters and both authored T000/T001 textures."; return false; }
        bool sameTextures = _foamDetailBytes is not null && _foamNormalBytes is not null
            && detail.AsSpan().SequenceEqual(_foamDetailBytes) && normal.AsSpan().SequenceEqual(_foamNormalBytes);
        if (!sameTextures)
        {
            var a = DdsTexture.LoadMipmapped(_gpu, detail); var b = DdsTexture.LoadMipmapped(_gpu, normal);
            if (a.Handle == null || b.Handle == null)
            {
                a.Dispose(); b.Dispose(); FoamError = "Native foam DDS inputs are unsupported or incomplete; no white fallback is used."; return false;
            }
            _foamDetail.Dispose(); _foamNormal.Dispose(); _foamDetail = a; _foamNormal = b;
            _foamDetailBytes = (byte[])detail.Clone(); _foamNormalBytes = (byte[])normal.Clone();
        }
        bool changed = !sameTextures || _foamParameters != parameters || _foamColor != color || _foamDetailParameters != controls;
        if (changed)
        {
            _foam?.SetParameters(parameters); InvalidateFoamSource(); _foamHasSignature = false;
        }
        _foamParameters = parameters; _foamColor = color; _foamDetailParameters = controls; _foamInputsDirty = false; FoamError = null; return true;
    }

    public string? SetFoam(bool? enabled, string? mode, int? frame, uint? seed, bool reset)
        => JointContactEnabled ? SetJointContactClock(enabled, mode, frame, seed, reset) : SetFoamCore(enabled, mode, frame, seed, reset);

    private string? SetFoamCore(bool? enabled, string? mode, int? frame, uint? seed, bool reset)
    {
        if (frame is < 0 or > 36000) return "Foam frame must be in 0–36000; replay is fixed 1/60 seconds and advances at most four steps per draw.";
        int selected = mode switch { null => _foamMode, "diffuse" => 0, "normals" => 1, "roughness" => 2, "surface" => 3, _ => -1 };
        if (selected < 0) return "Foam mode must be diffuse, normals, roughness or surface.";
        if ((enabled == true || (enabled != false && frame is not null)) && !FoamConfigured) return FoamError ?? "Native foam inputs are unavailable.";
        _foamMode = selected;
        if (seed is { } s && s != _foamSeed) { _foamSeed = s; reset = true; }
        if (frame is { } f) { if (f < FoamFrame) reset = true; _foamTarget = f; }
        if (reset) { _foam?.Reset(_foamSeed); _foamHasSignature = false; FoamError = null; }
        if (enabled is { } e) FoamEnabled = e;
        return null;
    }

    private int AdvanceFoam(SharedRenderTarget target, Camera camera, (GpuMesh Mesh, GpuPart Part)[] draws, Func<float, int> field)
    {
        if (!FoamEnabled) return 0;
        if (!FoamConfigured) { FoamError ??= "Native foam inputs are unavailable."; return 0; }
        _foam ??= new(_gpu, _foamParameters); _foamContext ??= new(_gpu);
        if (_foamDetail.Handle == null) _foamDetail = DdsTexture.LoadMipmapped(_gpu, _foamDetailBytes!);
        if (_foamNormal.Handle == null) _foamNormal = DdsTexture.LoadMipmapped(_gpu, _foamNormalBytes!);
        if (_foamBlur.Handle == null)
        {
            // Native C_PPShaderParamsCtx constructor102747120: min/mag2, mip1, UVW2.
            // C_D3D11SamplerState102612FE0 maps these to linear/point-mip and mirror.
            var desc = new SamplerDesc { Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Mirror,
                AddressV = TextureAddressMode.Mirror, AddressW = TextureAddressMode.Mirror, MaxLOD = float.MaxValue };
            ID3D11SamplerState* state = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateSamplerState(in desc, &state)); _foamBlur = state;
        }
        var signature = new HashCode();
        foreach (var d in draws)
        {
            signature.Add(d.Mesh.World); signature.Add((nint)d.Mesh.VertexBuffer.Handle); signature.Add((nint)d.Mesh.IndexBuffer.Handle);
            signature.Add(d.Part.MaterialHash); signature.Add(d.Part.StartIndex); signature.Add(d.Part.IndexCount);
            if (d.Part.SurfaceSrvs is { } views) foreach (var view in views) signature.Add((nint)view.Handle);
        }
        int current = signature.ToHashCode();
        var cameraView = View(camera);
        if (!_foamHasSignature || _foamSignature != current || _foam.Frames > _foamTarget || _foamConsumedRevision != _foamSourceRevision
            || _foamCamera != cameraView || _foamWidth != target.Width || _foamHeight != target.Height)
        {
            _foam.Reset(_foamSeed); ResetJointContactReplay(); _foamSignature = current; _foamHasSignature = true;
            _foamConsumedRevision = _foamSourceRevision;
            _foamCamera = cameraView; _foamWidth = target.Width; _foamHeight = target.Height;
        }
        if (_foam.Frames == _foamTarget) { FoamError = null; PublishJointContactState(); return 0; }
        if (!_foamContext.Prepare(target, camera, draws.Select(d => d.Mesh).Distinct(), out string? refusal)) { FoamError = refusal; return 0; }
        int calls = _foamContext.DefinitionPartsDrawn + 3;
        for (int step = 0; step < 4 && _foam.Frames < _foamTarget; step++)
        {
            if (!BeginJointContactStep((int)_foam.Frames + 1)) return calls;
            try
            {
                calls += field((_foam.Frames + 1) / 60f);
                var input = new NativeWaterFoamInputs(_foamContext.CameraConstants!.ViewConstants, _foamGlobals,
                    _foamContext.LinearDepth, _foamContext.Height, _displacement!.Resource, _foamBlur, JointContactFoam);
                if (_jointContactPreview is { } joint) { _foamGlobals[140] = joint.Origin.X; _foamGlobals[141] = joint.Origin.Y; }
                if (!_foam.Step(1f / 60, input, out refusal)) { FoamError = refusal; return calls; }
                _foamOrigin = new(camera.Position.X, camera.Position.Y); calls += 4;
            }
            finally { EndJointContactStep(); }
        }
        FoamError = null; PublishJointContactState(); return calls;
    }

    private int DrawFoamAnalytical((GpuMesh Mesh, GpuPart Part)[] draws, float time)
    {
        var ctx = _gpu.Context11; SetVector(32, new(_values[32], _values[33], _values[34], time));
        SetVector(236, ContactShaderState);
        ID3D11ShaderResourceView** empty = stackalloc ID3D11ShaderResourceView*[9]; for (int i = 0; i < 9; i++) empty[i] = null;
        ctx.PSSetShaderResources(0, 9, empty); ctx.VSSetShaderResources(0, 9, empty);
        ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        var vp = new Viewport(0, 0, FieldSize, FieldSize, 0, 1); ctx.RSSetViewports(1, &vp); ctx.RSSetState(_raster.Handle);
        ctx.OMSetDepthStencilState(_depthState.Handle, 0); ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, uint.MaxValue);
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        var cb = _constants.Handle; ctx.VSSetConstantBuffers(0, 1, &cb); ctx.PSSetConstantBuffers(0, 1, &cb);
        ID3D11SamplerState** samplers = stackalloc ID3D11SamplerState*[3] { _wrapped.Handle, _pointClamp.Handle, _linearClamp.Handle }; ctx.PSSetSamplers(0, 3, samplers);
        ID3D11RenderTargetView** rt = stackalloc ID3D11RenderTargetView*[2] { _displacement!.View.Handle, _normal!.View.Handle };
        ctx.OMSetRenderTargets(2, rt, _depthView.Handle); float* clear = stackalloc float[4]; for (int i = 0; i < 4; i++) clear[i] = 0;
        ctx.ClearRenderTargetView(rt[0], clear); ctx.ClearRenderTargetView(rt[1], clear); ctx.ClearDepthStencilView(_depthView.Handle, (uint)ClearFlag.Depth, 1, 0);
        ID3D11ShaderResourceView** input = stackalloc ID3D11ShaderResourceView*[6]; for (int i = 0; i < 6; i++) input[i] = null;
        input[1] = _mask!.Resource.Handle; input[2] = ContactAnalyticalState.Handle; input[3] = _lookup.Handle; ctx.PSSetShaderResources(0, 6, input); BindContactSampler();
        ctx.IASetInputLayout(_definitionLayout.Handle); ctx.VSSetShader(_definitionVs.Handle, null, 0); ctx.PSSetShader(_wavePs.Handle, null, 0);
        foreach (var d in draws) { SetMatrix(0, d.Mesh.World); Upload(); BindMesh(d.Mesh); ctx.DrawIndexed(d.Part.IndexCount, d.Part.StartIndex, 0); }
        ctx.PSSetShaderResources(0, 9, empty); ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null); return draws.Length;
    }

    private void DisposeFoam()
    {
        _foam?.Dispose(); _foam = null; _foamContext?.Dispose(); _foamContext = null;
        _foamDetail.Dispose(); _foamDetail = default; _foamNormal.Dispose(); _foamNormal = default;
        _foamBlur.Dispose(); _foamBlur = default; _foamSignature = 0; _foamConsumedRevision = -1;
    }
}
