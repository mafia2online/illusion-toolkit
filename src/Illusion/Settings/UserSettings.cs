using System.IO;
using System.Text.Json;

namespace Illusion.Settings;

/// <summary>
/// Application user settings (JSON in <c>%LOCALAPPDATA%\Illusion</c>) — the one place anything
/// configurable is kept: the game path, the Blender bridge and MCP options, and the rebound keys
/// (<see cref="Hotkeys"/>). The settings window edits this object and nothing else.
/// <para>
/// Read through <see cref="Current"/> and write through <see cref="Update"/>. The instance is shared, so an
/// edit is visible everywhere the moment it is made — a file re-read per call site could not do that. Whoever
/// has to ACT on a change (re-bind keys, re-read the game path) follows the thing that changed rather than
/// this object: see <see cref="HotkeyMap.Changed"/>. <see cref="Load"/> stays public for the headless probes,
/// which run in their own process and only ever read.
/// </para>
/// </summary>
public sealed class UserSettings
{
    public string? GamePath { get; set; }

    /// <summary>When set, a successful Build no longer pops its "Built N archives" notice (the user ticked
    /// "Don't show this again"). Failures are always reported regardless.</summary>
    public bool SuppressBuildNotice { get; set; }

    /// <summary>Explicit blender.exe path (or its folder) for the Blender bridge. When unset the
    /// bridge auto-detects: .blend association → Program Files → Steam → PATH.</summary>
    public string? BlenderPath { get; set; }

    /// <summary>Blender bridge: also push edits automatically when leaving Edit Mode (the addon's
    /// N-panel toggle mirrors this; the panel button always works regardless).</summary>
    public bool BridgeAutoPush { get; set; } = true;

    /// <summary>Loopback port for the embedded MCP server, whose address the launcher displays.
    /// Only worth changing if something else on the machine already owns the default.</summary>
    public int McpPort { get; set; } = Mcp.McpHostOptions.DefaultPort;

    /// <summary>Whether the launcher asks GitHub for a newer release when it opens. One request, in the
    /// background, and a failure is silent — the download button only ever appears when there really is
    /// something to download. Off leaves the settings window's own check, which is always available.</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    private List<string>? _rawSceneTree;

    /// <summary>
    /// The archives whose hierarchy the user left on RAW — file names, lower-cased, without the folder.
    ///
    /// <para>
    /// A car opens on its components, so only the DEVIATIONS are stored: an archive absent from this list is
    /// one the modder never switched, and a car that gains a component view in a later build therefore opens
    /// on it rather than inheriting an empty preference. Keyed by the file's name and not its path, because
    /// the same car under a reinstalled game is the same car.
    /// </para>
    /// </summary>
    public List<string> RawSceneTree
    {
        // Never null, whoever wrote the file: "RawSceneTree": null is as legal in JSON as a missing key.
        get => _rawSceneTree ??= [];
        set => _rawSceneTree = value;
    }

    private Dictionary<string, string>? _hotkeys;

    /// <summary>
    /// Rebound keys: <see cref="HotkeyId"/> name → gesture text (<c>"Ctrl+Shift+Z"</c>, <c>"Num /"</c>,
    /// <c>""</c> for unbound). Only the actions that DIFFER from the built-in default are stored, which is
    /// what lets a new action ship with a working default instead of inheriting an empty binding from an
    /// older settings.json. Unknown names and unparseable gestures are ignored on load, so a file written
    /// by a newer build still opens. See <see cref="HotkeyMap"/>.
    /// </summary>
    public Dictionary<string, string> Hotkeys
    {
        // Never null, whoever wrote the file: "Hotkeys": null is as legal in JSON as a missing key.
        get => _hotkeys ??= new Dictionary<string, string>(StringComparer.Ordinal);
        set => _hotkeys = value;
    }

    private static string SettingsFile => Environment.GetEnvironmentVariable("ILLUSION_SETTINGS_FILE") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Illusion", "settings.json");

    /// <summary>The live settings every window and the bridge read. Loaded once, on first touch.</summary>
    public static UserSettings Current { get; } = Load();

    /// <summary>Applies a change to <see cref="Current"/> and writes it out. The two belong together: the
    /// single write path is what keeps a setting from being changed in memory and lost on exit.</summary>
    public static void Update(Action<UserSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        change(Current);
        Current.Save();
    }

    /// <summary>Reads settings from disk; a corrupt/missing file yields clean settings rather than a crash.</summary>
    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsFile)) ?? new UserSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Settings are non-critical — start with empty ones.
        }
        return new UserSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            // Temp-then-move so a crash mid-write (or a concurrent headless probe reading the file) can never
            // observe a truncated settings.json and silently drop the saved game path.
            string tmp = SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, SettingsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are non-critical — a failed write must not crash the application.
        }
    }
}
