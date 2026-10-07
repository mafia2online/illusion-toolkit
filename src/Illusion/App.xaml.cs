using System.Globalization;
using System.Windows;
using Illusion.Diagnostics;
using Illusion.Mcp;
using Illusion.Settings;
using Illusion.Updates;
using Illusion.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Illusion;

public partial class App : Application
{
    /// <summary>
    /// The MCP server, shared by every window. It belongs to the application rather than to a window
    /// because there is no single main window: the launcher and the editor replace one another as the
    /// user moves between them, and the server has to outlive both. Null only in probe runs, which
    /// never reach the UI. Reached statically, in the same spirit as <see cref="UserSettings.Current"/> —
    /// this codebase has no DI container to hang it from.
    /// </summary>
    public static McpServerHost? McpServer { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Game files must be culture-independent: the legacy resource (de)compilers still format and
        // parse numbers through the current culture (upstream MafiaToolkit forced a fixed culture at
        // startup for the same reason — on e.g. a Russian locale, floats would otherwise extract as
        // "0,34" and fail to repack). UI culture is left alone; this only pins number/date formatting.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        // Headless probes (--probe-*): diagnose load chains without UI,
        // reports are written to %TEMP%\illusion_*.txt.
        if (ProbeRunner.TryRun(e.Args))
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // One theme, always dark. Everything the toolkit paints itself — the editor panels, the shared
        // styles in EditorStyles.xaml, the custom controls — is written for a dark surface, so the Fluent
        // chrome around it has to be dark too; following the OS setting would frame those panels in light
        // chrome on a light Windows. ThemeMode is still [Experimental] in WPF (WPF0001); we opt in
        // deliberately and keep the suppression scoped to this one call. Set before any window is shown,
        // the updater's included.
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001

        // Installing an update: this build was started out of its staging folder by the copy that is on its
        // way out, to replace that copy's files (see UpdateInstaller). Nothing else belongs in this process —
        // least of all the MCP server, whose port the outgoing toolkit is still holding.
        if (UpdateInstaller.TryReadApplyRequest(e.Args, out ApplyRequest? applyRequest))
        {
            new ApplyUpdateWindow(applyRequest!).Show();
            return;
        }

        // A collision cook that was killed or crashed leaves its scratch directory behind, and so does an
        // update that has been installed; nothing else ever deletes either. Best-effort, off the critical path.
        Task.Run(Assets.Collisions.PhysXCooker.SweepStaleScratch);
        Task.Run(UpdateInstaller.SweepStaging);

        StartMcpServer();

        // Start from the launcher (path selection + unpacking), not straight into the editor.
        new LauncherWindow().Show();
    }

    /// <summary>
    /// Brings the MCP server up. Deliberately not awaited: binding a socket is quick, but the
    /// launcher must appear regardless, and it follows the server through
    /// <see cref="McpServerHost.StateChanged"/> anyway. Starting cannot throw.
    /// </summary>
    private void StartMcpServer()
    {
        UserSettings settings = UserSettings.Current;
        McpServer = new McpServerHost(new McpHostOptions
        {
            // settings.json is hand-edited, so the value has to be treated as untrusted. Zero would
            // mean "any free port", handing out a different address every launch; anything outside
            // the TCP range would fail the bind with a message about argument ranges. Neither is
            // worth showing the user — come up on the default port, which the launcher displays.
            Port = settings.McpPort is > 0 and <= 65535 ? settings.McpPort : McpHostOptions.DefaultPort,

            // Where tools get their hands on the application: the UI marshal for anything that will
            // touch the scene or the viewport, the game environment the file-browsing tools report
            // paths from, and the running editor itself — the area, the selection, the camera, the
            // Blender session — for the tools that drive it.
            ConfigureServices = services => services
                .AddSingleton<IUiThreadMarshal>(new WpfUiThreadMarshal(Dispatcher))
                .AddSingleton<IGameEnvironment, AppGameEnvironment>()
                .AddSingleton<IEditorSession, AppEditorSession>(),
        });

        _ = McpServer.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Waiting here is what releases the port before the process goes; the windows are already
        // gone, so the pause is invisible, and it measures in milliseconds. The outer cap is a
        // safety net only — never let a stuck shutdown strand the process with no window to close.
        _ = McpServer?.StopAsync(TimeSpan.FromSeconds(3)).Wait(TimeSpan.FromSeconds(5));
        base.OnExit(e);
    }
}
