using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class WaterWaveTools
{
    [McpServerTool(Name = "water_foam")]
    [Description("Opt in to an isolated Mafia III viewport foam diagnostic. Default OFF. mode=diffuse shows the native authored T000/D265 foam term; normals/roughness show native pre-lighting material inputs at an explicit deep-water boundary. Actual opaque depth, water-definition height and analytical displacement feed fixed 1/60 simulation with explicit seed and zero dynamic ripples. Frame seek0–36000 is bounded to at most4steps per render; check foamReady and actual/target frames. Backward seek/reset/seed/source changes reset deterministic history. No SSR, native final lighting or white fallback is invented; no resource writes.")]
    public static async Task<string> Foam(IEditorSession editor, IUiThreadMarshal ui, bool? enabled = null,
        string? mode = null, int? frame = null, uint? seed = null, bool reset = false)
    {
        try
        {
            WaterSurfaceInfo? result = null;
            if (await ui.RunAsync(() => editor.WaterFoam(enabled, mode, frame, seed, reset, out result)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, scope = "native foam material diagnostic; deep-water input boundary, no native final lighting/SSR", surface = result });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
    [McpServerTool(Name = "water_surface")]
    [Description("Inspect or control the Mafia III analytical water surface in the current viewport. Optional enabled, playing, time and normals change only preview state; reload rereads wave configuration and lookup files. Actual defining triangles supply native mask/color UVs and screen-space reconstruction. Active requires supported visible definitions, inputs and perspective shading. Uses toolkit lighting; dynamic ripples, SSR and refraction are excluded. Foam is a separate opt-in water_foam material diagnostic, default OFF; its replay is bounded to600seconds. No game resources are modified.")]
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
