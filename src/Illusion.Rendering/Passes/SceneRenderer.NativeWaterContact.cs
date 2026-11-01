using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Passes;

public sealed unsafe partial class SceneRenderer
{
    private NativeWaterContactPreview? _contactPreview;
    private long _contactPreviewRevision;
    private string? _contactPreviewError;
    private float? _jointPriorWaterTime;
    private bool _jointPriorPlaying;
    public bool NativeWaterContactJointFoam => _nativeWater?.JointContactEnabled == true;
    public bool NativeWaterContactZeroFoamSource => _contactPreview?.ZeroFoamSource ?? true;
    public bool NativeWaterContactPreviewEnabled => _contactPreview is not null;
    public bool NativeWaterContactPreviewReady => NativeWaterActive && _contactPreview?.Ready == true && (!NativeWaterContactJointFoam || NativeWaterFoamReady);
    public int NativeWaterContactFrame => _contactPreview?.Frame ?? 0;
    public Vector2 NativeWaterContactOrigin => _contactPreview?.Origin ?? Vector2.Zero;
    public int NativeWaterContactTargetFrame => _contactPreview?.TargetFrame ?? 0;
    public string? NativeWaterContactError => _contactPreviewError ?? _contactPreview?.Error ?? (NativeWaterContactJointFoam ? NativeWaterFoamError : null);
    public const string NativeWaterContactScope = "Explicit sparse 512² R8 stamp in a fixed 96m world domain; ZERO auxiliary foam source (t052) and ZERO wind. Independent fixed contact clock; no actor physics or joint foam history.";
    public const string NativeWaterContactJointScope = "Explicit sparse native contact plus foam in ONE fixed replay clock. PC order: prior blurred RGBA8 foam source feeds contact; prior contact foam plus current analytical field feeds foam generation/history. Toolkit depth/definition capture and seeded noise; ZERO wind. No actor physics or live-game radiance/capture parity.";
    public string? SetNativeWaterContactPreview(Vector2 origin, IReadOnlyList<int> texels, IReadOnlyList<float> influences, int frame, bool jointFoam = false)
    {
        if (_nativeWater is null) return "Native water is unavailable.";
        if (jointFoam && _nativeWater.ValidateJointContactConfiguration() is { } jointError) return jointError;
        NativeWaterContactPreview fresh;
        try { fresh = new(_gpu, origin, texels.ToArray(), influences.ToArray(), frame); }
        catch (ArgumentException error) { return error.Message; }
        try { if (jointFoam) _nativeWater.PrepareJointContactResources(); } catch { fresh.Dispose(); throw; }
        ClearNativeWaterContact(); _contactPreview = fresh; _contactPreviewError = null;
        if (jointFoam)
        {
            _jointPriorWaterTime = _nativeWaterTime; _jointPriorPlaying = NativeWaterPlaying;
            _nativeWaterTime = frame / 60f; NativeWaterPlaying = false;
            if (_nativeWater.BeginJointContactReplay(fresh) is { } refusal) { ClearNativeWaterContact(); return refusal; }
        }
        return null;
    }
    public string? SeekNativeWaterContactPreview(int frame, bool reset = false)
    {
        if (_contactPreview is null) return "Configure an explicit native contact stamp first.";
        if (NativeWaterContactJointFoam)
        {
            string? refusal = _nativeWater!.SeekJointContactReplay(frame, reset);
            if (refusal is null) _nativeWaterTime = frame / 60f;
            return refusal;
        }
        if (!_contactPreview.Seek(frame, reset)) return "Contact frame must be within 0–36000.";
        if (reset || !_contactPreview.Ready) _nativeWater?.ClearContactState();
        _contactPreviewError = null; return null;
    }
    private void AdvanceNativeWaterContactPreview()
    {
        if (_contactPreview is null || _nativeWater is null || NativeWaterContactJointFoam) return;
        int count = _contactPreview.Advance();
        if (count > 0) _contactPreviewRevision = (_contactPreviewRevision + 1) & long.MaxValue;
        if (_contactPreview.Ready)
            _nativeWater.ConfigureContactState(_contactPreview.State, _contactPreview.Origin, _contactPreviewRevision, out _contactPreviewError);
        else _nativeWater.ClearContactState();
    }
    private void DisposeNativeWaterContactPreview()
    {
        _nativeWater?.EndJointContactReplay();
        if (_jointPriorWaterTime is { } time) { _nativeWaterTime = time; NativeWaterPlaying = _jointPriorPlaying; _jointPriorWaterTime = null; }
        _contactPreview?.Dispose(); _contactPreview = null; _contactPreviewError = null;
    }
    public bool NativeWaterContactConfigured => _nativeWater?.ContactConfigured ?? false;
    public long NativeWaterContactRevision => _nativeWater?.ContactRevision ?? 0;
    public bool ConfigureNativeWaterContact(ComPtr<ID3D11ShaderResourceView> state, Vector2 origin,
        long contentRevision, out string? refusal)
    {
        if (_nativeWater is null) { refusal = state.Handle == null ? null : "Native water is unavailable."; return state.Handle == null; }
        bool accepted = _nativeWater.ConfigureContactState(state, origin, contentRevision, out refusal);
        if (accepted) DisposeNativeWaterContactPreview();
        return accepted;
    }
    public void ClearNativeWaterContact() { _nativeWater?.ClearContactState(); DisposeNativeWaterContactPreview(); }
}
