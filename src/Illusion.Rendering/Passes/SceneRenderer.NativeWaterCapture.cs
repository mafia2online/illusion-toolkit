using System.Diagnostics;
using Illusion.Rendering.Gpu;

namespace Illusion.Rendering.Passes;

public sealed unsafe partial class SceneRenderer
{
    /// <summary>Renders one ordinary frame, or bounded foam replay at the requested capture projection.
    /// Refuses pending output instead of silently exporting fallback water. No event pumping or desktop capture.</summary>
    public string? RenderForCapture(SharedRenderTarget target, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        for (int draw = 0; draw < 8; draw++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Render(target);
            if (NativeWaterContactPreviewEnabled)
            {
                if (!NativeWaterActive) return "Native contact preview requires supported visible water definitions in a perspective view.";
                if (NativeWaterContactError is { } contactError) return contactError;
                if (!NativeWaterContactPreviewReady) { if (watch.ElapsedMilliseconds >= 300) break; continue; }
            }
            if (!NativeWaterFoamEnabled) return null;
            if (!NativeWaterActive) return "Native foam diagnostic is unavailable in this view: supported visible water definitions and perspective shading are required.";
            if (NativeWaterFoamError is { } error) return error;
            if (NativeWaterFoamReady) return null;
            if (watch.ElapsedMilliseconds >= 300) break;
        }
        if (NativeWaterContactPreviewEnabled && !NativeWaterContactPreviewReady)
            return $"Native contact preview is pending at frame {NativeWaterContactFrame}/{NativeWaterContactTargetFrame}; capture budget is eight draws /300ms, at most four contact steps each.";
        return $"Native foam capture replay is pending at frame {NativeWaterFoamFrame}/{NativeWaterFoamTargetFrame}; capture budget is eight draws /300ms, at most four simulation steps each. Reduce the requested frame or use the viewport projection.";
    }
}
