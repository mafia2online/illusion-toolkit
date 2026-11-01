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

    [McpServerTool(Name = "linked_proxy_group")]
    [Description("Create an undoable native Kit group in a prepared fine object's own resource. owner identifies the fine object; name is the new group name. The group starts at the resource root. Use scene_reparent to place the fine object under it; its generated far root continues to follow its world transform. Normal Undo/Redo and Save/Build apply.")]
    public static async Task<string> Group(IEditorSession editor, IUiThreadMarshal ui, string owner, string name)
    {
        try
        {
            string? path = null;
            if (await ui.RunAsync(() => editor.CreateLinkedProxyGroup(owner, name, out path)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, path });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "linked_proxy_bones")]
    [Description("Inspect native rest bones of a prepared fine/far pair. Reports index, parent, name and authored parent-local row-major matrix. Supports verified rigid, static collision/glass and imported pure Model/Skeleton weighted rigs. Unknown gameplay/dynamic and morph layouts remain refused.")]
    public static async Task<string> Bones(IEditorSession editor, IUiThreadMarshal ui, string owner)
    {
        try
        {
            IReadOnlyList<LinkedProxyBoneInfo> bones = [];
            if (await ui.RunAsync(() => editor.InspectLinkedProxyBones(owner, out bones)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, bones });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "linked_proxy_bone_set")]
    [Description("Author one existing native rigid rest bone on a prepared fine object and its generated far counterpart, atomically and undoably. localMatrix is 16 row-major floats; translation is indices12–14. Must remain a finite proper rigid transform. Preserves inverse binds, indices, palettes and raw vertex buffers; refreshes native bounds and both representations. Verified static collision/glass placements follow rigid bones; imported pure weighted rigs refit native joint envelopes. Unknown dynamic/gameplay and morph layouts remain refused. Normal Save/Build applies.")]
    public static async Task<string> SetBone(IEditorSession editor, IUiThreadMarshal ui, string owner, int bone, float[] localMatrix)
    {
        try
        {
            if (await ui.RunAsync(() => editor.SetLinkedProxyBone(owner, bone, localMatrix)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "linked_proxy_bone_add")]
    [Description("Append an unused rest bone to a prepared Mafia III fine/far pair. Existing palette indices remain unchanged. Supply a unique name, existing parent index, rigid local row-major matrix and positive local bound reach. Undoable; normal Save/Build.")]
    public static async Task<string> AddBone(IEditorSession editor, IUiThreadMarshal ui, string owner, string name, int parent, float[] localMatrix, float reach)
    {
        try
        {
            if (await ui.RunAsync(() => editor.AddLinkedProxyBone(owner, name, parent, localMatrix, reach)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "linked_proxy_bone_reparent")]
    [Description("Reparent a rest bone in a prepared Mafia III fine/far pair while preserving its model-space bind transform. Parent must precede the bone, or be -1 for a root. Existing palette indices remain unchanged. Undoable; normal Save/Build.")]
    public static async Task<string> ReparentBone(IEditorSession editor, IUiThreadMarshal ui, string owner, int bone, int parent)
    {
        try
        {
            if (await ui.RunAsync(() => editor.ReparentLinkedProxyBone(owner, bone, parent)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true });
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
