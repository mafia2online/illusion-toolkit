using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Mcp;
using Illusion.Views;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Pictures of the shipped chrome in the shipped (dark) theme, to be looked at rather than asserted — the other
/// probes' renders come out light, because a window that is never shown is never themed.
/// <para>
/// Each window here IS shown, but far off the desktop, unactivated and out of the taskbar: it gets its theme
/// without ever taking the focus, or the virtual desktop, from whoever is working. Popups stay shut for the
/// same reason — WPF keeps a popup on a screen, so an open one would land on the user's.
/// </para>
/// Output: %TEMP%\illusion_shot_*.png + illusion_shots.txt
/// </summary>
internal static class ShotProbes
{
    internal static void Run(string area, string car)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_shots.txt");
        var sb = new StringBuilder();
        var opened = new List<Window>();
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
#pragma warning disable WPF0001 // ThemeMode is experimental; App.OnStartup pins the same theme after the probe path
            if (Application.Current is { } app) app.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001

            var launcher = Offscreen(new LauncherWindow(), null, null, opened);
            Shot(launcher, "launcher", sb);

            var settings = Offscreen(new SettingsWindow(), null, null, opened);
            foreach (SettingsSection section in Enum.GetValues<SettingsSection>())
            {
                settings.SelectSection(section);
                Shot(settings, "settings_" + section.ToString().ToLowerInvariant(), sb);
            }

            var resources = Offscreen(new ResourceEditorWindow(), 1600, 900, opened);
            Shot(resources, "resource_editor_empty", sb);
            var session = new AppEditorSession();
            string? refused = session.OpenResource("cars/" + car + ".sds", out _);
            Pump(() => session.ResourceStatus() is { Loading: false, Meshes: > 0 }, 180);
            sb.AppendLine(refused ?? $"car {car}: {session.ResourceStatus().Meshes} meshes");
            Shot(resources, "resource_editor_car", sb);
            Resize(resources, 1280, 720);
            Shot(resources, "resource_editor_car_min", sb);
            resources.Close();
            opened.Remove(resources);

            var main = Offscreen(new MainWindow(), 1600, 900, opened);
            Shot(main, "main_empty", sb);
            session = new AppEditorSession();
            Pump(() => session.Areas().Count > 0, 60);
            refused = session.LoadArea(area, winter: false, discardUnsavedEdits: true);
            Pump(() => session.Status() is { Loading: false, Meshes: > 0 }, 240);
            sb.AppendLine(refused ?? $"area {area}: {session.Status().Meshes} meshes");
            Shot(main, "main_area", sb);
            if (session.Find(null, "Mesh", null, null, 1).FirstOrDefault() is { } mesh && session.Select([mesh.Name]) == null)
            {
                Shot(main, "main_selected", sb);
                Resize(main, 1280, 720);
                Shot(main, "main_selected_min", sb);
            }

