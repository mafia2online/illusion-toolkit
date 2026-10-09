using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What an interior opens showing.
/// <para>
/// The map hides two kinds of scene by default — the proxies a district carries of its neighbours and its
/// winter geometry — and tells them by the flags their objects have in the frame name table. An interior's
/// rooms hang under holders with the same bits set (the game moves those holders to the shop's place), so an
/// interior read by the district's rule opened without its walls, floor and ceiling: drawn by the game,
/// counted by the editor, and not in the viewport. This probe holds both ends: the shipped interiors open
/// whole, and a district still has its proxy scenes hidden.
/// </para>
/// </summary>
internal static class InteriorSceneProbes
{
    // Args: interiors under pc\sds without the extension (default: shops\elgreco shops\gunshop).
    // Output: %TEMP%\illusion_interior_scenes.txt (+ .png, the first interior's room seen from inside on the stage)
    internal static void Run(string[] args)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_interior_scenes.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        Window? window = null;
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sdsFolder = Path.Combine(MafiaEnvironment.PcFolder, "sds");
            string[] interiors = args.Length > 0 ? args : [@"shops\elgreco", @"shops\gunshop"];

            // ── which archives have proxy and snow scenes at all ──
            Check("a district, city_univers and the crash layer are the city's archives",
                SdsMeshLoader.IsCityArchive(new FileInfo(Path.Combine(sdsFolder, "city", "greenfield.sds")))
                && SdsMeshLoader.IsCityArchive(new FileInfo(Path.Combine(sdsFolder, "city_univers", "city_univers.sds")))
                && SdsMeshLoader.IsCityArchive(new FileInfo(Path.Combine(sdsFolder, "city_crash", "city_crash_z.sds"))));
            Check("an interior and a car are not",
                !SdsMeshLoader.IsCityArchive(new FileInfo(Path.Combine(sdsFolder, "shops", "elgreco.sds")))
                && !SdsMeshLoader.IsCityArchive(new FileInfo(Path.Combine(sdsFolder, "cars", "shubert_38.sds"))));

            // ── the interiors: flagged like a district's backdrops, and shown all the same ──
            FileInfo? staged = null;
            foreach (string asked in interiors)
            {
                // named with or without the extension
                string interior = asked.EndsWith(".sds", StringComparison.OrdinalIgnoreCase) ? asked[..^4] : asked;
                var file = new FileInfo(Path.Combine(sdsFolder, interior + ".sds"));
                if (!file.Exists) { sb.AppendLine($"    ({interior}: no such archive — skipped)"); continue; }
                (List<SdsFrameNode> roots, _, _) = SdsMeshLoader.LoadHierarchy(file);
                List<SdsFrameNode> scenes = [.. roots.Where(r => r.Kind == "Scene")];
                var flagged = new List<string>();
                foreach (SdsFrameNode scene in scenes) Flagged(scene, flagged);
                Check($"{interior}: objects of its scenes carry name-table flags a district reads as proxy or snow",
                    flagged.Count > 0, string.Join(", ", flagged.Take(4)));
                Check($"{interior}: none of its scenes is sorted as one",
                    scenes.Count > 0 && scenes.All(s => s.Category == "Normal"),
                    string.Join(", ", scenes.Select(s => $"{s.Name} {s.Category}")));

                List<SceneNode> leaves = Opened(roots, out _);
                List<SceneNode> hidden = [.. leaves.Where(l => !l.IsVisible && l.Lod == 0 && !DefaultHidden.IsEmitterShell(l.Name))];
                Check($"{interior}: every mesh is shown when it opens", leaves.Count > 0 && hidden.Count == 0,
                    $"{leaves.Count - hidden.Count} of {leaves.Count} shown"
                    + (hidden.Count == 0 ? "" : "; hidden: " + string.Join(", ", hidden.Take(6).Select(h => h.Name))));
                staged ??= file;
            }

            // The interiors are what this probe is about: with none of them opened it has shown nothing.
            Check("at least one interior was opened", staged != null, $"none of: {string.Join(", ", interiors)}");

