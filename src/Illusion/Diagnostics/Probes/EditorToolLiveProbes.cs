using System.IO;
using System.Text;
using Illusion.Mcp;
using Illusion.Views;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The editor tools against the editor itself: a real window with a district loaded, driven through the same
/// session object the MCP tools call. What the scripted editor of <see cref="EditorToolProbes"/> cannot
/// answer — whether a guard really sees the edits it is there to protect. Nothing is saved.
/// </summary>
internal static class EditorToolLiveProbes
{
    // Output: %TEMP%\illusion_editor_tools_live.txt
    internal static void Run(string area)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_editor_tools_live.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        MainWindow? window = null;
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            window = new MainWindow
            {
                WindowState = System.Windows.WindowState.Normal, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -4000, Top = 0, Width = 1280, Height = 800, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            var session = new AppEditorSession();
            Pump(() => session.Areas().Count > 0, 60);
            string? refused = session.LoadArea(area, winter: false, discardUnsavedEdits: true);
            Pump(() => session.Status() is { Loading: false, Meshes: > 0 }, 240);
            Check("the district loads in a window of this process", refused == null && session.Status() is { Loading: false, Meshes: > 0 },
                refused ?? $"{session.Status().Meshes} meshes");
            if (session.Status() is not { Loading: false, Meshes: > 0 }) return;

            // ── numbers that are not numbers ──
            SceneObjectInfo? mesh = session.Find(null, "Mesh", null, null, 1).FirstOrDefault();
            Check("the scene has a mesh to name", mesh != null);
            if (mesh != null)
            {
                Check("object_move refuses a position that is not finite",
                    session.Move(mesh.Name, [float.PositiveInfinity, 0f, 0f], null) != null && !session.Status().UnsavedEdits);
                Check("and an offset that is not finite",
                    session.Move(mesh.Name, null, [0f, float.NaN, 0f]) != null && !session.Status().UnsavedEdits);
            }
            Check("actor_import refuses a position that is not finite",
                session.ImportActor(Path.Combine(Path.GetTempPath(), "illusion_no_such.act"), "x", "y", [0f, float.NegativeInfinity, 0f])
                    is { } why && why.Contains("finite"));

            // ── a layer switched off under unsaved edits ──
            Check("the crash layer switches on", session.SetView(null, null, crash: true, null, null) == null);
            Pump(() => session.Find(null, "CrashInstance", null, null, 1).Count > 0, 90);
            SceneObjectInfo? copy = session.Find(null, "CrashInstance", null, null, 1).FirstOrDefault();
            Check("a crash copy is found", copy != null, copy?.Path ?? "");
            if (copy != null)
            {
                Check("with nothing edited, switching the layer off and on is allowed",
                    session.SetView(null, null, crash: false, null, null) == null
                    && session.SetView(null, null, crash: true, null, null) == null);
                Pump(() => session.Find(null, "CrashInstance", null, null, 1).Count > 0, 90);
                copy = session.Find(null, "CrashInstance", null, null, 1).FirstOrDefault();
                string? moved = copy == null ? "no copy after the layer came back" : session.Move(copy.Path, null, [0f, 0f, 0.25f]);
                Check("the copy is moved and the scene has unsaved edits", moved == null && session.Status().UnsavedEdits, moved ?? "");
                string? guarded = session.SetView(null, null, crash: false, null, null);
                Check("switching the crash layer off is refused while that move is unsaved",
                    guarded != null && session.Status().UnsavedEdits && window.CrashToggle.IsChecked == true, guarded ?? "switched off");
                Check("switching another layer ON is not what the guard is about",
                    session.SetView(null, null, null, zones: true, null) == null);
                Check("nor is the collision layer, which is only hidden: it switches on and off with the edit unsaved",
                    session.SetView(null, collision: true, null, null, null) == null
                    && session.SetView(null, collision: false, null, null, null) == null && session.Status().UnsavedEdits);
                Check("a move further than a position can be is refused, though each number is finite",
                    mesh != null && session.Move(mesh.Name, [3e38f, 0f, 0f], [3e38f, 0f, 0f]) != null);
                Check("and with the edits given up it is switched off",
                    session.SetView(null, null, crash: false, null, null, discardUnsavedEdits: true) == null
                    && window.CrashToggle.IsChecked != true);
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            try { window?.Close(); }
            catch (Exception ex) { sb.AppendLine("    (closing the window: " + ex.Message + ")"); }
            sb.Insert(0, $"EDITOR TOOLS LIVE PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static void Pump(Func<bool> until, int seconds)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        while (!until() && DateTime.UtcNow < end)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(15);
        }
    }
}
