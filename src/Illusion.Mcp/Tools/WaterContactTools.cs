using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class WaterContactTools
{
    [McpServerTool(Name = "water_contact_preview")]
    [Description("Inspect or preview an explicit Mafia III native water-contact stamp. Provide origin=[worldX,worldY], unique flattened 512² texel indices (index=y*512+x), and finite influences. One impulse is quantized by native S_WaterStamp.Write then propagated at fixed1/60; frame is bounded0–36000, at most4steps per draw. Domain is96m square centered on origin; increasing texelY means increasing worldY. ZERO height capture and ZERO wind are explicit boundaries. By default this independent preview clock changes geometry/normals only. Optional jointFoam=true requires already configured native foam inputs, then advances contact and foam in ONE bounded clock. Neither mode supplies actor physics or real contact shoreline capture. Omit arrays to inspect/seek/reset; clear restores analytical baseline. No game resources are written.")]
    public static async Task<string> Preview(IEditorSession editor, IUiThreadMarshal ui, float[]? origin = null,
        int[]? texels = null, float[]? influences = null, int? frame = null, bool reset = false, bool clear = false, bool? jointFoam = null)
    {
        try
        {
            WaterContactInfo? result = null;
            if (await ui.RunAsync(() => editor.WaterContact(origin, texels, influences, frame, reset, clear, jointFoam, out result)) is { } refusal)
                return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, contact = result });
        }
        catch (Exception error) { return ToolResult.Fail(error); }
    }
}