            // The dialogs, each over the district's viewport where it needs one. A dialog that cannot be built
            // without a scene it does not have is skipped, not fatal.
            (string Name, Func<Window> Make)[] dialogs =
            [
                ("dialog_import", () => new ImportWindow(main.Viewport)),
                ("dialog_material_editor", () => new MaterialEditorWindow(main.Viewport)),
                ("dialog_restore_backup", () => new RestoreBackupWindow(main.Viewport, null)),
                ("dialog_hide_triangles", () => new HideTrianglesWindow(main.Viewport)),
                ("dialog_load_zones", () => new LoadZonesWindow(main.Viewport)),
                ("dialog_stream_map", () => new StreamMapWindow()),
                ("dialog_build", () => new BuildWindow([new FileInfo(Assets.MafiaEnvironment.CityUniversSds)])),
                ("dialog_car_clone", () => new CarToolWindow(CarTool.Clone, car)),
                ("dialog_app", () => new AppDialog(new DialogOptions
                {
                    Heading = "Discard unsaved edits?", Text = "Three objects were moved since the last save.",
                    Icon = DialogIcon.Warning, Buttons = DialogButtons.YesCancel, ConfirmText = "Discard",
                    CheckboxText = "Do not ask again",
                })),
            ];
            foreach ((string dialogName, Func<Window> make) in dialogs)
            {
                try
                {
                    Window dialog = Offscreen(make(), null, null, opened);
                    Shot(dialog, dialogName, sb);
                    dialog.Close();
                    opened.Remove(dialog);
                }
                catch (Exception ex) { sb.AppendLine($"{dialogName}: skipped — {ex.Message}"); }
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally
        {
            foreach (Window w in opened)
            {
                try { w.Close(); } catch { /* a window that will not close does not spoil the pictures */ }
            }
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static T Offscreen<T>(T window, double? width, double? height, List<Window> opened) where T : Window
    {
        window.WindowState = WindowState.Normal;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20_000;
        window.Top = -20_000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        if (width is { } w) window.Width = w;
        if (height is { } h) window.Height = h;
        window.Show();
        opened.Add(window);
        return window;
    }

    private static void Resize(Window window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
    }

    // The window's client area over the window colour: the viewport draws through D3D and comes out empty, and
    // without a backdrop the chrome would float on a transparent PNG.
    private static void Shot(Window window, string name, StringBuilder sb)
    {
        try
        {
            Pump(() => false, 1);
            window.UpdateLayout();
            if (window.Content is not FrameworkElement root) return;
            int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
            if (w <= 0 || h <= 0) { sb.AppendLine($"{name}: not laid out"); return; }

            var backdrop = new DrawingVisual();
            using (DrawingContext dc = backdrop.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(window.TryFindResource("SurfaceWindowColor") is Color c ? c : Colors.Black),
                    null, new Rect(0, 0, w, h));

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(backdrop);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string path = Path.Combine(Path.GetTempPath(), $"illusion_shot_{name}.png");
            using (FileStream fs = File.Create(path)) enc.Save(fs);
            sb.AppendLine($"{name}: {w}x{h} -> {path}");
            Census(root, name);
        }
        catch (Exception ex) { sb.AppendLine($"{name}: render failed — {ex.Message}"); }
    }

    // What the picture cannot be measured from by eye: every control's laid-out box, the spread of control heights,
    // and the neighbours in one row that disagree in height or centre. Written beside the PNG as _layout.txt.
    private static void Census(FrameworkElement root, string name)
    {
        var controls = new List<(FrameworkElement El, Rect Box, string Label)>();
        void Walk(DependencyObject node)
        {
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement fe && !fe.IsVisible) continue;
                // Controls a window is built from, not the parts of their templates (a ComboBox's own toggle) and
                // not a tree row, whose box holds its whole subtree.
                if (child is System.Windows.Controls.Control control
                    && control is not (System.Windows.Controls.UserControl or System.Windows.Controls.TreeViewItem)
                    && control.ActualWidth > 0 && control.ActualHeight > 0
                    && control.TemplatedParent is not System.Windows.Controls.Control)
                {
                    try
                    {
                        Rect box = control.TransformToAncestor(root).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
                        string label = control.GetType().Name + (string.IsNullOrEmpty(control.Name) ? "" : "#" + control.Name)
                            + (control is System.Windows.Controls.ContentControl { Content: string s } ? $" \"{(s.Length > 24 ? s[..24] : s)}\"" : "");
                        controls.Add((control, box, label));
                    }
                    catch (InvalidOperationException) { /* not under root (popup content) */ }
                }
                Walk(child);
            }
        }
        Walk(root);

        var sb = new StringBuilder();
        sb.AppendLine($"{name}: {controls.Count} controls");
        sb.AppendLine("\nCONTROL HEIGHTS BY TYPE (height: count)");
        foreach (var group in controls.GroupBy(c => c.El.GetType().Name).OrderBy(g => g.Key))
        {
            sb.AppendLine($"  {group.Key,-22} " + string.Join(", ",
                group.GroupBy(c => Math.Round(c.Box.Height, 1)).OrderBy(g => g.Key).Select(g => $"{g.Key:0.#}:{g.Count()}")));
        }

        // Neighbours: two controls whose boxes overlap vertically, sit side by side within 24 px, and share a parent
        // panel — a toolbar row, a field and its button. They should agree in height and in centre line.
        sb.AppendLine("\nROW MISMATCHES (same parent, side by side, height or centre differs by more than 1 px)");
        foreach (var byParent in controls.GroupBy(c => VisualTreeHelper.GetParent(c.El)))
        {
            var row = byParent.OrderBy(c => c.Box.Left).ToList();
            for (int i = 0; i + 1 < row.Count; i++)
            {
                var a = row[i]; var b = row[i + 1];
                bool sideBySide = b.Box.Left >= a.Box.Right - 1 && b.Box.Left - a.Box.Right <= 24
                    && Math.Min(a.Box.Bottom, b.Box.Bottom) > Math.Max(a.Box.Top, b.Box.Top);
                if (!sideBySide) continue;
                double dh = Math.Abs(a.Box.Height - b.Box.Height);
                double dc = Math.Abs((a.Box.Top + a.Box.Bottom) / 2 - (b.Box.Top + b.Box.Bottom) / 2);
                if (dh > 1 || dc > 1)
                    sb.AppendLine($"  {a.Label} {Fmt(a.Box)}  |  {b.Label} {Fmt(b.Box)}  dh={dh:0.#} dcentre={dc:0.#}");
            }
        }

        sb.AppendLine("\nALL CONTROLS (x, y, w, h)");
        foreach (var c in controls.OrderBy(c => Math.Round(c.Box.Top)).ThenBy(c => c.Box.Left))
            sb.AppendLine($"  {Fmt(c.Box)}  {c.Label}");

        File.WriteAllText(Path.Combine(Path.GetTempPath(), $"illusion_shot_{name}_layout.txt"), sb.ToString());
    }

    private static string Fmt(Rect r) => $"({r.X:0.#},{r.Y:0.#} {r.Width:0.#}x{r.Height:0.#})";

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
