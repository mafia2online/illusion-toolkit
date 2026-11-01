namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? StreamWorld(bool enabled, bool winter, bool discardUnsavedEdits)
    {
        if (Window is not { } window) return NotOpen;
        var host = window.Viewport;
        if (host.BridgeEditedCount > 0) return "End the Blender edit session before changing map streaming.";
        if (winter && !host.Areas.Any(area => area.Winter is not null)) return "This map has no winter archives.";
        bool reloads = (window.WholeMapCheck.IsChecked == true) != enabled
            || (window.WinterToggle.IsChecked == true) != winter;
        if (reloads && host.HasUnsavedEdits && !discardUnsavedEdits)
            return "The scene holds unsaved edits. Save them first, or pass discardUnsavedEdits=true to replace the scene and its history.";
        if (window.AreaCombo.SelectedItem is null) window.AreaCombo.SelectedItem = host.Areas.FirstOrDefault();
        if ((window.WinterToggle.IsChecked == true) != winter) window.WinterToggle.IsChecked = winter;
        if ((window.WholeMapCheck.IsChecked == true) != enabled) window.WholeMapCheck.IsChecked = enabled;
        _resourceTarget = false;
        return null;
    }
}
