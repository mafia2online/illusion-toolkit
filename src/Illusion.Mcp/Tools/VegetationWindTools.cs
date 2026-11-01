using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class VegetationWindTools
{
    [McpServerTool(Name = "vegetation_wind_preview")]
    [Description("Inspect or configure opt-in Mafia III LARGE/GENERAL native vegetation vertex wind. Enabling requires explicit globalWind=[D119x,y,z,w], localPosition=[D213x,y,z], localParameters=[D214x,y]. Time is an explicit clock within ±600 seconds. Only verified instanced templates are animated; unsupported meshes remain static with refusal counts. Toolkit lighting and rest-pose picking remain in use. Suspended during Blender bridge editing. Default off; no resources are written.")]
    public static async Task<string> Preview(IEditorSession editor, IUiThreadMarshal ui, bool? enabled = null,
        float? time = null, float[]? globalWind = null, float[]? localPosition = null, float[]? localParameters = null)
    {
        try
        {
            VegetationWindInfo? result = null;
            if (await ui.RunAsync(() => editor.VegetationWind(enabled, time, globalWind, localPosition, localParameters, out result)) is { } refusal)
                return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, wind = result });
        }
        catch (Exception error) { return ToolResult.Fail(error); }
    }
}
