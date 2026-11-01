using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class AnimationAuthoringTools
{
    [McpServerTool(Name = "animation_authoring")]
    [Description("Inspect or edit the clip selected by animation_open. operation=transform writes16 native parent-local row-major track matrix values (additive bindings retain deltas) at frame/track; float writes one raw native float/expression track value. Packed spline clips are baked to verified native interleaved frames; playback between authored frames uses native linear interpolation. operation=undo/redo uses this animation window's key history; save writes native working blobs and SDS allocation metadata. Build the copied animation archive separately. inspect returns frame counts, named track bindings and native blend hint; read_transform/read_float sample the native frame/track without editing. Bindings, named skeletons and untouched tracks remain preserved; unsupported packfile layouts refuse.")]
    public static async Task<string> Authoring(IEditorSession editor, IUiThreadMarshal ui, string operation = "inspect",
        int frame = 0, int track = 0, float[]? localMatrix = null, float? value = null)
    {
        try
        {
            AnimationAuthoringInfo? result = null;
            if (await ui.RunAsync(() => editor.AnimationAuthoring(operation,frame,track,localMatrix,value,out result)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, animation = result });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
}
