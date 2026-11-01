using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class WaterWaveTools
{
    [McpServerTool(Name = "water_surface")]
    [Description("Inspect or control the Mafia III analytical water surface in the current viewport. Optional enabled, playing, time and normals change only preview state; reload rereads wave configuration and lookup files. Actual defining triangles supply native mask/color UVs and screen-space reconstruction. Active requires supported visible definitions, inputs and perspective shading. Uses toolkit lighting; dynamic ripples, shoreline height, SSR, foam and refraction are excluded. No game resources are modified.")]
    public static async Task<string> Surface(IEditorSession editor, IUiThreadMarshal ui, bool? enabled = null,
        bool? playing = null, float? time = null, bool? normals = null, bool reload = false)
    {
        try
        {
            WaterSurfaceInfo? result = null;
            if (await ui.RunAsync(() => editor.WaterSurface(enabled, playing, time, normals, reload, out result)) is { } refusal)
                return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, scope = "analytical surface with toolkit shading; height/ripple inputs zero", surface = result });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
    [McpServerTool(Name = "water_wave_preview")]
    [Description("Preview verified Mafia III analytical water waves using render_config.xml and sincos.dds from the current game root. Opens without activation. Four template weights are explicit RGBA inputs, not normalized; center is world XY and span is the vertical viewed world extent (horizontal extent follows image aspect). mode selects normals or displacement. This isolated field reproduces the analytical wave stage; it excludes ripples, depth, reflections, lighting and the engine's screen-space ocean grid. Reads files only.")]
    public static async Task<string> Preview(IEditorSession editor, IUiThreadMarshal ui, float time = 0,
        float[]? weights = null, float[]? center = null, float span = 128, string mode = "normals", float displacementScale = 1)
    {
        try
        {
            var result = await ui.RunAsync(() =>
            {
                string? refusal = editor.WaterWavePreview(time, weights ?? [1, 0, 0, 0], center ?? [0, 0],
                    span, mode, displacementScale, out string? description);
                return (refusal, description);
            });
            return result.refusal is { } refusal ? ToolResult.Invalid(refusal)
                : ToolResult.Json(new { success = true, scope = "isolated analytical water waves", description = result.description,
                    time, weights = weights ?? [1, 0, 0, 0], center = center ?? [0, 0], span, mode, displacementScale });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "water_wave_screenshot")]
    [Description("Save the analytical wave preview GPU output as PNG, without capturing or activating the desktop.")]
    public static async Task<string> Screenshot(IEditorSession editor, IUiThreadMarshal ui, string path)
    {
        try
        {
            if (await ui.RunAsync(() => editor.WaterWaveScreenshot(path)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, path });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
}
