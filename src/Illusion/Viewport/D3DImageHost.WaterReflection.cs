using Illusion.Mcp;
using Illusion.Rendering.Passes;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    internal string? SetNativeWaterTemporalReflection(bool? enabled, bool reset, out WaterReflectionInfo? result)
    {
        result = null;
        if (Rnd is not { } renderer) return "Wait for viewport initialization.";
        if ((enabled is not null || reset) && IsLoading) return "Wait for scene loading to finish.";
        if (enabled == true && (!renderer.NativeWaterAvailable || !renderer.NativeWaterOpticsConfigured))
            return "Load native water inputs and configure water optics/BRDF before enabling temporal reflections.";
        if (enabled is { } requested) renderer.NativeWaterTemporalReflections = requested;
        if (reset) renderer.ResetNativeWaterTemporalReflections();
        result = new(renderer.NativeWaterAvailable, renderer.NativeWaterOpticsConfigured, renderer.NativeWaterTemporalReflections,
            renderer.NativeWaterTemporalReflectionsActive, renderer.NativeWaterTemporalReflectionFrames, SceneRenderer.NativeWaterTemporalReflectionScope);
        return null;
    }
}
