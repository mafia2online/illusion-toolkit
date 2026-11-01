namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? VegetationWind(bool? enabled, float? time, float[]? globalWind,
        float[]? localPosition, float[]? localParameters, out VegetationWindInfo? result)
    {
        result = null;
        if (TargetHost is not { } host) return TargetNotOpen;
        return host.SetNativeVegetationWind(enabled, time, globalWind, localPosition, localParameters, out result);
    }
}
