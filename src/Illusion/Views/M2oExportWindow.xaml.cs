using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Illusion.Assets;
using Illusion.Assets.Sds;

namespace Illusion.Views;

public partial class M2oExportWindow : Window
{
    private readonly IReadOnlyList<FileInfo> _archives;
    private readonly Action _save;
    private bool _busy;
    private string? _exportedFolder;

    internal M2oExportWindow(IReadOnlyList<FileInfo> archives, bool unsaved, Action save)
    {
        InitializeComponent();
        _archives = archives.ToArray();
        _save = save;
        ScopeText.Text = $"{archives.Count} edited archive(s) · native SDS targets";
        ArchiveList.ItemsSource = archives.Select(a => new { Target = Describe(a), Source = a.FullName }).ToArray();
        FolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "m2o-map-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        SaveText.Text = unsaved ? "Pending edits will be saved to the working copy before export." : "Saved working copies are ready to export.";
        ExportButton.Content = unsaved ? "Save and export" : "Export map";
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e) => e.Cancel = _busy;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the parent folder for a new map export" };
        if (picker.ShowDialog(this) == true)
            FolderBox.Text = Path.Combine(picker.FolderName, "m2o-map-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        ExportButton.IsEnabled = BrowseButton.IsEnabled = FolderBox.IsEnabled = CloseButton.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = "Saving working copies…";
        try
        {
            string destination = Path.GetFullPath(FolderBox.Text.Trim());
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Choose a new folder name; existing exports are kept intact.");
            _save();
            SaveText.Text = "Working copies saved.";
            ExportButton.Content = "Export map";
            var progress = new Progress<string>(message => StatusText.Text = message);
            IReadOnlyList<M2oExportResult> results = await Task.Run(() => M2oMapExporter.Export(_archives, destination, progress));
            if (results.Count == 0)
            {
                StatusText.Text = "The working copies match the original archives. No patches or export folder were created.";
                return;
            }
            _exportedFolder = destination;
            StatusText.Text = $"Exported {results.Count} archive(s); skipped {_archives.Count - results.Count} unchanged.\n\nCopy the stream folder into your server resource (resources\\<name>\\stream). Reconnect clients after changing a map.\n\n" +
                string.Join("\n", results.Select(Summary));
            OpenButton.Visibility = Visibility.Visible;
            CloseButton.Content = "Done";
            ExportButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Export failed: " + ex.Message + "\nNo map export was published. Your saved working copies remain available.";
        }
        finally
        {
            _busy = false;
            ExportButton.IsEnabled = BrowseButton.IsEnabled = FolderBox.IsEnabled = _exportedFolder is null;
            CloseButton.IsEnabled = true;
            ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private static string Describe(FileInfo archive)
    {
        string target = "/" + Path.GetRelativePath(MafiaEnvironment.PcFolder, archive.FullName).Replace('\\', '/').ToLowerInvariant();
        return StockArchives.IsStock(target) ? target + "  ·  patch" : target + "  ·  new archive";
    }

    private static string Summary(M2oExportResult result)
    {
        string what = result.Diff is { } diff ? $"patch, {diff.Changed} changed, {diff.Removed} removed, {diff.Added} added" : "new archive";
        return $"{result.Target}: {what}" + (result.Materials > 0 ? $", {result.Materials} material(s)" : "");
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_exportedFolder!) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = $"Could not open the folder: {ex.Message}\n{_exportedFolder}"; }
    }
}
