using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Views;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Window layout. The real <see cref="MainWindow"/> is built headless and laid out at every screen we support,
/// from the 1280x720 floor up; each pass asserts that the three toolbar groups stay apart and inside the row,
/// that no tool button is pushed into the ToolBar's overflow menu, and that the chrome leaves the viewport its
/// share of the window. The <see cref="LauncherWindow"/> follows, at its one fixed width. Needs no game data
/// and no GPU (no window is ever shown, so the D3D surface is never created).
/// Output: %TEMP%\illusion_layout.txt + illusion_layout.png + illusion_layout_launcher.png
/// </summary>
internal static class LayoutProbes
{
    // Client areas, not screen sizes: a maximized window loses the title bar and the taskbar (~79px together at
    // 100% scaling), and a windowed one loses the borders too. 1280x720 is the floor the editor supports; the
    // last entry is the window's own minimum, where the layout only has to degrade safely — the ToolBar is
    // allowed to move buttons into its overflow menu there.
    private static readonly (string Screen, double Width, double Height)[] Layouts =
    {
        ("1280x720 maximized", 1280, 641),
        ("1280x720 windowed", 1264, 689),
        ("1366x768 maximized", 1366, 689),
        ("1600x900 maximized", 1600, 821),
        ("1920x1080 maximized", 1920, 1001),
        ("2560x1440 maximized", 2560, 1361),
        ("960x560 window minimum", 960, 560),
    };

    /// <summary>Below this width the ToolBar may fold surplus buttons into its overflow menu.</summary>
    private const double SupportedWidth = 1280;

