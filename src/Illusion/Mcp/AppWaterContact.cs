namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? WaterContact(float[]? origin, int[]? texels, float[]? influences, int? frame,
        bool reset, bool clear, bool? jointFoam, out WaterContactInfo? result)
    {
        result = null;
        if (TargetHost is not { } host) return TargetNotOpen;
        return host.SetNativeWaterContactPreview(origin, texels, influences, frame, reset, clear, out result, jointFoam);
    }
}
