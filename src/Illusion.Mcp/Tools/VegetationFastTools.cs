using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class VegetationFastTools
{
    [McpServerTool(Name="vegetation_fast_preview")]
    [Description("Inspect/configure opt-in native Mafia III FAST vegetation field. First enable vegetation_wind_preview with explicit context, then provide weatherWind[3], fixed fieldOrigin[2] and noiseSeed. Replays existing wind clock at60Hz, at most4steps/draw; inspect ready before capture. Native field/vertex equations, original clear state, explicit editor seed/fixed-origin history and zero actor colliders; toolkit lighting and rest picking. Default off; never saves resources.")]
    public static async Task<string> Preview(IEditorSession editor,IUiThreadMarshal ui,bool? enabled=null,float[]? weatherWind=null,float[]? fieldOrigin=null,uint? noiseSeed=null)
    {
        try{VegetationFastInfo? result=null;if(await ui.RunAsync(()=>editor.VegetationFast(enabled,weatherWind,fieldOrigin,noiseSeed,out result)) is { } refusal)return ToolResult.Invalid(refusal);return ToolResult.Json(new{success=true,field=result});}
        catch(Exception error){return ToolResult.Fail(error);}
    }
}
