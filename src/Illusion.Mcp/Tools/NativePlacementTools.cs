using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class NativePlacementTools
{
    [McpServerTool(Name = "foliage_paint")]
    [Description("Inspect or toggle the current Mafia III viewport's read-only procedural foliage preview. Camera cells use verified native clipping, density, additive refinement and active-prefix rules. shuffleSeed controls an explicitly editor-seeded point order; the running game's global shuffle state, wind and final packed-buffer parity are not reproduced. Reports pending cells, point/mesh budgets and unsupported native inputs. Preview state only; no resource writes or undo entry.")]
    public static async Task<string> Paint(IEditorSession editor, IUiThreadMarshal ui, bool? enabled = null, uint? shuffleSeed = null)
    {
        try
        {
            PaintPreviewInfo status = null!;
            string? refusal = await ui.RunAsync(() => editor.NativePaint(enabled, shuffleSeed, out status));
            return refusal is null ? ToolResult.Json(new { success = true, status }) : ToolResult.Invalid(refusal);
        }
        catch (Exception exception) { return ToolResult.Fail(exception); }
    }

    [McpServerTool(Name = "placement_status")]
    [Description("Inspect loaded Mafia III native translocator placement documents in the current editor. Reports owning archives, activated resource GUIDs, placement/rendered counts and explicit unsupported-condition/component refusals. Shared dependencies resolve templates but do not activate their placements again. Read-only; does not change scene loading or game resources.")]
    public static async Task<string> Status(IEditorSession editor, IUiThreadMarshal ui)
    {
        try { return ToolResult.Json(new { success = true, documents = await ui.RunAsync(editor.NativePlacements) }); }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
}
