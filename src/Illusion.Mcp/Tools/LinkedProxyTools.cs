using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class LinkedProxyTools
{
    [McpServerTool(Name = "linked_proxy_prepare")]
    [Description("Prepare paired authoring for a verified Mafia III fine/coarse family. First inspect linked_proxy_inspect: expectedTriangles and expectedDraws must match its projected replacement costs. Replaces the merged atlas proxy with separate lowest-LOD native visuals and can substantially increase rendering cost. One undoable edit; normal Save/Build. Fine transforms and eligible unique static UV/normal/color edits update corresponding proxies atomically. Generated far roots, positions, topology, sharing, materials, rigs and hierarchy remain protected.")]
    public static async Task<string> Prepare(IEditorSession editor, IUiThreadMarshal ui, string name, int expectedTriangles, int expectedDraws)
    {
        try
        {
            LinkedProxyInfo? info = null;
            if (await ui.RunAsync(() => editor.PrepareLinkedProxy(name, expectedTriangles, expectedDraws, out info)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, prepared = true, proxy = info });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "linked_proxy_inspect")]
    [Description("Inspect a Mafia III object's verified linked coarse/fine proxy family and projected replacement costs. Read-only: does not prepare geometry, unlock editing, change files or create history. Reports native triangle/draw increases and conservative source payload bytes.")]
    public static async Task<string> Inspect(IEditorSession editor, IUiThreadMarshal ui, string name)
    {
        try
        {
            LinkedProxyInfo? info = null;
            if (await ui.RunAsync(() => editor.InspectLinkedProxy(name, out info)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, proxy = info });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
}
