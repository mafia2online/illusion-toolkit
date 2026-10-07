using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Viewport;
using Microsoft.Win32;

namespace Illusion.Views;

/// <summary>
/// The Build window: the archives about to be packed, each with a tick and with the files of its working copy
/// that differ from the archive the game has now (<see cref="WorkingCopyDiff"/>). Nothing is packed until Build
/// is pressed, and only what is ticked.
/// <para>
/// It exists because a Build packs a WHOLE working copy: a file changed in that folder in an earlier session
/// goes into the game beside today's edit. Such files are counted out loud, per archive and for the build.
/// </para>
/// </summary>
public sealed partial class BuildWindow : Window
{
    private static readonly Brush Plain = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xE3, 0xA6, 0x4B));

    /// <summary>One file a Build would make different in the game.</summary>
    public sealed record FileRow(string Kind, string Path, string Size, string When, Brush WhenBrush);

    /// <summary>One archive on the list.</summary>
    public sealed class ArchiveRow : INotifyPropertyChanged
    {
        private bool _checked, _open, _comparing = true;
        private string _summary = "Comparing with the game…", _caution = "";
        private IReadOnlyList<FileRow> _files = [];

        public ArchiveRow(FileInfo archive, bool isChecked)
        {
            Archive = archive;
            _checked = isChecked;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public FileInfo Archive { get; }
        public string Name => Archive.Name;
        public string Path => Archive.FullName;

        /// <summary>Files written before this session started, and the oldest of them.</summary>
        public int Old { get; set; }
        public DateTime Oldest { get; set; }

        public bool IsChecked { get => _checked; set => Set(ref _checked, value, nameof(IsChecked)); }
        public bool IsOpen { get => _open; set { Set(ref _open, value, nameof(IsOpen)); Raise(nameof(OpenVisibility)); } }
        public bool Comparing { get => _comparing; set => Set(ref _comparing, value, nameof(Comparing)); }
        public string Summary { get => _summary; set => Set(ref _summary, value, nameof(Summary)); }
        public string Caution { get => _caution; set { Set(ref _caution, value, nameof(Caution)); Raise(nameof(CautionVisibility)); } }

        public IReadOnlyList<FileRow> Files
        {
            get => _files;
            set
            {
                _files = value;
                Raise(nameof(Files));
                Raise(nameof(FilesVisibility));
                Raise(nameof(OpenVisibility));
            }
        }

        public Visibility CautionVisibility => _caution.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FilesVisibility => _files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility OpenVisibility => _open && _files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        private void Set<T>(ref T field, T value, string name)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            Raise(name);
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly ObservableCollection<ArchiveRow> _rows = [];
    // "This session" is the life of this process: what was written before it started was not made in it.
    private readonly DateTime _session = Process.GetCurrentProcess().StartTime;

    public BuildWindow(IEnumerable<FileInfo> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        InitializeComponent();
        ArchiveList.ItemsSource = _rows;
        foreach (FileInfo archive in pending) Add(archive);
        Refresh();
    }

    /// <summary>The archives ticked when Build was pressed; empty when the window was cancelled.</summary>
    public IReadOnlyList<FileInfo> Chosen { get; private set; } = [];

    /// <summary>
    /// The whole Build, as both editors run it: save (a Build always packs what is on disk, so the list has to be
    /// of what is on disk), show the window, pack what was ticked. Null when nothing was built - the save did
    /// not complete (said in a dialog), or the window was cancelled.
    /// </summary>
    public static D3DImageHost.BuildReport? Run(Window owner, D3DImageHost host)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(host);
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            D3DImageHost.SaveReport saved = host.SaveEditsReport();
            Mouse.OverrideCursor = null;
            if (!saved.Complete)
            {
                AppDialog.Show(owner, new DialogOptions
                {
                    Title = "Build",
                    Icon = DialogIcon.Warning,
                    Heading = "Nothing was built",
                    Text = "The save a Build starts with did not complete:\n\n" + string.Join("\n", saved.NotSaved.Select(n => "• " + n)),
                });
                return null;
            }

            var window = new BuildWindow(host.PendingBuildArchives()) { Owner = owner };
            if (window.ShowDialog() != true || window.Chosen.Count == 0) return null;

            Mouse.OverrideCursor = Cursors.Wait;
            return host.BuildArchives(window.Chosen, createBackup: true);   // backups are always kept (versioned in a "backups" folder)
        }
        // Everything: this runs straight off a menu click, with nothing above it but the program's end, and a
        // working copy changed by hand can fail in ways no list of exception types names (a contents list that
        // is no longer XML, for one). A Build that fails says so; it does not take the editor with it.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Mouse.OverrideCursor = null;
            AppDialog.Show(owner, new DialogOptions { Title = "Build", Icon = DialogIcon.Error, Heading = "Build failed", Text = ex.Message });
            return null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void Add(FileInfo archive)
    {
        var row = new ArchiveRow(archive, isChecked: true);
        // The footer follows the tick itself, not a click on it: a tick set from the keyboard, by a screen
        // reader or by the comparison (nothing differs - unticked) changes what Build will do just the same.
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArchiveRow.IsChecked)) Refresh();
        };
        _rows.Add(row);
        _ = Compare(row);
    }

    // The comparison unpacks the game's archive into %TEMP% - a second or so for a district - off the UI thread.
    private async Task Compare(ArchiveRow row)
    {
        WorkingCopyComparison? found = null;
        string? error = null;
        try
        {
            found = await Task.Run(() => WorkingCopyDiff.Compare(row.Archive));
        }
        // Whatever it is: a comparison that ended in an exception nobody caught left its row on "Comparing…"
        // for good, and with it the Build button for every archive in the list.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.Message;
        }

        if (found == null)
        {
            // Not comparing is not a reason to refuse the Build - the archive was built without a list until
            // now - but it is said, and the tick stays the user's.
            row.Summary = "Could not be compared with the game: " + error;
        }
        else
        {
            var old = found.Changes.Where(c => c.Kind != WorkingCopyChangeKind.Removed && c.Modified < _session).ToList();
            row.Old = old.Count;
            row.Oldest = old.Count > 0 ? old.Min(c => c.Modified) : default;
            row.Files = [.. found.Changes.Select(c => new FileRow(
                c.Kind switch { WorkingCopyChangeKind.Added => "new", WorkingCopyChangeKind.Removed => "removed", _ => "changed" },
                c.Path, Bytes(c.Size),
                c.Kind == WorkingCopyChangeKind.Removed ? "" : c.Modified < _session ? c.Modified.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture) : "this session",
                c.Kind != WorkingCopyChangeKind.Removed && c.Modified < _session ? Warn : Plain))];
            int n = found.Changes.Count;
            if (!found.InGame)
            {
                row.Summary = $"Not in the game yet - {n} file(s) make a new archive.";
            }
            else if (n == 0)
            {
                // Packing it would write the same archive again: left unticked, for the user to tick if that is wanted.
                row.Summary = "Nothing differs from the game - packing it would change nothing.";
                row.IsChecked = false;
            }
            else
            {
                int added = found.Changes.Count(c => c.Kind == WorkingCopyChangeKind.Added), removed = found.Changes.Count(c => c.Kind == WorkingCopyChangeKind.Removed);
                row.Summary = $"{n} file(s) differ from the game: {n - added - removed} changed, {added} new, {removed} removed.";
            }
            var cautions = new List<string>();
            if (old.Count > 0)
            {
                cautions.Add($"{old.Count} of them were written before this session, the oldest on {row.Oldest.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} - they go into the game too.");
            }
            if (found.MissingEntries.Count > 0)
            {
                cautions.Add($"{found.MissingEntries.Count} file(s) the archive's list names are missing from the working copy and will be left out: "
                    + string.Join(", ", found.MissingEntries.Take(4)) + (found.MissingEntries.Count > 4 ? ", …" : "") + ".");
            }
            row.Caution = string.Join("\n", cautions);
            row.IsOpen = old.Count > 0 || (n > 0 && n <= 12);      // short lists and anything with an old file in it start open
        }
        row.Comparing = false;
        Refresh();
    }

    private static string Bytes(long size) => size switch
    {
        >= 1 << 20 => (size / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        >= 1 << 10 => (size / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => size.ToString(CultureInfo.CurrentCulture) + " B",
    };

    // The footer and the banner follow the ticks. Build waits for the comparisons: the window is there to be read.
    private void Refresh()
    {
        EmptyLabel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List<ArchiveRow> ticked = [.. _rows.Where(r => r.IsChecked)];
        bool comparing = _rows.Any(r => r.Comparing);
        BuildBtn.IsEnabled = ticked.Count > 0 && !comparing;
        BuildBtn.Content = comparing ? "Comparing…" : ticked.Count switch { 0 => "Build", 1 => "Build 1 archive", _ => $"Build {ticked.Count} archives" };

        int old = ticked.Sum(r => r.Old);
        OldBanner.Visibility = old > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (old > 0)
        {
            DateTime oldest = ticked.Where(r => r.Old > 0).Min(r => r.Oldest);
            OldText.Text = $"{old} file(s) in the ticked archives were last written before this session started (the oldest on "
                + $"{oldest.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}). A Build takes them into the game with the rest - "
                + "look at them below and untick an archive you are not sure of.";
        }
    }

    private void Toggle_Clicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ArchiveRow row) row.IsOpen = !row.IsOpen;
    }

    /// <summary>The rows, for the probe that makes the window and waits for its comparisons.</summary>
    internal IReadOnlyList<ArchiveRow> Rows => _rows;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Archive to pack",
            Filter = "SDS archives (*.sds)|*.sds",
            CheckFileExists = false,       // a new archive has a working copy and no file in the game yet
        };
        if (MafiaEnvironment.IsInitialized) dialog.InitialDirectory = System.IO.Path.Combine(MafiaEnvironment.PcFolder, "sds");
        if (dialog.ShowDialog(this) != true) return;

        var archive = new FileInfo(dialog.FileName);
        if (_rows.FirstOrDefault(r => string.Equals(r.Path, archive.FullName, StringComparison.OrdinalIgnoreCase)) is { } listed)
        {
            listed.IsChecked = true;
            Refresh();
            return;
        }
        string working;
        try
        {
            working = MafiaEnvironment.ExtractedDir(archive);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            working = "";
        }
        if (working.Length == 0 || !File.Exists(System.IO.Path.Combine(working, "SDSContent.xml")))
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Build",
                Icon = DialogIcon.Info,
                Heading = $"{archive.Name} has no working copy",
                Text = "There is nothing to pack: an archive gets a working copy when it is opened in an editor"
                    + (working.Length > 0 ? ", in\n" + working : "") + ".",
            });
            return;
        }
        Add(archive);
        Refresh();
    }

    private void Build_Click(object sender, RoutedEventArgs e)
    {
        Chosen = [.. _rows.Where(r => r.IsChecked).Select(r => r.Archive)];
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
