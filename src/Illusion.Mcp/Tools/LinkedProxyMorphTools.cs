using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class LinkedProxyMorphTools
{
    [McpServerTool(Name = "linked_proxy_morph_sources")]
    [Description("List actual shared-library rigid sparse-morph props accepted by the native inline RGBA16F atlas/correction/bind contract. owner identifies a city document. Dynamic, gameplay, collision, weighted skin, unresolved atlases and virtual skeleton dependencies remain excluded. Source files are read only.")]
    public static async Task<string> Sources(IEditorSession editor, IUiThreadMarshal ui, string owner)
    {
        try
        {
            IReadOnlyList<LinkedProxyMorphSourceInfo> sources = [];
            if (await ui.RunAsync(() => editor.InspectLinkedProxyMorphSources(owner, out sources)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, sources });
        }
        catch (Exception exception) { return ToolResult.Fail(exception); }
    }
    [McpServerTool(Name = "linked_proxy_import_morph")]
    [Description("Import a verified rigid native morph prop under a prepared owning Kit. owner is the fine family object, parent is its same-resource Kit, resource/guid come from linked_proxy_morph_sources. Near and LOWEST far atlases, geometry and skeletons are independently owned with fresh cache identities. Copied visual/joint/group envelopes conservatively cover manual target weights in [0,1]. Select the imported visual:0 mesh for morph_targets/morph_preview; both counterparts preview together. Rig edits, weighted/morph skin, signed/overshoot prepared animation and Dynamic remain guarded. Normal Undo/Redo/Save/Build; source prototype untouched.")]
    public static async Task<string> Import(IEditorSession editor, IUiThreadMarshal ui, string owner, string parent, string resource, string guid)
    {
        try
        {
            string? path = null;
            if (await ui.RunAsync(() => editor.ImportLinkedProxyMorph(owner, parent, resource, guid, out path)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, path });
        }
        catch (Exception exception) { return ToolResult.Fail(exception); }
    }
}
