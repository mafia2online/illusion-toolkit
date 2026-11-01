using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class MorphTools
{
    [McpServerTool(Name = "morph_channels")]
    [Description("Read Mafia III sparse expression target IDs and atlas statistics for a compiled visual. lod is zero-based. Requires the native RGBA16F atlas dependency in the copied resource mirror. Does not change files.")]
    public static async Task<string> Channels(IEditorSession editor, IUiThreadMarshal ui, string name, int lod = 0)
    {
        try
        {
            MorphInfo? result = null;
            if (await ui.RunAsync(()=>editor.MorphChannels(name,lod,out result)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success=true, channels=result });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "morph_preview")]
    [Description("Transient Mafia III facial morph GPU preview. Supply matching zero-based targetIndices and finite weights in [0,1]. Empty arrays clear the preview exactly. Evaluates native sparse position and tangent-frame deltas before skinning. Does not author native expression weights, save resource changes, or create undo entries. Requires all uploaded LOD atlas dependencies; one preview is active per viewport.")]
    public static async Task<string> Preview(IEditorSession editor, IUiThreadMarshal ui, string name, int[] targetIndices, float[] weights)
    {
        try
        {
            if (targetIndices is null || weights is null || targetIndices.Length != weights.Length || weights.Length > 4096
                || targetIndices.Any(i=>i<0) || weights.Any(w=>!float.IsFinite(w)||w<0||w>1)
                || targetIndices.Distinct().Count()!=targetIndices.Length)
                return ToolResult.Invalid("Give equal-length unique targetIndices and finite weights in [0,1], at most 4096 targets.");
            var values=targetIndices.Select((index,i)=>(index,weights[i])).ToDictionary(p=>p.index,p=>p.Item2);
            if (await ui.RunAsync(()=>editor.MorphPreview(name,values)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success=true, transient=true, active=weights.Any(w=>w!=0) });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
}