            // ── a district: still sorted, its proxy scenes still hidden ──
            var district = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "greenfield.sds"));
            if (district.Exists)
            {
                (List<SdsFrameNode> roots, _, _) = SdsMeshLoader.LoadHierarchy(district);
                List<SdsFrameNode> proxies = [.. roots.Where(r => r.Kind == "Scene" && r.Category == "Proxy")];
                Check("a district still has scenes sorted as proxy", proxies.Count > 0,
                    string.Join(", ", roots.Where(r => r.Kind == "Scene").GroupBy(r => r.Category).Select(g => $"{g.Key} {g.Count()}")));
                Opened(roots, out List<SceneNode> rows);
                List<SceneNode> proxyRows = [.. rows.Where(r => r.Category == "Proxy")];
                Check("…and they open hidden, the others shown",
                    proxyRows.Count > 0 && proxyRows.All(r => !r.IsVisible)
                    && rows.Any(r => r.Category == "Normal" && r.IsVisible));
            }
            else
            {
                sb.AppendLine("    (no greenfield district — the district steps were skipped)");
            }

            // ── on the stage: the room is in the picture ──
            if (staged != null)
            {
                var host = new D3DImageHost { IsMapViewport = false };
                window = new Window
                {
                    Width = 1100, Height = 760, Left = -20_000, Top = -20_000, ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None, Content = host,
                };
                window.Show();
                Pump(() => host.Rnd != null, 20);
                if (host.Rnd == null) { Check("the stage's renderer came up", false); return; }
                host.LoadStage(staged, "probe");
                Pump(() => !host.IsLoading && host.MeshCount > 0, 120);

                // The room itself: the largest thing drawn under a scene.
                var meshRows = new List<SceneNode>();
                foreach (SceneNode root in host.Tree.Roots) Collect(root, meshRows, underScene: false);
                SceneNode? shell = meshRows.Where(r => r.Mesh is { Instanced: false })
                    .MaxBy(r => Volume(r.Mesh!.BoundsMax - r.Mesh.BoundsMin));
                Check($"{staged.Name} on the stage: its meshes are loaded", shell != null, $"{meshRows.Count} under its scenes");
                if (shell?.Mesh is { } mesh)
                {
                    Check($"the room '{shell.Name}' is shown", shell.IsVisible && mesh.Visible);
                    Vector3 centre = (mesh.BoundsMin + mesh.BoundsMax) * 0.5f;
                    host.Rnd.ShowSky = false;
                    host.LookFrom(centre, centre + new Vector3(3f, 0.5f, -0.4f));
                    const int W = 640, H = 400;
                    byte[]? with = host.CaptureFrame(W, H);
                    if (with != null) GpuProbes.SavePng(with, W, H, Path.ChangeExtension(outFile, ".png"));
                    mesh.Visible = false;
                    byte[]? without = host.CaptureFrame(W, H);
                    mesh.Visible = true;
                    int own = 0;
                    for (int i = 0; with != null && without != null && i < with.Length; i += 4)
                    {
                        if (with[i] != without[i] || with[i + 1] != without[i + 1] || with[i + 2] != without[i + 2]) own++;
                    }
                    Check("…and drawn: seen from inside, it is most of the picture", own > W * H / 2, $"{own} of {W * H} pixels are its own");
                }
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
            sb.Insert(0, $"INTERIOR SCENES PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // The tree the streamer builds for one archive, with the map's default filters applied the way BeginBuild
    // applies them: the mesh rows, and the rows of the scenes themselves.
    private static List<SceneNode> Opened(List<SdsFrameNode> roots, out List<SceneNode> sceneRows)
    {
        var leaves = new List<SceneNode>();
        var tree = new SceneTree();
        sceneRows = [];
        foreach (SdsFrameNode root in roots)
        {
            SceneNode row = SceneTree.BuildSceneTree(root, leaves);
            tree.ApplySceneFilter(row);
            if (root.Kind == "Scene") sceneRows.Add(row);
        }
        return leaves;
    }

    private static void Flagged(SdsFrameNode node, List<string> into)
    {
        if (node.Source is IFrameNode { IsOnNameTable: true, NameTableFlags: not 0 } frame)
        {
            into.Add($"{node.Name} 0x{frame.NameTableFlags:X}");
        }
        foreach (SdsFrameNode child in node.Children) Flagged(child, into);
    }

    private static void Collect(SceneNode node, List<SceneNode> into, bool underScene)
    {
        if (underScene && node.Mesh != null) into.Add(node);
        foreach (SceneNode child in node.Children) Collect(child, into, underScene || node.Kind == "Scene");
    }

    private static float Volume(Vector3 size) => size.X * size.Y * size.Z;

    private static bool Pump(Func<bool> until, int seconds)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (until()) return true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(15);
        }
        return until();
    }
}
