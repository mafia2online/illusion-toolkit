using System.Numerics;
using Illusion.Mcp;
using Illusion.Rendering.Passes;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    internal string? SetNativeWaterContactPreview(float[]? origin, int[]? texels, float[]? influences,
        int? frame, bool reset, bool clear, out WaterContactInfo? result, bool? jointFoam = null)
    {
        result = null;
        if (Rnd is not { } renderer) return "Wait for the viewport to initialize.";
        bool command = origin is not null || texels is not null || influences is not null || frame is not null || reset || clear || jointFoam is not null;
        if (command && IsLoading) return "Wait for scene loading to finish before changing contact preview.";
        bool replacing = origin is not null || texels is not null || influences is not null;
        if (!clear && !replacing && jointFoam is { } requested && requested != renderer.NativeWaterContactJointFoam)
            return "Provide the raw stamp arrays to change between independent and joint clock modes.";
        if (clear) renderer.ClearNativeWaterContact();
        else if (origin is not null || texels is not null || influences is not null)
        {
            if (origin is not { Length: 2 } || texels is null || influences is null) return "Provide origin XY, sparse texel indices and one finite influence per index together.";
            if (renderer.SetNativeWaterContactPreview(new Vector2(origin[0], origin[1]), texels, influences, frame ?? 1, jointFoam ?? false) is { } refusal) return refusal;
        }
        else if (frame is not null || reset)
            if (renderer.SeekNativeWaterContactPreview(frame ?? renderer.NativeWaterContactTargetFrame, reset) is { } refusal) return refusal;
        result = new(renderer.NativeWaterAvailable, renderer.NativeWaterContactPreviewEnabled,
            renderer.NativeWaterContactPreviewReady, renderer.NativeWaterContactConfigured,
            renderer.NativeWaterContactFrame, renderer.NativeWaterContactTargetFrame,
            [renderer.NativeWaterContactOrigin.X, renderer.NativeWaterContactOrigin.Y], renderer.NativeWaterContactZeroFoamSource, true,
            renderer.NativeWaterContactJointFoam ? SceneRenderer.NativeWaterContactJointScope : SceneRenderer.NativeWaterContactScope, renderer.NativeWaterContactError,
            renderer.NativeWaterContactJointFoam, renderer.NativeWaterFoamFrame, renderer.NativeWaterFoamTargetFrame,
            renderer.NativeWaterContactJointFoam && !renderer.NativeWaterContactZeroFoamSource);
        return null;
    }
}
