using System.Numerics;
using Illusion.Rendering.Scene;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

public sealed unsafe partial class NativeWaterSurface
{
    private FieldTarget? _reflectionCurrent, _reflectionRoughness, _reflectionDepth, _reflectionZero;
    private NativeReflectionTemporal[]? _reflectionTemporal;
    private ComPtr<ID3D11PixelShader> _reflectionCapture;
    private Matrix4x4 _reflectionPreviousProjection;
    private Vector3 _reflectionPreviousCamera;
    private Vector3 _reflectionPreviousForward;
    private float _reflectionPreviousFov, _reflectionPreviousAspect;
    private float _reflectionPreviousTime;
    private int _reflectionFrames;
    public bool TemporalReflectionsEnabled { get; set; }
    public bool TemporalReflectionsActive { get; private set; }
    public int TemporalReflectionFrames => _reflectionFrames;
    public static string TemporalReflectionScope => "256-square toolkit opaque-scene SSR radiance, native PS1403 temporal filter and R11G11B10/R8 storage; native water dielectric BRDF. No environment cubemap or offscreen geometry reflection.";

    public void ResetTemporalReflections() { _reflectionFrames = 0; TemporalReflectionsActive = false; }

    private bool AdvanceTemporalReflections(Camera camera, float time)
    {
        TemporalReflectionsActive = false;
        if (!TemporalReflectionsEnabled || !OpticsActive || !ReflectionsEnabled || DebugNormals ||
            (FoamEnabled && _foamMode != 3)) { ResetTemporalReflections(); return false; }
        if (_reflectionTemporal == null)
        {
            try
            {
                _reflectionCurrent = new(_gpu, Format.FormatR11G11B10Float);
                _reflectionRoughness = new(_gpu, Format.FormatR16Float);
                _reflectionDepth = new(_gpu, Format.FormatR32G32B32A32Float);
                _reflectionZero = new(_gpu, Format.FormatR32G32B32A32Float);
                _reflectionTemporal = [new(_gpu, FieldSize, true, true), new(_gpu, FieldSize, true, true)];
                using var compiler = D3DCompiler.GetApi();
                using var code = ShaderCompiler.Compile(compiler, NativeWaterSurfaceShader.Source, "ReflectionCapturePS", "ps_5_0", "native-water-reflection-capture");
                ID3D11PixelShader* shader = null;
                SilkMarshal.ThrowHResult(_gpu.Device11.CreatePixelShader(code.GetBufferPointer(), code.GetBufferSize(), (ID3D11ClassLinkage*)null, &shader));
                _reflectionCapture = shader;
                float* clear = stackalloc float[4]; for (int i = 0; i < 4; i++) clear[i] = 0;
                _gpu.Context11.ClearRenderTargetView(_reflectionZero.View.Handle, clear);
            }
            catch { DisposeTemporalReflections(); throw; }
        }
        if (_reflectionFrames > 0 && (time < _reflectionPreviousTime || time - _reflectionPreviousTime > .25f ||
            Vector3.DistanceSquared(camera.Position, _reflectionPreviousCamera) > 100 ||
            Vector3.Dot(camera.Forward, _reflectionPreviousForward) < .5f ||
            Math.Abs(camera.Fov - _reflectionPreviousFov) > .1f || Math.Abs(camera.AspectRatio - _reflectionPreviousAspect) > .1f))
            ResetTemporalReflections();
        var ctx = _gpu.Context11;
        var targets = stackalloc ID3D11RenderTargetView*[3];
        targets[0] = _reflectionCurrent!.View.Handle; targets[1] = _reflectionRoughness!.View.Handle; targets[2] = _reflectionDepth!.View.Handle;
        float* zero = stackalloc float[4]; for (int i = 0; i < 4; i++) zero[i] = 0;
        for (int i = 0; i < 3; i++) ctx.ClearRenderTargetView(targets[i], zero);
        ctx.ClearDepthStencilView(_depthView.Handle, (uint)ClearFlag.Depth, 1, 0);
        ctx.OMSetRenderTargets(3, targets, _depthView.Handle);
        var viewport = new Viewport(0, 0, FieldSize, FieldSize, 0, 1); ctx.RSSetViewports(1, &viewport);
        float oldX = _values[56], oldY = _values[57];
        _values[56] = _values[57] = 1f / FieldSize; _values[231] = 1; Upload();
        ctx.PSSetShader(_reflectionCapture.Handle, null, 0); ctx.DrawIndexed(257 * 257 * 6, 0, 0);
        _values[56] = oldX; _values[57] = oldY; _values[231] = 0;
        var nulls = stackalloc ID3D11ShaderResourceView*[14]; for (int i = 0; i < 14; i++) nulls[i] = null;
        ctx.PSSetShaderResources(0, 14, nulls); ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        float tan = MathF.Tan(camera.Fov * .5f);
        Vector3 right = camera.Right * (tan * camera.AspectRatio), up = Vector3.Cross(camera.Right, camera.Forward) * tan;
        var constants = new Vector4[25];
        constants[1] = new(camera.Forward - right + up, 0); constants[2] = new(right * 2, 0); constants[3] = new(up * -2, 0);
        constants[4] = new(camera.Position, 0); constants[8] = new(_reflectionFrames == 0 ? camera.Position : _reflectionPreviousCamera, 0);
        Matrix4x4 previous = _reflectionFrames == 0 ? camera.ViewProjection : _reflectionPreviousProjection;
        constants[21] = new(previous.M11, previous.M21, previous.M31, previous.M41);
        constants[22] = new(previous.M12, previous.M22, previous.M32, previous.M42);
        constants[24] = new(previous.M14, previous.M24, previous.M34, previous.M44);
        int current = _reflectionFrames & 1;
        var old = _reflectionTemporal[current ^ 1];
        if (!_reflectionTemporal[current].Evaluate(new(_reflectionCurrent.Resource,
            _reflectionFrames == 0 ? _reflectionZero!.Resource : old.Radiance, _reflectionRoughness.Resource,
            _reflectionFrames == 0 ? _reflectionZero!.Resource : old.Confidence, _reflectionDepth.Resource,
            _reflectionZero!.Resource, _linearClamp, _linearClamp, constants))) return false;
        _reflectionPreviousCamera = camera.Position; _reflectionPreviousProjection = camera.ViewProjection; _reflectionPreviousTime = time;
        _reflectionPreviousForward = camera.Forward; _reflectionPreviousFov = camera.Fov; _reflectionPreviousAspect = camera.AspectRatio;
        _reflectionFrames++; TemporalReflectionsActive = true; _values[231] = 2;
        return true;
    }

    private void DisposeTemporalReflections()
    {
        _reflectionCapture.Dispose(); _reflectionCapture = default;
        if (_reflectionTemporal != null) foreach (var pass in _reflectionTemporal) pass.Dispose();
        _reflectionTemporal = null;
        _reflectionCurrent?.Dispose(); _reflectionRoughness?.Dispose(); _reflectionDepth?.Dispose(); _reflectionZero?.Dispose();
        _reflectionCurrent = _reflectionRoughness = _reflectionDepth = _reflectionZero = null;
        ResetTemporalReflections();
    }
}
