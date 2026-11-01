namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? WaterSurface(bool? enabled, bool? playing, float? time, bool? normals, bool reload, out WaterSurfaceInfo? result)
    {
        result = null;
        if (TargetHost is not { Rnd: { } renderer } host) return TargetNotOpen;
        if (time is { } t && (!float.IsFinite(t) || t < 0 || t > 1000000))
            return "Water time must be finite in 0–1000000 seconds.";
        if (renderer.NativeWaterFoamEnabled && time is > 600) return "Foam replay is bounded to 600 seconds /36000frames.";
        if (reload && host.ReloadNativeWaterInputs() is { } refusal) return refusal;
        if (!renderer.NativeWaterAvailable && (enabled == true || playing == true || time is not null || normals is not null))
            return host.NativeWaterInputError ?? "Native analytical water inputs are unavailable for this viewport.";
        if (enabled is { } e) renderer.NativeWaterEnabled = e;
        if (playing is { } p) renderer.NativeWaterPlaying = p;
        if (time is { } value) renderer.NativeWaterTime = value;
        if (normals is { } n) renderer.NativeWaterNormals = n;
        result = new(renderer.NativeWaterAvailable, renderer.NativeWaterEnabled, renderer.NativeWaterActive,
            renderer.NativeWaterPlaying, renderer.NativeWaterTime, renderer.NativeWaterNormals,
            renderer.NativeWaterPartsDrawn, host.NativeWaterInputError, renderer.NativeWaterFineNormalsConfigured,
            renderer.NativeWaterFlowPartsDrawn, renderer.NativeWaterFoamConfigured, renderer.NativeWaterFoamEnabled,
            renderer.NativeWaterFoamReady, renderer.NativeWaterFoamFrame, renderer.NativeWaterFoamTargetFrame,
            renderer.NativeWaterFoamSeed, renderer.NativeWaterFoamMode, renderer.NativeWaterFoamError,
            renderer.NativeWaterOpticsConfigured, renderer.NativeWaterOpticsActive, renderer.NativeWaterReflections, renderer.NativeWaterRefraction);
        return null;
    }

    public string? WaterOptics(bool? reflections, bool? refraction, out WaterSurfaceInfo? result)
    {
        result = null;
        if (TargetHost is not { Rnd: { } renderer }) return TargetNotOpen;
        if (!renderer.NativeWaterOpticsConfigured && (reflections == true || refraction == true))
            return "Water optics require native D261 thresholds and a supported env_brdf_lut_blinn.dds in copied working assets.";
        if (reflections is { } r) renderer.NativeWaterReflections = r;
        if (refraction is { } t) renderer.NativeWaterRefraction = t;
        return WaterSurface(null, null, null, null, false, out result);
    }

    public string? WaterFoam(bool? enabled, string? mode, int? frame, uint? seed, bool reset, out WaterSurfaceInfo? result)
    {
        result = null;
        if (TargetHost is not { Rnd: { } renderer } host) return TargetNotOpen;
        if (frame is < 0 or > 36000) return "Foam frame must be in 0–36000.";
        if (mode is not null && mode is not ("diffuse" or "normals" or "roughness")) return "Foam mode must be diffuse, normals or roughness.";
        if (!renderer.NativeWaterAvailable) return host.NativeWaterInputError ?? "Native water is unavailable.";
        bool willEnable = enabled ?? renderer.NativeWaterFoamEnabled;
        if (willEnable && (frame is { } targetFrame ? targetFrame / 60f : renderer.NativeWaterTime) > 600)
            return "Foam replay is bounded to 600 seconds /36000frames.";
        if (willEnable && !renderer.NativeWaterFineNormalsConfigured) return "Native variance inputs are unavailable for the foam material diagnostic.";
        bool activating = enabled == true && !renderer.NativeWaterFoamEnabled;
        if (enabled != false && (enabled == true || frame is not null || reset) && (!renderer.NativeWaterFoamConfigured || reset || activating))
            if (host.ReloadNativeWaterFoamInputs(forceTextureRead: true) is { } inputError) return inputError;
        if (renderer.SetNativeWaterFoam(enabled, mode, frame, seed, reset) is { } refusal) return refusal;
        if (frame is { } f) renderer.NativeWaterTime = f / 60f;
        return WaterSurface(null, null, null, null, false, out result);
    }
}
