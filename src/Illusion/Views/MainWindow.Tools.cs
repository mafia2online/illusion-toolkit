using System.Windows;
using System.Windows.Input;
using Illusion.Assets.World;
using Illusion.Mcp;

namespace Illusion.Views;

// The Tools menu of the map editor: windows over the jobs that used to be reachable only through the MCP server.
// Each runs what the matching tool runs; this file only opens the window, or asks and reports.
public partial class MainWindow
{
    private HideTrianglesWindow? _hideTriangles;
    private StreamMapWindow? _streamMap;

    // The mirror item says in its tooltip why it cannot run, instead of failing on the click.
    private void ToolsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        string? why = MirrorWinterObstacle();
        MirrorWinterItem.IsEnabled = why == null;
        MirrorWinterItem.ToolTip = why ?? "Carry this district's edits into its winter archive, so they do not have to be made twice";
    }

    // What stands in the way of a mirror that can be told before trying. The rest (a save that does not
    // complete, a winter archive that cannot be written) is the job's own to refuse.
    private string? MirrorWinterObstacle()
    {
        if (WholeMapCheck.IsChecked == true || AreaCombo.SelectedItem is not MapArea area)
        {
            return "Load one district first - the whole map has no single winter archive";
        }
        if (area.Winter == null) return $"{area.BaseName} has no winter variant";
        if (WinterToggle.IsChecked == true) return "The winter variant is loaded - load the summer one: it is the summer scene that gets mirrored";
        if (Viewport.BridgeEditedCount > 0) return "A Blender edit is open - leave it first (Tab)";
        return null;
    }

    // One window of a kind per editor: asking for it again brings the open one forward.
    private static T ShowTool<T>(T? open, Func<T> create, Action<T?> keep) where T : Window
    {
        if (open != null)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return open;
        }
        T window = create();
        window.Closed += (_, _) => keep(null);
        keep(window);
        window.Show();
        return window;
    }

    private void HideTriangles_Click(object sender, RoutedEventArgs e) =>
        ShowTool(_hideTriangles, () => new HideTrianglesWindow(Viewport) { Owner = this }, w => _hideTriangles = w);

    private void StreamMap_Click(object sender, RoutedEventArgs e) =>
        ShowTool(_streamMap, () => new StreamMapWindow { Owner = this }, w => _streamMap = w);

    private void MirrorWinter_Click(object sender, RoutedEventArgs e)
    {
        CommitFocusedField();
        if (MirrorWinterObstacle() != null || AreaCombo.SelectedItem is not MapArea area) return;

        DialogOutcome answer = AppDialog.Show(this, new DialogOptions
        {
            Title = "Mirror to Winter",
            Icon = DialogIcon.Question,
            Heading = $"Mirror {area.BaseName} into winter?",
            Text = $"The scene is saved, then this district's edits are carried into the working copy of {area.Winter!.Name}: "
                + "objects are matched by name, new ones are added, deleted ones dropped. Build then packs the winter archive.\n\n"
                + "A district whose winter variant is a scene of its own cannot be mirrored - the edits have to be repeated there.",
            Buttons = DialogButtons.YesCancel,
            ConfirmText = "Mirror",
        });
        if (!answer.Confirmed) return;

        string? refused;
        SeasonMirrorOutcome? outcome;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            refused = new AppEditorSession().MirrorToWinter(out outcome);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (refused != null || outcome == null)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Mirror to Winter",
                Icon = DialogIcon.Warning,
                Heading = "Nothing was mirrored",
                Text = ToolText.ForPeople(refused ?? "the winter archive could not be written"),
            });
            return;
        }
        AppDialog.Show(this, new DialogOptions
        {
            Title = "Mirror to Winter",
            Icon = outcome.Ambiguous > 0 ? DialogIcon.Warning : DialogIcon.Success,
            Heading = $"{area.BaseName} is mirrored into winter",
            Text = $"{outcome.Matched} object(s) settled, {outcome.Added} added, {outcome.Dropped} dropped, "
                + $"{outcome.Reassigned} re-pointed material slot(s) carried over; {outcome.Files.Count} file(s) and "
                + $"{outcome.Textures.Count} texture(s) written.\n\nBuild packs the winter archive."
                + (outcome.Ambiguous == 0 ? "" : $"\n\n{outcome.Ambiguous} object(s) could not be told from a namesake and kept "
                    + "their summer materials - look at them in winter."),
        });
    }
}