    internal static void RunLayoutProbe()
    {
        string outTxt = Path.Combine(Path.GetTempPath(), "illusion_layout.txt");
        string outPng = Path.Combine(Path.GetTempPath(), "illusion_layout.png");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            var window = new MainWindow();
            var content = (FrameworkElement)window.Content;
            ToolbarRowPanel row = window.ToolbarRow;
            var tray = (ToolBarTray)row.Children[0];
            var toolBar = (ToolBar)tray.ToolBars[0];

            Check("toolbar row holds exactly the three groups it lays out", row.Children.Count == 3,
                $"{row.Children.Count} children");
            // One opaque band across the row, and nothing painting over it: two translucent layers would show
            // the tray as a lighter patch, and a tray-painted band would stop where its buttons end.
            // The layers list is the toolbar's growth valve: layers go in there, not into new toolbar buttons.
            // Zones · crash objects · collision hulls · AI navigation. Car collision is NOT here — it moved
            // onto the Render tab, where a car is actually worked on, and it is the one overlay you keep
            // switching while placing a shape.
            Check("every display layer is in the layers list", window.LayerRows.Children.Count == 4,
                $"{window.LayerRows.Children.Count} rows");

            // Both toolbar flyouts open on hover; HoverPopup takes over their closing, which is what StaysOpen
            // shows here (the behaviour itself is exercised by the hover cases in the UI probe).
            Check("both toolbar flyouts are wired for hover",
                window.LayersPopup.StaysOpen && window.ModeStrip.DropPopup.StaysOpen,
                $"layers={window.LayersPopup.StaysOpen}, modes={window.ModeStrip.DropPopup.StaysOpen}");
            Check("toolbar band is painted once, by the row",
                row.Background is SolidColorBrush { Color.A: 0xFF }
                && IsClear(tray.Background) && IsClear(toolBar.Background),
                $"row={row.Background}, tray={tray.Background}, bar={toolBar.Background}");

            foreach ((string screen, double width, double height) in Layouts)
            {
                // Twice: the props row's height cap follows the column's, which is only known after an
                // arrange — the same second pass the live window gets for free from its SizeChanged.
                for (int i = 0; i < 2; i++)
                {
                    content.Measure(new Size(width, height));
                    content.Arrange(new Rect(0, 0, width, height));
                    content.UpdateLayout();
                }

                Rect tools = Bounds(row, 0), action = Bounds(row, 1), modes = Bounds(row, 2);
                sb.AppendLine($"— {screen} ({width}x{height} client): tools {Fmt(tools)}, " +
                              $"action {Fmt(action)}, modes {Fmt(modes)}, row w={row.ActualWidth:F0}, " +
                              $"modes folded={window.ModeStrip.IsCompact}, tool overflow={toolBar.HasOverflowItems}");

                // The bug this probe exists for: the three groups shared one Grid cell, so on a narrow window the
                // tray's last toggles were drawn underneath the Play button.
                Check($"{screen}: tools clear of the action bar", !Overlap(tools, action),
                    $"tools end at {tools.Right:F0}, action starts at {action.Left:F0}");
                Check($"{screen}: action bar clear of the shading modes", !Overlap(action, modes),
                    $"action ends at {action.Right:F0}, modes start at {modes.Left:F0}");
                Check($"{screen}: tools clear of the shading modes", !Overlap(tools, modes));
                Check($"{screen}: groups stay inside the row",
                    tools.Left >= -0.5 && modes.Right <= row.ActualWidth + 0.5,
                    $"tools.left={tools.Left:F0}, modes.right={modes.Right:F0}, row={row.ActualWidth:F0}");

                // Reachability: a button folded into the overflow menu or arranged to nothing is not on screen.
                if (width >= SupportedWidth)
                {
                    Check($"{screen}: every tool button fits without the overflow menu", !toolBar.HasOverflowItems);
                    Check($"{screen}: shading modes stay one click away", !window.ModeStrip.IsCompact);
                }

                // Room is given up in order: the shading modes fold into their drop-down before the tools start
                // disappearing into an overflow menu.
                Check($"{screen}: tools are the last to give way",
                    !toolBar.HasOverflowItems || window.ModeStrip.IsCompact,
                    $"overflow={toolBar.HasOverflowItems}, modes folded={window.ModeStrip.IsCompact}");
                Check($"{screen}: all four shading modes are still there", window.ModeStrip.Items.Count == 4,
                    $"{window.ModeStrip.Items.Count} buttons");
                Check($"{screen}: Play and the area selector are laid out",
                    window.PlayBtn.ActualWidth > 0 && window.AreaCombo.ActualWidth > 0,
                    $"play={window.PlayBtn.ActualWidth:F0}, combo={window.AreaCombo.ActualWidth:F0}");

                // The viewport is the point of the window: the scene panel must not take it over, and the tool
                // shelf (top-left overlay, margin included) has to fit the viewport's height. The area is
                // measured on the shelf's host grid, not on the render surface — that one is an Image whose
                // rendered size is zero until a D3D source exists, which never happens in a headless pass.
                var viewportArea = (FrameworkElement)window.ToolShelf.Parent;
                double panel = window.Scene.Tabs.PropertyTabs.ActualWidth;
                Check($"{screen}: scene panel leaves the viewport its share", panel <= width * 0.4,
                    $"panel={panel:F0} of {width:F0} ({panel / width:P0})");
                Check($"{screen}: tool shelf fits the viewport height",
                    window.ToolShelf.ActualHeight + 24 <= viewportArea.ActualHeight,
                    $"shelf={window.ToolShelf.ActualHeight:F0}+24, viewport={viewportArea.ActualHeight:F0}");
                if (width >= SupportedWidth)
                {
                    Check($"{screen}: viewport stays usable", viewportArea.ActualWidth >= 600,
                        $"viewport={viewportArea.ActualWidth:F0}x{viewportArea.ActualHeight:F0}");
                }

                // The props browser sits under the viewport, as the content browser sits under the resource
                // editor's stage — and keeps the same rules: never more than half, both panes there.
                double props = window.PropsPanel.ActualHeight;
                sb.AppendLine($"  props h={props:F0}, viewport {viewportArea.ActualWidth:F0}x{viewportArea.ActualHeight:F0}");
                Check($"{screen}: the props never out-grow the viewport", props <= viewportArea.ActualHeight,
                    $"props={props:F0}, viewport={viewportArea.ActualHeight:F0}");
                Check($"{screen}: both props panes are laid out",
                    window.PropsPanel.Shelves.ActualWidth > 60 && window.PropsPanel.Tiles.ActualWidth > 100,
                    $"shelves={window.PropsPanel.Shelves.ActualWidth:F0}, tiles={window.PropsPanel.Tiles.ActualWidth:F0}");
            }

            // Folded away, the props are a hairline and their tab — and the viewport gets the height back.
            double openViewport = ((FrameworkElement)window.ToolShelf.Parent).ActualHeight;
            window.PropsPanel.IsCollapsed = true;
            content.Measure(new Size(Layouts[^1].Width, Layouts[^1].Height));
            content.Arrange(new Rect(0, 0, Layouts[^1].Width, Layouts[^1].Height));
            content.UpdateLayout();
            Check("map editor: folded, the props give up all of their height",
                window.PropsPanel.ActualHeight <= 4, $"{window.PropsPanel.ActualHeight:F0}px");
            Check("map editor: folded, the props' splitter is gone too",
                window.PropsSplitter.Visibility == Visibility.Collapsed, window.PropsSplitter.Visibility.ToString());
            Check("map editor: folding gives the height back to the viewport",
                ((FrameworkElement)window.ToolShelf.Parent).ActualHeight > openViewport + 100,
                $"{openViewport:F0} → {((FrameworkElement)window.ToolShelf.Parent).ActualHeight:F0}");
            window.PropsPanel.IsCollapsed = false;

            CheckResourceEditor(Check, sb);

            CheckLauncher(Check, sb);

            // A look at the floor size, where everything is tightest. The chrome comes out in Fluent's default
            // (light) colours — a window that is never shown is never themed, and the probe path runs before
            // App pins the dark one — but light and dark share their metrics, so the geometry is the shipped
            // one. Best-effort: a render-only failure leaves the layout asserts green.
            try
            {
                (double w, double h) = (Layouts[0].Width, Layouts[0].Height);
                for (int i = 0; i < 2; i++)
                {
                    content.Measure(new Size(w, h));
                    content.Arrange(new Rect(0, 0, w, h));
                    content.UpdateLayout();
                }
                // The viewport draws through D3D and stays empty here; without a background the chrome would
                // float on a transparent PNG.
                if (content is Panel root) root.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));

