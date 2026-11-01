using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class WaterReflectionTools
{
    [McpServerTool(Name = "water_reflection_temporal")]
    [Description("Inspect or opt into native PS1403 temporal filtering of the editor's bounded water screen-space reflection radiance. Requires configured water_optics/BRDF and visible water. Uses a256-square toolkit opaque-scene SSR prepass, explicit water depth/roughness, native R11G11B10/R8 history and verified native water dielectric BRDF composition. History advances per rendered frame; reset starts deterministic replay. Camera cuts restart history. This does not load environment probes, reflect offscreen geometry or write game resources. Disabled by default; enabled=false restores ordinary bounded SSR.")]
    public static async Task<string> Preview(IEditorSession editor, IUiThreadMarshal ui, bool? enabled = null, bool reset = false)
    {
        try
        {
            WaterReflectionInfo? result = null;
            if (await ui.RunAsync(() => editor.WaterReflection(enabled, reset, out result)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, reflection = result });
        }
        catch (Exception error) { return ToolResult.Fail(error); }
    }
}
