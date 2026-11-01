using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class NavigationNetworkTools
{
    [McpServerTool(Name = "navigation_network_deform")]
    [Description("Deform a native navigation network of 2..32 explicitly loaded section indices in one document. loadedSections must include source section. Source vertex indices and finite XYZ positions propagate through exact reciprocal seams, including multi-way junctions. Open, blocked and unloaded boundaries remain protected. moveRepresentatives optionally rebuilds moved clusters and reciprocal costs for every affected loaded section using the original offline native functions. Every original loaded graph must reproduce exactly; unknown baselines refuse. Fixed topology only, atomic Undo/Redo and Save/Build. executable optionally names a supported copied executable; no game is launched.")]
    public static async Task<string> Deform(IEditorSession editor, IUiThreadMarshal ui, string name, int section,
        int[] loadedSections, int[] vertices, float[][] positions, string? executable = null, bool moveRepresentatives = false)
    {
        try
        {
            var operation = await ui.RunAsync(() => editor.DeformNavigationNetworkAsync(name, section, loadedSections, vertices, positions, executable, moveRepresentatives));
            if (await operation is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, section, loadedSections, vertices = vertices.Length, moveRepresentatives });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
}
