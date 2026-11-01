using System.Numerics;
using Illusion.Mcp;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    internal string? SetNativeVegetationWind(bool? enabled, float? time, float[]? globalWind,
        float[]? localPosition, float[]? localParameters, out VegetationWindInfo? result)
    {
        result = null;
        if (Rnd is not { } renderer) return "Wait for the viewport to initialize.";
        bool command = enabled is not null || time is not null || globalWind is not null || localPosition is not null || localParameters is not null;
        if (command && IsLoading) return "Wait for scene loading to finish before changing vegetation wind.";
        if (renderer.NativeVegetationFastEnabled && time is < 0) return "FAST field replay requires a clock within0..600seconds; disable FAST before using a negative LARGE clock.";
        if (globalWind is not null && globalWind.Length != 4 || localPosition is not null && localPosition.Length != 3
            || localParameters is not null && localParameters.Length != 2) return "Provide globalWind[4], localPosition[3], localParameters[2].";
        if (enabled == true && !renderer.NativeVegetationWindEnabled
            && (globalWind is null || localPosition is null || localParameters is null))
            return "Enabling wind requires all three explicit native context arrays.";
        if (command)
        {
            try
            {
                renderer.ConfigureNativeVegetationWind(enabled ?? renderer.NativeVegetationWindEnabled,
                    time ?? renderer.NativeVegetationWindTime,
                    globalWind is null ? renderer.NativeVegetationWindGlobal : new Vector4(globalWind[0], globalWind[1], globalWind[2], globalWind[3]),
                    localPosition is null ? renderer.NativeVegetationWindLocalPosition : new Vector3(localPosition[0], localPosition[1], localPosition[2]),
                    localParameters is null ? renderer.NativeVegetationWindLocalParameters : new Vector2(localParameters[0], localParameters[1]));
            }
            catch (ArgumentException error) { return error.Message; }
        }
        var g = renderer.NativeVegetationWindGlobal; var p = renderer.NativeVegetationWindLocalPosition; var l = renderer.NativeVegetationWindLocalParameters;
        result = new(renderer.NativeVegetationWindEnabled, renderer.NativeVegetationWindTime,
            [g.X, g.Y, g.Z, g.W], [p.X, p.Y, p.Z], [l.X, l.Y], renderer.NativeVegetationWindBridgeSuspended,
            renderer.NativeVegetationWindDrawnParts, renderer.NativeVegetationWindEligibleMeshes,
            new Dictionary<string, int>(renderer.NativeVegetationWindRefusals),
            "Verified LARGE/GENERAL instanced vertex deformation; explicit native context, toolkit lighting, authored rest-pose picking. Unsupported templates remain static.")
            { PendingMeshes = renderer.NativeVegetationWindPendingMeshes };
        return null;
    }
}
