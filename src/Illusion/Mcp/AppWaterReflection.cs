namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? WaterReflection(bool? enabled, bool reset, out WaterReflectionInfo? result)
    {
        result = null;
        if (TargetHost is not { } host) return TargetNotOpen;
        return host.SetNativeWaterTemporalReflection(enabled, reset, out result);
    }
}
