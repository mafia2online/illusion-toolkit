namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? WaterSurface(bool? enabled, bool? playing, float? time, bool? normals, bool reload, out WaterSurfaceInfo? result)
    {
        result = null;
        if (TargetHost is not { Rnd: { } renderer } host) return TargetNotOpen;
        if (time is { } t && (!float.IsFinite(t) || t < 0 || t > 1000000))
            return "Water time must be finite in 0–1000000 seconds.";
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
            renderer.NativeWaterFlowPartsDrawn);
        return null;
    }
}
