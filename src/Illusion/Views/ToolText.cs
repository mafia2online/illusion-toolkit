namespace Illusion.Views;

/// <summary>
/// The tools behind Tools → … are the ones the MCP server exposes, and they refuse in the server's terms: "a
/// Blender session is open — blender_end first". Somebody at the menu has no <c>blender_end</c> to call, so a
/// refusal shown in a dialog names what the window offers instead.
/// </summary>
internal static class ToolText
{
    private static readonly (string Tool, string Menu)[] Wording =
    [
        ("blender_end first", "leave the Blender edit first (Tab)"),
        ("editor_save first", "save first (Ctrl+S)"),
        ("call editor_open_area first", "open a district first"),
        ("editor_open_area first", "open a district first"),
        ("resource_open first", "open it in the Resource Editor first"),
        ("view_set crash=true first", "switch the Crash objects layer on first"),
    ];

    /// <summary>A tool's refusal, reworded for a dialog and given a capital letter.</summary>
    public static string ForPeople(string refusal)
    {
        string text = refusal;
        foreach ((string tool, string menu) in Wording)
        {
            text = text.Replace(tool, menu, StringComparison.Ordinal);
        }
        return text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    }
}
