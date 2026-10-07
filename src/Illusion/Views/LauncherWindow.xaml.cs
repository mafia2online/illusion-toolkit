using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Mcp;
using Illusion.Settings;
using Illusion.Updates;
using Microsoft.Win32;

namespace Illusion.Views;

/// <summary>
/// Startup window: pick the game path, unpack all resources into <c>&lt;game&gt;\resources</c> (mandatory),
/// then enter the Map Editor (the 3D window). Resource Editor is a stub for now.
/// </summary>
public partial class LauncherWindow : Window
{
    private readonly McpServerHost? _mcp = App.McpServer;
    private DispatcherTimer? _copiedTimer;
    private bool _busy;

    /// <summary>The release the download button would install, or null while there is nothing to install.</summary>
    private ReleaseInfo? _update;

    public LauncherWindow()
    {
        InitializeComponent();

        // Subscribed here rather than in Loaded: the server starts before this window does, so its
        // first state changes can land before Loaded ever runs.
        if (_mcp is not null)
        {
            _mcp.StateChanged += OnMcpStateChanged;
            ShowMcpState(_mcp.State);
        }

        // The launcher is built anew each time the editor hands control back, so a handler left on
        // the application-lifetime server would pin every window that ever existed.
        Closed += (_, _) =>
        {
            if (_mcp is not null) _mcp.StateChanged -= OnMcpStateChanged;
            _copiedTimer?.Stop();
        };

        PathBox.LostFocus += (_, _) => CommitGamePath();

        Loaded += (_, _) =>
        {
            // Prefill with the saved path — the same setting the settings window edits.
            PathBox.Text = UserSettings.Current.GamePath ?? "";
            RefreshState();

            // Not awaited: whether a newer release exists has nothing to do with opening the game, and the
            // check can only ever ADD the button. UpdateChecker remembers the answer for the session, so
            // coming back here from the editor costs no second request.
            _ = LookForUpdateAsync();
        };
    }

    // Raised on a thread-pool thread — hop to the dispatcher before touching the controls.
    private void OnMcpStateChanged(McpServerState state) =>
        Dispatcher.BeginInvoke(() => ShowMcpState(state));

    private void ShowMcpState(McpServerState state)
    {
        _copiedTimer?.Stop();

        switch (state.Status)
        {
            case McpServerStatus.Running:
                McpDot.Fill = Palette.StatusOk;
                McpAddressText.Text = state.Address;
                McpStateText.Text = "MCP running";
                McpAddressPanel.Cursor = Cursors.Hand;
                McpAddressPanel.ToolTip = "Click to copy the MCP server address";
                break;

            case McpServerStatus.Starting:
                McpDot.Fill = Palette.StatusWarn;
                // Nothing to put beside "starting…" yet, and repeating the word would just be noise.
                McpAddressText.Text = "";
                McpStateText.Text = "MCP starting…";
                McpAddressPanel.Cursor = null;
                McpAddressPanel.ToolTip = null;
                break;

            case McpServerStatus.Failed:
                McpDot.Fill = Palette.StatusError;
                McpAddressText.Text = state.Error;
                McpStateText.Text = "MCP failed";
                McpAddressPanel.Cursor = null;
                McpAddressPanel.ToolTip = state.Error;
                break;

            default:
                McpDot.Fill = Palette.StatusIdle;
                McpAddressText.Text = "";
                McpStateText.Text = "MCP stopped";
                McpAddressPanel.Cursor = null;
                McpAddressPanel.ToolTip = null;
                break;
        }
    }

