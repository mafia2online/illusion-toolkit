using System.IO;
using System.Windows;
using Illusion.Assets;
using Illusion.Formats.StreamMap;
using Microsoft.Win32;

namespace Illusion.Views;

/// <summary>
/// Tools → Stream map…: find and replace across the strings of a <c>StreamMapa.bin</c>, with the list of what
/// would change shown before anything is written. The replacement and the write-with-backup are
/// <see cref="StreamMapEditor"/>'s - the same ones the <c>edit_stream_map</c> tool uses.
/// </summary>
public sealed partial class StreamMapWindow : Window
{
    /// <summary>One string the replacement touches - or would, were it not refused.</summary>
    public sealed record EditRow(string Kind, string Before, string After, string? Tip);

    private byte[]? _file;
    private StreamMapPatch? _preview;

    public StreamMapWindow()
    {
        InitializeComponent();
        if (MafiaEnvironment.IsInitialized && File.Exists(MafiaEnvironment.StreamMapPath))
        {
            PathBox.Text = MafiaEnvironment.StreamMapPath;
        }
        else
        {
            ReadFile();
        }
    }

    private StreamMapFields Fields =>
        (PathCheck.IsChecked == true ? StreamMapFields.Path : StreamMapFields.None)
        | (EntityCheck.IsChecked == true ? StreamMapFields.Entity : StreamMapFields.None)
        | (LineCheck.IsChecked == true ? StreamMapFields.LineName : StreamMapFields.None)
        | (GroupCheck.IsChecked == true ? StreamMapFields.GroupName : StreamMapFields.None);

    private void Path_Changed(object sender, RoutedEventArgs e) => ReadFile();

    private void Query_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) Preview();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a StreamMap",
            Filter = "StreamMap (*.bin)|*.bin|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        string current = PathBox.Text.Trim();
        if (current.Length > 0 && Directory.Exists(Path.GetDirectoryName(current))) dialog.InitialDirectory = Path.GetDirectoryName(current);
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FileName;
    }

    // Reads the file named in the field. What it holds is said under the field, and so is why it could not be
    // read - a path being typed is wrong most of the time, which is no reason for a dialog.
    private void ReadFile()
    {
        _file = null;
        string path = PathBox.Text.Trim();
        if (path.Length == 0)
        {
            FileText.Text = "Pick a StreamMapa.bin - the game's own is in edit\\tables.";
        }
        else if (!File.Exists(path))
        {
            FileText.Text = "No such file.";
        }
        else
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                StreamMapFile map = StreamMapFile.Read(bytes);
                _file = bytes;
                FileText.Text = $"{map.GroupHeaders.Length} group(s), {map.Lines.Length} line(s), {map.Loaders.Length} loader(s).";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Formats.FileFormatException
                                           or ArgumentException)
            {
                FileText.Text = "Not a StreamMap this toolkit can read: " + ex.Message;
            }
        }
        if (IsInitialized) Preview();
    }

    private void Preview()
    {
        _preview = null;
        ReplaceBtn.IsEnabled = false;
        EditList.ItemsSource = null;
        CountText.Text = "The file is copied beside itself as a backup before it is written.";
        if (_file == null)
        {
            EmptyLabel.Text = "No file open.";
            EmptyLabel.Visibility = Visibility.Visible;
            return;
        }
        string find = FindBox.Text;
        if (find.Length == 0 || Fields == StreamMapFields.None)
        {
            EmptyLabel.Text = find.Length == 0 ? "Type what to find." : "Tick at least one kind of text.";
            EmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        StreamMapPatch patch;
        try
        {
            patch = StreamMapEditor.Replace(_file, find, ReplaceBox.Text, Fields, dryRun: true);
        }
        catch (Exception ex) when (ex is Formats.FileFormatException or ArgumentException)
        {
            EmptyLabel.Text = ex.Message;
            EmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        _preview = patch;
        EditList.ItemsSource = patch.Edits.Select(e => new EditRow(KindOf(e.Field), e.Before,
            e.Refused == null ? e.After : "refused: " + e.Refused, e.Refused ?? $"{e.Before}  →  {e.After}")).ToList();
        EmptyLabel.Text = "Nothing in this file matches.";
        EmptyLabel.Visibility = patch.Edits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int fits = patch.Edits.Count(e => e.Refused == null);
        int refused = patch.Edits.Count - fits;
        if (patch.Edits.Count > 0)
        {
            CountText.Text = $"{fits} string(s) will change"
                + (refused > 0 ? $"; {refused} cannot - the new text is longer than the old, and the edit is made in place" : "")
                + ". A backup is written first.";
        }
        ReplaceBtn.IsEnabled = fits > 0 && find != ReplaceBox.Text;
    }

    private static string KindOf(StreamMapFields field) => field switch
    {
        StreamMapFields.Path => "archive path",
        StreamMapFields.Entity => "instance name",
        StreamMapFields.LineName => "line name",
        StreamMapFields.GroupName => "group name",
        _ => "shared text",       // one string referenced as more than one kind of field
    };

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        string path = PathBox.Text.Trim();
        if (_file == null || _preview == null) return;
        try
        {
            // Read again and replaced from what is on disk now: the preview may be minutes old.
            byte[] original = File.ReadAllBytes(path);
            StreamMapPatch patch = StreamMapEditor.Replace(original, FindBox.Text, ReplaceBox.Text, Fields, dryRun: false);
            int applied = patch.Edits.Count(x => x.Applied);
            if (patch.Patched == null || applied == 0)
            {
                ReadFile();
                return;
            }
            string backup = StreamMapEditor.WriteWithBackup(path, original, patch.Patched);
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Stream Map",
                Icon = DialogIcon.Success,
                Heading = $"Replaced {applied} string(s)",
                Text = $"The file as it was is kept as {Path.GetFileName(backup)}, beside it."
                    + (patch.Edits.Count > applied ? $"\n\n{patch.Edits.Count - applied} match(es) were left alone: the new text did not fit." : ""),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Formats.FileFormatException or ArgumentException)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Stream Map",
                Icon = DialogIcon.Error,
                Heading = "The file was not written",
                Text = ex.Message,
            });
        }
        ReadFile();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
