using System.IO;

namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? EffectPreview(int effect, int generation, int parameter, float time, float age, bool ageAxis,
        out string? description, out IReadOnlyList<string> parameters)
    {
        description = null; parameters = [];
        if (!float.IsFinite(time) || time < 0 || time > 10 || !float.IsFinite(age) || age < 0 || age > 1)
            return "Emission time must be in 0–10 seconds and normalized age in 0–1.";
        if (ResourceWindow is not { } window) return "Open a compiled particle library in the resource editor first.";
        try
        {
            var preview = window.OpenEffectCurvePreview(activate: false);
            preview.Configure(effect, generation, parameter, time, age, ageAxis);
            description = preview.Description; parameters = preview.ParameterNames; return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException)
        { return ex.Message; }
    }
    public string? EffectPreviewScreenshot(string path)
    {
        if (!Path.IsPathRooted(path)) return "Give the full PNG output path.";
        if (ResourceWindow?.EffectCurvePreview is not { } preview) return "Open the native effect preview first.";
        try { preview.SaveFrame(path); return null; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { return ex.Message; }
    }
}
