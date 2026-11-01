using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class LinkedProxySkinTools
{
    [McpServerTool(Name="linked_proxy_skin_sources")]
    [Description("Read-only list of actual shared-library weighted prop sources satisfying the native pure Model/Skeleton, palette, normalized-weight and complete joint-envelope contract. owner identifies a city object in the destination document. Returns source resource paths and native GUIDs for linked_proxy_import_skin; unsupported Dynamic/gameplay/morph and incomplete envelopes are omitted.")]
    public static async Task<string> Sources(IEditorSession editor,IUiThreadMarshal ui,string owner)
    {
        try
        {
            IReadOnlyList<LinkedProxySkinSourceInfo> sources=[];
            if(await ui.RunAsync(()=>editor.InspectLinkedProxySkinSources(owner,out sources))is{}refusal)return ToolResult.Invalid(refusal);
            return ToolResult.Json(new{success=true,sources});
        }
        catch(Exception exception){return ToolResult.Fail(exception);}
    }
    [McpServerTool(Name="linked_proxy_import_skin")]
    [Description("Import a verified native weighted prop from this city's shared prop library into an existing prepared Kit, with independently owned near and LOWEST far skeleton/palette/buffer copies. owner is an existing fine family object, parent is its same-resource Kit, resource is the extracted source GENR path and guid its hexadecimal object ID. Pure Model/Skeleton only; every native joint envelope must contain all influenced vertices. No Dynamic, gameplay or morph dependencies. Places at owner's world transform. Undo/Redo and normal Save/Build; returned path identifies the new fine object.")]
    public static async Task<string> Import(IEditorSession editor,IUiThreadMarshal ui,string owner,string parent,string resource,string guid)
    {
        try
        {
            string? path=null;
            if(await ui.RunAsync(()=>editor.ImportLinkedProxySkin(owner,parent,resource,guid,out path))is{}refusal)return ToolResult.Invalid(refusal);
            return ToolResult.Json(new{success=true,path});
        }
        catch(Exception exception){return ToolResult.Fail(exception);}
    }
}