    // Copying beats retyping a URL by hand, and the address is what every client needs.
    private void McpAddress_Click(object sender, MouseButtonEventArgs e)
    {
        McpServerState? state = _mcp?.State;
        if (state is not { Status: McpServerStatus.Running, Address: { } address }) return;

        try
        {
            Clipboard.SetText(address);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process can hold the clipboard open; nothing to do but skip the feedback.
            return;
        }

        McpAddressText.Text = "copied to clipboard";
        _copiedTimer ??= new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _copiedTimer.Interval = TimeSpan.FromSeconds(1.2);
        _copiedTimer.Tick -= RestoreAddress;
        _copiedTimer.Tick += RestoreAddress;
        // Start() on a timer that is already running does nothing — it would keep the deadline from
        // the previous click, cutting this one's confirmation short. Stopping first rewinds it.
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void RestoreAddress(object? sender, EventArgs e)
    {
        _copiedTimer?.Stop();
        if (_mcp is not null) ShowMcpState(_mcp.State);
    }

    // Drives the three mutually-exclusive status panels (warn / ready / unpack). The status bar is
    // not touched here — it belongs to the MCP server and follows its own events.
    private void RefreshState()
    {
        if (_busy) return;

        string? root = MafiaEnvironment.ResolveGameRoot(PathBox.Text?.Trim());
        bool validPath = root != null && Directory.Exists(root);
        bool unpacked = validPath && File.Exists(Path.Combine(root!, "resources", ".unpacked"));

        BrowseBtn.IsEnabled = true;
        UnpackBtn.IsEnabled = validPath && !unpacked;
        MapEditorBtn.IsEnabled = unpacked;
        ResourcesEditorBtn.IsEnabled = unpacked;

        // Idle: the progress panel is always hidden here.
        WorkPanel.Visibility = Visibility.Collapsed;

        if (unpacked)
        {
            // Clean, friendly ready state — no progress bar, no "Resources unpacked:" line.
            WarnPanel.Visibility = Visibility.Collapsed;
            ReadyPanel.Visibility = Visibility.Visible;
            ReadyPathText.Text = Path.Combine(root!, "resources");
        }
        else
        {
            ReadyPanel.Visibility = Visibility.Collapsed;
            WarnPanel.Visibility = Visibility.Visible;
            WarnText.Text = validPath
                ? "The game is not unpacked. Click the unpack button before opening the editors."
                : string.IsNullOrWhiteSpace(PathBox.Text)
                    ? "No game folder chosen yet — pick it with the folder button, or type the path."
                    : "Enter a valid Mafia II install path (the pc folder or the game root).";
        }
    }

    // Shows the warn panel with an ad-hoc message (WarnText lives inside WarnPanel now, so error
    // paths can't just toggle its Visibility — they route through here).
    private void ShowWarn(string message)
    {
        ReadyPanel.Visibility = Visibility.Collapsed;
        WorkPanel.Visibility = Visibility.Collapsed;
        WarnPanel.Visibility = Visibility.Visible;
        WarnText.Text = message;
    }

    // Takes the card over for a run that has a progress bar (unpacking, downloading an update).
    private void ShowWork(string status)
    {
        WarnPanel.Visibility = Visibility.Collapsed;
        ReadyPanel.Visibility = Visibility.Collapsed;
        WorkPanel.Visibility = Visibility.Visible;
        WorkProgress.Maximum = 1;
        WorkProgress.Value = 0;
        WorkStatus.Text = status;
    }

    private void Path_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshState();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select the Mafia II 'pc' folder or install root",
        };
        if (!string.IsNullOrWhiteSpace(PathBox.Text) && Directory.Exists(PathBox.Text))
        {
            dlg.InitialDirectory = PathBox.Text;
        }
        if (dlg.ShowDialog(this) == true)
        {
            PathBox.Text = dlg.FolderName; // TextChanged → RefreshState
            CommitGamePath();
        }
    }

    // The gear: everything else that is configurable. It can change the game folder too, so the box is
    // re-read on the way back — and so is the state, since the folder may have been unpacked meanwhile.
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        CommitGamePath();   // a path typed here and not yet committed must not be overwritten by the old one

        var settings = new SettingsWindow { Owner = this };
        settings.SelectSection(SettingsSection.General);
        settings.ShowDialog();

        PathBox.Text = UserSettings.Current.GamePath ?? "";
        RefreshState();
    }

    // Both this window and the settings window write the same setting; whoever changed it last wins, and the
    // other re-reads it when it is next looked at. Committed on focus loss rather than per keystroke, so a
    // path halfway through being typed is never the one the probes are handed.
    private void CommitGamePath()
    {
        string path = PathBox.Text.Trim();
        if (UserSettings.Current.GamePath == path) return;
        UserSettings.Update(s => s.GamePath = path);
    }

    // Initializes the environment with the chosen path (idempotent). Needed before unpack and Map Editor,
    // and the last chance to persist a path that was typed and acted on without ever losing focus.
    private bool EnsureEnv(out string? error)
    {
        CommitGamePath();
        return MafiaEnvironment.TryInitialize(PathBox.Text.Trim(), out error);
    }

    // Shows the dedicated UnpackPanel (progress + status) for the duration of the run; RefreshState()
    // in finally hides it and flips to the ready banner on success. This is the ONLY place the
    // progress bar is ever shown — the already-unpacked state never displays it.
    private async void Unpack_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEnv(out string? err))
        {
            ShowWarn("Could not open the game: " + err);
            return;
        }

        // Changing the game folder mid-unpack would leave the run writing into the old one.
        SetBusy(true);
        ShowWork("Preparing…");

        var progress = new Progress<(int done, int total, string name)>(p =>
        {
            WorkProgress.Maximum = Math.Max(1, p.total);
            WorkProgress.Value = p.done;
            WorkStatus.Text = $"{p.done}/{p.total} · {p.name}";
        });

        string? unpackError = null;
        try
        {
            await Task.Run(() => ResourceUnpacker.UnpackAll(progress, CancellationToken.None));
            WorkStatus.Text = "Unpacking complete.";
        }
        catch (Exception ex)
        {
            unpackError = ex.Message;
        }
        finally
        {
            SetBusy(false);
            RefreshState(); // hides WorkPanel, shows the ready banner on success

            // RefreshState hides WorkPanel — show the error on top, otherwise it would vanish instantly.
            if (unpackError != null) ShowWarn("Unpack failed: " + unpackError);
        }
    }

    // Everything that must not be touched while a long run owns the window. _busy also makes RefreshState a
    // no-op, so it is cleared BEFORE the refresh that ends the run — and that refresh is what puts the
    // path-dependent buttons back, which is why leaving the run does not re-enable them here.
    private void SetBusy(bool busy)
    {
        _busy = busy;
        SettingsBtn.IsEnabled = !busy;
        UpdateBtn.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        if (busy)
        {
            BrowseBtn.IsEnabled = false;
            UnpackBtn.IsEnabled = false;
            MapEditorBtn.IsEnabled = false;
            ResourcesEditorBtn.IsEnabled = false;
        }
    }

    // ── Updates ──

    private async Task LookForUpdateAsync()
    {
        if (!UserSettings.Current.CheckUpdatesOnStartup) return;
        ShowUpdate(await UpdateChecker.CheckAsync());
    }

    /// <summary>
    /// Puts a check result on screen — which here means the download button, and only when there is something
    /// to download. A failed check shows nothing at all: no network is a normal way to run the toolkit, and
    /// anyone who wants the reason can press the check in the settings, which does report it.
    /// </summary>
    internal void ShowUpdate(UpdateCheckResult result)
    {
        _update = result.HasUpdate ? result.Release : null;
        UpdateBtn.Visibility = _update is null ? Visibility.Collapsed : Visibility.Visible;
        if (_update is not null)
        {
            UpdateBtn.ToolTip =
                $"Version {_update.Version} is out ({_update.AssetSizeText}) — click to install it";
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } release || _busy) return;

        // Asked before a byte is downloaded: a folder that cannot be written to is not going to become
        // writable afterwards, and the release page is the honest fallback.
        if (!UpdateInstaller.CanInstall(out string reason))
        {
            DialogOutcome refusal = AppDialog.Show(this, new DialogOptions
            {
                Icon = DialogIcon.Info,
                Heading = $"Version {release.Version} is out",
                Text = reason,
                Buttons = DialogButtons.YesCancel,
                ConfirmText = "Open the release",
                CancelText = "Close",
            });
            if (refusal.Confirmed) OpenInBrowser(release.PageUrl);
            return;
        }

        // A download declined at the restart prompt is still on disk; asking again must not fetch it twice.
        StagedUpdate? staged = UpdateDownloader.ReadyFor(release);
        string? error = null;
        if (staged is null)
        {
            SetBusy(true);
            ShowWork($"Downloading {release.AssetName}…");

            var progress = new Progress<DownloadProgress>(p =>
            {
                WorkProgress.Maximum = Math.Max(1, p.Total);
                WorkProgress.Value = p.Received;
                WorkStatus.Text = p.Total > 0
                    ? $"{Megabytes(p.Received)} / {Megabytes(p.Total)} MB"
                    : $"{Megabytes(p.Received)} MB";
            });

            try
            {
                staged = await UpdateDownloader.DownloadAsync(release, progress);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                           or UnauthorizedAccessException or OperationCanceledException)
            {
                error = ex.Message;
            }
            finally
            {
                SetBusy(false);
                RefreshState();
            }
        }

        if (staged is null)
        {
            // RefreshState has just repainted the card — the failure goes on top of it, as unpacking's does.
            ShowWarn("The update could not be downloaded: " + error);
            return;
        }

        DialogOutcome outcome = AppDialog.Show(this, new DialogOptions
        {
            Icon = DialogIcon.Success,
            Heading = $"Version {staged.Version} is ready",
            Text = "The toolkit closes, replaces its own files and opens again. It takes a moment.",
            Buttons = DialogButtons.YesCancel,
            ConfirmText = "Restart now",
            CancelText = "Later",
        });
        if (!outcome.Confirmed) return;

        try
        {
            UpdateInstaller.Start(staged);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            ShowWarn("The downloaded toolkit would not start: " + ex.Message);
            return;
        }

        // Nothing else may run now — the staged copy is waiting for this process to let go of its files.
        Application.Current.Shutdown();
    }

    private static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Hands a URL to the shell. Shared with the settings window, which links to the same pages.</summary>
    internal static void OpenInBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No browser registered for http — there is nothing useful to say about that here.
        }
    }

    private void MapEditor_Click(object sender, RoutedEventArgs e) => OpenEditor(() => new MainWindow());

    private void ResourcesEditor_Click(object sender, RoutedEventArgs e) =>
        OpenEditor(() => new ResourceEditorWindow());

    // Both tiles do the same thing: make sure the environment is ready, hand the app over to the chosen
    // editor and step out of the way. Whichever one is opened becomes the application's main window — the
    // launcher is the way IN, not a window that stays around behind the editor.
    private void OpenEditor(Func<Window> create)
    {
        if (TryOpenEditor(create) is { } error) ShowWarn(error);
    }

    /// <summary>
    /// The map editor tile, for a caller that cannot click it — the MCP server. Returns why it could not be
    /// opened instead of showing it, null once the editor is up.
    /// </summary>
    internal string? OpenMapEditor() =>
        MapEditorBtn.IsEnabled
            ? TryOpenEditor(() => new MainWindow())
            : "The launcher is not ready: the game path is not set, or the game has not been unpacked yet.";

    private string? TryOpenEditor(Func<Window> create)
    {
        // Initialize with the chosen path BEFORE opening the viewport (it calls TryInitialize again
        // and reuses the ready environment).
        if (!EnsureEnv(out string? err)) return "Could not open the game: " + err;

        Window editor = create();
        Application.Current.MainWindow = editor;
        editor.Show();
        Close();
        return null;
    }
}
