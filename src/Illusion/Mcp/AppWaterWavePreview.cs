using System.IO;
using System.Numerics;

namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? WaterWavePreview(float time, float[] weights, float[] center, float span, string mode,
        float displacementScale, out string? description)
    {
        description = null;
        if (ResourceWindow is not { } resource) return "Open a resource editor first.";
        if (weights.Length != 4 || center.Length != 2 || !weights.All(float.IsFinite) || !center.All(float.IsFinite))
            return "Give four finite template weights and two finite world coordinates.";
        try
        {
            var preview = resource.OpenWaterWavePreview(activate: false);
            preview.Configure(time, new Vector4(weights[0], weights[1], weights[2], weights[3]),
                new Vector2(center[0], center[1]), span, mode, displacementScale);
            preview.EnsureReady();
            description = preview.Description;
            return null;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or InvalidOperationException or NotSupportedException or ArgumentException)
        { return ex.Message; }
    }

    public string? WaterWaveScreenshot(string path)
    {
        if (!Path.IsPathRooted(path)) return "Give the full PNG output path.";
        if (ResourceWindow?.WaterWavePreview is not { } preview) return "Open the analytical water preview first.";
        try { preview.SaveFrame(path); return null; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { return ex.Message; }
    }
}
