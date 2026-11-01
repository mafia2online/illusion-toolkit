using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Passes;

public sealed unsafe partial class NativeWaterSurface
{
    private NativeWaterContactPreview? _jointContactPreview;
    private JointFoamState? _jointPriorFoam;
    private bool _jointContactStepping;
    private ComPtr<ID3D11ShaderResourceView> _jointPreviousContactFoam;
    private sealed record JointFoamState(bool Enabled, int Mode, int Target, uint Seed);
    internal bool JointContactEnabled => _jointContactPreview is not null;
    internal string? ValidateJointContactConfiguration() => FoamConfigured && FineNormalsConfigured ? null : "Joint contact/foam requires configured native foam and fine-normal inputs first.";
    private ComPtr<ID3D11ShaderResourceView> ContactAnalyticalState => _jointContactStepping ? _jointContactPreview!.State : _contactState;
    // PC FoamUpdate reads contactFoam[idx^1], even though ContactUpdate has already written idx.
    private ComPtr<ID3D11ShaderResourceView> JointContactFoam => _jointContactStepping ? _jointPreviousContactFoam : default;
    private Vector4 ContactShaderState => _jointContactStepping ? new(1, _jointContactPreview!.Origin.X, _jointContactPreview.Origin.Y, 0)
        : ContactConfigured ? new(1, _contactOrigin.X, _contactOrigin.Y, 0) : Vector4.Zero;

    internal string? BeginJointContactReplay(NativeWaterContactPreview preview)
    {
        if (ValidateJointContactConfiguration() is { } refusal) return refusal;
        EnsureContactSampler();
        _jointPriorFoam = new(FoamEnabled, _foamMode, _foamTarget, _foamSeed);
        _jointContactPreview = preview; _jointContactStepping = false; _jointPreviousContactFoam = default;
        return SetFoamCore(true, FoamEnabled ? null : "surface", preview.TargetFrame, null, true);
    }
    internal void EndJointContactReplay()
    {
        if (_jointContactPreview is null && _jointPriorFoam is null) return;
        var prior = _jointPriorFoam; _jointContactPreview = null; _jointPriorFoam = null; _jointContactStepping = false; _jointPreviousContactFoam = default;
        ClearContactStateCore(false); _foamGlobals[140] = _foamGlobals[141] = 0;
        if (prior is not null) SetFoamCore(prior.Enabled, prior.Mode switch { 1 => "normals", 2 => "roughness", 3 => "surface", _ => "diffuse" }, prior.Target, prior.Seed, true);
    }
    private string? SetJointContactClock(bool? enabled, string? mode, int? frame, uint? seed, bool reset)
    {
        if (enabled == false) return "Clear the joint contact preview before disabling its foam stage.";
        if (frame is < 0 or > 36000 || mode is not null && mode is not ("diffuse" or "normals" or "roughness" or "surface")) return "Invalid joint contact/foam frame or mode.";
        if (ValidateJointContactConfiguration() is { } refusal) return refusal;
        var preview = _jointContactPreview!;
        bool restart = reset || seed is { } s && s != _foamSeed || frame is { } f && f < FoamFrame;
        int target = frame ?? preview.TargetFrame;
        if (SetFoamCore(true, mode, target, seed, restart) is { } error) return error;
        preview.Seek(target, restart);
        if (restart || !preview.Ready) ClearContactStateCore(false);
        return null;
    }
    internal string? SeekJointContactReplay(int frame, bool reset) => SetJointContactClock(null, null, frame, null, reset);
    private void ResetJointContactReplay()
    {
        _jointContactStepping = false; _jointPreviousContactFoam = default;
        if (_jointContactPreview is { } preview) { preview.Seek(_foamTarget, true); ClearContactStateCore(false); }
    }
    private bool BeginJointContactStep(int frame)
    {
        if (_jointContactPreview is not { } preview) return true;
        if (preview.Frame != frame - 1 || preview.TargetFrame != _foamTarget)
        { FoamError = "Joint contact/foam clocks lost synchronization; replay will restart."; _foamHasSignature = false; return false; }
        if (_foam is null) { FoamError = "The previous native foam source is unavailable."; return false; }
        _jointPreviousContactFoam = preview.Foam;
        if (preview.AdvanceWithFoamSource(_foam.GeneratedSource, _foam.GeneratedSourceOrigin) != 1)
        { _jointPreviousContactFoam = default; FoamError = preview.Error ?? "Joint contact step was refused."; return false; }
        _jointContactStepping = true;
        return true;
    }
    private void EndJointContactStep() { _jointContactStepping = false; _jointPreviousContactFoam = default; }
    private void PublishJointContactState()
    {
        _jointContactStepping = false;
        if (_jointContactPreview is not { Ready: true } preview || !FoamReady || preview.Frame != FoamFrame) return;
        if (!ConfigureContactStateCore(preview.State, preview.Origin, preview.Frame, false, out string? error)) FoamError = error;
    }
}