                var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(content);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (FileStream fs = File.Create(outPng)) enc.Save(fs);
                sb.AppendLine($"\nrendered {w}x{h}px -> {outPng}");
            }
            catch (Exception rex) { sb.AppendLine("\nrender skipped — " + rex.Message); }

            sb.Insert(0, $"LAYOUT PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outTxt, sb.ToString()); }
    }

    /// <summary>
    /// The resource editor, whose extra piece of chrome is the content browser docked under its stage — the
    /// one panel that eats the render surface's HEIGHT, which on the 1280x720 floor is the scarce dimension.
    /// Laid out at every screen we support: the browser must never out-grow the stage, both of its panes have
    /// to be there, and folding it away has to give the height back. A picture goes out beside the editor's.
    /// </summary>
    private static void CheckResourceEditor(Action<string, bool, string> check, StringBuilder sb)
    {
        var window = new ResourceEditorWindow();
        var content = (FrameworkElement)window.Content;
        var stageArea = (FrameworkElement)window.Stage.Parent;

        foreach ((string screen, double width, double height) in Layouts)
        {
            // Twice: the browser's height cap follows the column's, which is only known after an arrange —
            // the same second pass the live window gets for free from its SizeChanged.
            for (int i = 0; i < 2; i++)
            {
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
            }

            double browser = window.Browser.ActualHeight;
            double stage = stageArea.ActualHeight;
            double panel = window.Scene.ActualWidth;
            sb.AppendLine($"— {screen} resource editor: browser h={browser:F0}, " +
                          $"stage {stageArea.ActualWidth:F0}x{stage:F0}, panel w={panel:F0}");

            check($"{screen}: the browser never out-grows the stage", browser <= stage,
                $"browser={browser:F0}, stage={stage:F0}");
            check($"{screen}: the stage keeps a workable height", stage >= 140, $"stage={stage:F0}");
            check($"{screen}: both browser panes are laid out",
                window.Browser.FolderTree.ActualWidth > 60 && window.Browser.Contents.ActualWidth > 100,
                $"tree={window.Browser.FolderTree.ActualWidth:F0}, " +
                $"contents={window.Browser.Contents.ActualWidth:F0}");
            check($"{screen}: the scene panel leaves the stage its share", panel <= width * 0.4,
                $"panel={panel:F0} of {width:F0}");
            check($"{screen}: the shading modes are all there",
                window.ModeStrip.Items.Count == 4, $"{window.ModeStrip.Items.Count} buttons");
            // The same action bar as the map editor: trying an edit means starting the game, and which
            // editor you made it in is not a reason to go looking for another window.
            // The same tools as the map editor, and they have to fit the stage the same way — without them
            // there is no walking around a resource, which is what the shelf was added for.
            check($"{screen}: the tool shelf fits the stage",
                window.ToolShelf.ActualHeight > 0 && window.ToolShelf.ActualHeight + 24 <= stage,
                $"shelf={window.ToolShelf.ActualHeight:F0}+24, stage={stage:F0}");
            check($"{screen}: Play and Build are laid out",
                window.PlayBtn.ActualWidth > 0 && window.BuildBtn.ActualWidth > 0
                && window.MultiplayerBtn.ActualWidth > 0,
                $"play={window.PlayBtn.ActualWidth:F0}, mp={window.MultiplayerBtn.ActualWidth:F0}, " +
                $"build={window.BuildBtn.ActualWidth:F0}");
        }

        // Folded away, the browser is a hairline and its tab and nothing more — this is what makes it
        // affordable at 720p.
        window.Browser.IsCollapsed = true;
        content.Measure(new Size(Layouts[0].Width, Layouts[0].Height));
        content.Arrange(new Rect(0, 0, Layouts[0].Width, Layouts[0].Height));
        content.UpdateLayout();
        check("resource editor: folded, the browser gives up all of its height",
            window.Browser.ActualHeight <= 4, $"{window.Browser.ActualHeight:F0}px");
        check("resource editor: folded, the browser's splitter is gone too",
            window.BrowserSplitter.Visibility == Visibility.Collapsed,
            window.BrowserSplitter.Visibility.ToString());
        check("resource editor: folding gives the height back to the stage",
            stageArea.ActualHeight > 400, $"stage={stageArea.ActualHeight:F0}");

        // The city-only scene filters are gone: a resource has no neighbouring districts and no winter twin.
        // IsVisible is false in a window that was never shown — the flag to read here is the one the code sets.
        var filters = window.Scene.Tabs.RenderFilters;
        check("resource editor: the city-only render filters are hidden, the actor one is not",
            filters.ProxyScenesRow.Visibility == Visibility.Collapsed
            && filters.ProxyMeshesRow.Visibility == Visibility.Collapsed
            && filters.SnowScenesRow.Visibility == Visibility.Collapsed
            && filters.ActorsToggle.Visibility == Visibility.Visible,
            $"proxy={filters.ProxyScenesRow.Visibility}, snow={filters.SnowScenesRow.Visibility}, " +
            $"actors={filters.ActorsToggle.Visibility}");
    }

    /// <summary>
    /// The launcher, whose width is fixed and whose height follows its content. Nothing here changes with the
    /// screen, so it is laid out once — what matters is that the settings gear stays in its corner: it shares
    /// a cell with the title, so a longer title or a larger glyph is what would push them into each other.
    /// A picture of it goes out beside the editor's.
    /// </summary>
    private static void CheckLauncher(Action<string, bool, string> check, StringBuilder sb)
    {
        var launcher = new LauncherWindow();
        var body = (FrameworkElement)launcher.Content;
        const double width = 600;
        body.Measure(new Size(width, double.PositiveInfinity));
        double height = body.DesiredSize.Height;
        body.Arrange(new Rect(0, 0, width, height));
        body.UpdateLayout();

        Rect gear = BoundsIn(launcher.SettingsBtn, body);
        Rect title = BoundsIn(launcher.TitleBlock, body);
        sb.AppendLine($"— launcher ({width}x{height:F0}): gear {Fmt(gear)}, title {Fmt(title)}, " +
                      $"path box w={launcher.PathBox.ActualWidth:F0}");

        check("launcher: the gear is in the top-right corner",
            gear.Right <= width + 0.5 && gear.Top >= -0.5 && gear.Left > width / 2,
            $"gear {Fmt(gear)} in {width:F0}");
        check("launcher: the gear clears the title", gear.Left >= title.Right - 0.5,
            $"title ends at {title.Right:F0}, gear starts at {gear.Left:F0}");
        check("launcher: the gear sits level with the title", gear.Top < title.Bottom,
            $"gear top {gear.Top:F0}, title bottom {title.Bottom:F0}");
        check("launcher: both game-folder buttons are laid out",
            launcher.BrowseBtn.ActualWidth > 0 && launcher.UnpackBtn.ActualWidth > 0,
            $"browse={launcher.BrowseBtn.ActualWidth:F0}, unpack={launcher.UnpackBtn.ActualWidth:F0}");
        check("launcher: the path box keeps the rest of the row",
            launcher.PathBox.ActualWidth > width / 2, $"{launcher.PathBox.ActualWidth:F0} of {width:F0}");
        // Two ways in, side by side and the same size — neither editor is the one the launcher is "really"
        // for, and a tile that is only ever disabled is a promise, not a door.
        check("launcher: both editor tiles are laid out, equally wide",
            launcher.MapEditorBtn.ActualWidth > 0
            && Math.Abs(launcher.MapEditorBtn.ActualWidth - launcher.ResourcesEditorBtn.ActualWidth) < 1,
            $"map={launcher.MapEditorBtn.ActualWidth:F0}, resources={launcher.ResourcesEditorBtn.ActualWidth:F0}");

        try
        {
            if (body is Panel root) root.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(body);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string path = Path.Combine(Path.GetTempPath(), "illusion_layout_launcher.png");
            using FileStream fs = File.Create(path);
            enc.Save(fs);
            sb.AppendLine($"rendered launcher -> {path}");
        }
        catch (Exception ex) { sb.AppendLine("launcher render skipped — " + ex.Message); }
    }

    /// <summary>A child's rendered rectangle in its parent panel's coordinates (margins excluded, as drawn).</summary>
    private static Rect Bounds(Panel parent, int index) => BoundsIn((UIElement)parent.Children[index], parent);

    /// <summary>Internal so the update probe can measure the launcher's corner with its second button
    /// showing — that state only exists when a check has found something, which this probe never does.</summary>
    internal static Rect BoundsIn(UIElement child, Visual ancestor)
    {
        Point origin = child.TransformToAncestor(ancestor).Transform(new Point(0, 0));
        return new Rect(origin, child.RenderSize);
    }

    // Half a pixel of contact is a rounding artifact, not an overlap.
    private static bool Overlap(Rect a, Rect b)
    {
        Rect hit = Rect.Intersect(a, b);
        return !hit.IsEmpty && hit.Width > 0.5 && hit.Height > 0.5;
    }

    private static string Fmt(Rect r) => $"[{r.Left:F0}..{r.Right:F0}]";

    /// <summary>Paints nothing: no brush at all, or a fully transparent one (still hit-testable).</summary>
    private static bool IsClear(Brush? brush) => brush is null or SolidColorBrush { Color.A: 0 };
}
