using System.IO;

namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public async Task<EffectPointViewResult> EffectPointPreviewAsync(ulong generation, float time, bool timeline, int frames, float step, uint seed, string plane, float zoom)
    {
        if (ResourceWindow is not { } resource) return new(null, null, "Open a compiled particle library in the resource editor first.");
        try
        {
            var preview = resource.OpenEffectPointPreview(activate: false); preview.Configure(generation, time, timeline, frames, step, seed, plane, zoom);
            if (await preview.WaitForReadyAsync() is { } refusal) return new(null, null, refusal);
            if (preview.Snapshot is not { } snapshot || snapshot.GenerationId != generation || timeline && snapshot.Frames != frames)
                return new(null, null, "The point preview changed while the requested frame was being evaluated; retry the request.");
            return new(snapshot, preview.TexturePath, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException)
        { return new(null, null, ex.Message); }
    }
    public string? EffectPointScreenshot(string path)
    {
        if (!Path.IsPathRooted(path)) return "Give the full PNG output path.";
        if (ResourceWindow?.EffectPointPreview is not { } preview) return "Open the native point-burst preview first.";
        try { preview.SaveFrame(path); return null; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { return ex.Message; }
    }
}
