using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using Illusion.Assets;
using Illusion.Assets.Library;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The content browser's archive pictures: a handful of people and a car drawn the way a tile draws them, the
/// pictures written beside the report so they can be looked at — a face seen from behind passes every count.
/// </summary>
internal static class ArchiveThumbnailProbes
{
    // Output: %TEMP%\illusion_archive_thumbs.txt, pictures in %TEMP%\illusion_archive_thumbs\
    // Args: archive paths under pc\sds (traffic/ccerb2.sds ...); none = a default handful.
    internal static void RunArchiveThumbnailProbe(string[] archives)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_archive_thumbs.txt");
        string pictures = Path.Combine(Path.GetTempPath(), "illusion_archive_thumbs");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            if (archives.Length == 0)
            {
                archives = ["traffic/ccerb2.sds", "traffic/cbarma.sds", "hchar/vitarmy.sds", "player/vitvez2.sds",
                    "police_char/polmur.sds", "cars/shubert_38.sds"];
            }
            if (Directory.Exists(pictures)) Directory.Delete(pictures, recursive: true);
            Directory.CreateDirectory(pictures);

            using var renderer = new ArchiveThumbnailRenderer();
            int drawn = 0, tried = 0, keptAfter = 0, olderLeft = 0;
            foreach (string relative in archives)
            {
                var file = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!file.Exists) { sb.AppendLine($"    (not in this install: {relative})"); continue; }
                string folder = "sds/" + Path.GetDirectoryName(relative)!.Replace('\\', '/');
                var entry = new LibraryEntry
                {
                    Name = Path.GetFileNameWithoutExtension(file.Name),
                    File = file,
                    Size = file.Length,
                    FolderPath = folder,
                    Resource = LibraryResourceKinds.Of(folder),
                };
                foreach (float side in new[] { 1f, -1f })     // both sides of a person, so the right one can be told by eye
                {
                    if (side < 0 && entry.Resource == LibraryResourceKind.Car) continue;
                    ArchiveThumbnailRenderer.PersonFrontY = side;
                    string kept = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Illusion", "archive_thumbs");
                    if (Directory.Exists(kept)) Directory.Delete(kept, recursive: true);   // draw, do not read back
                    // A picture kept for this archive in an earlier state of it — what every rebuild used to leave behind.
                    Directory.CreateDirectory(kept);
                    string older = Path.Combine(kept, ArchiveThumbnailRenderer.KeyOf(entry)[..17] + "0000000000000000.png");
                    File.WriteAllBytes(older, [1]);
                    tried++;
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    if (renderer.Render(entry) is not BitmapSource picture)
                    {
                        sb.AppendLine($"    {relative}: nothing drawn");
                        continue;
                    }
                    drawn++;
                    // Found again under the key computed NOW — after the archive was read, and so extracted.
                    if (ArchiveThumbnailRenderer.TryKept(entry, out System.Windows.Media.ImageSource? again) && again != null) keptAfter++;
                    if (File.Exists(older)) olderLeft++;
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(picture));
                    string name = $"{entry.Name}_{(side > 0 ? "plusY" : "minusY")}.png";
                    using FileStream png = File.Create(Path.Combine(pictures, name));
                    encoder.Save(png);
                    sb.AppendLine($"    {relative} ({entry.Resource}) from {(side > 0 ? "+Y" : "-Y")}: {clock.ElapsedMilliseconds} ms, detail {Colours(picture)} colours -> {name}");
                }
            }
            Check("every archive asked for gets a picture", drawn == tried && tried > 0, $"{drawn} of {tried}");
            Check("each picture is kept and found again by the next look-up", keptAfter == drawn, $"{keptAfter} of {drawn}");
            Check("and the one kept for the archive's earlier state is dropped", olderLeft == 0, $"{olderLeft} left behind");

            // An archive that cannot be read: a working copy whose manifest is cut short, which is not one of
            // the errors anybody would think to name. It has to come back as "no picture", not as an exception —
            // the tile asks from a timer tick, where an exception ends the program.
            var broken = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", "illusion_probe_thumb.sds"));
            string brokenCopy = MafiaEnvironment.ExtractedDir(broken);
            try
            {
                File.WriteAllBytes(broken.FullName, [0]);
                Directory.CreateDirectory(brokenCopy);
                File.WriteAllText(Path.Combine(brokenCopy, "SDSContent.xml"), "<SDSResource><ResourceEntry><Type>FrameRes");
                broken.Refresh();
                var entry = new LibraryEntry
                {
                    Name = "illusion_probe_thumb", File = broken, Size = broken.Length, FolderPath = "sds/cars",
                    Resource = LibraryResourceKind.Car,
                };
                string outcome;
                bool quiet;
                try
                {
                    ArchiveThumbnailRenderer.Staged staged = ArchiveThumbnailRenderer.Stage(entry);
                    System.Windows.Media.ImageSource? picture = renderer.Draw(staged);
                    quiet = staged.Empty && !staged.Passing && picture == null;
                    outcome = $"empty {staged.Empty}, passing {staged.Passing}";
                }
                catch (Exception ex)
                {
                    quiet = false;
                    outcome = ex.GetType().Name + ": " + ex.Message;
                }
                Check("an archive whose working copy cannot be read gets no picture and throws nothing", quiet, outcome);
                Check("and that is written down, so the next run does not read it again to find out",
                    ArchiveThumbnailRenderer.TryKept(entry, out System.Windows.Media.ImageSource? none) && none == null);
            }
            finally
            {
                if (File.Exists(broken.FullName)) File.Delete(broken.FullName);
                if (Directory.Exists(brokenCopy)) Directory.Delete(brokenCopy, recursive: true);
            }

            // What a car is made of, part by part: which materials are paintwork is a question about their
            // shaders and slots, and this is where it can be read off.
            foreach (string relative in archives.Where(a => a.StartsWith("cars/", StringComparison.OrdinalIgnoreCase)).Take(3))
            {
                var file = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!file.Exists) continue;
                sb.AppendLine($"\n    parts of {relative}:");
                foreach (Illusion.Domain.MeshData mesh in Illusion.Assets.Sds.SdsMeshLoader.LoadSds(file, lod: 0))
                {
                    sb.AppendLine($"      mesh {mesh.Name}: {mesh.Positions.Length} vertices, skinned {mesh.BoneIndices != null}");
                    foreach (Illusion.Domain.MeshPart part in mesh.Parts)
                    {
                        Illusion.Domain.Materials.MaterialInfo? info =
                            Illusion.Assets.Materials.MafiaMaterialCatalog.Instance.GetMaterial(part.MaterialHash);
                        string c002 = info?.Parameters.FirstOrDefault(x => x.ParamId == "C002") is { } colour
                            ? string.Join(",", colour.Values.Select(v => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))) : "-";
                        sb.AppendLine($"        {part.IndexCount / 3,5} tris  {info?.Name ?? "?",-26} shader {info?.ShaderId:X16}/{info?.ShaderHash:X8} "
                            + $"blended {part.Blended,-5} C002 {c002,-18} slots "
                            + string.Join(" ", (info?.TextureSlots ?? []).Select(t => $"{t.SlotId}={t.TextureName}"))
                            + "  flags " + string.Join("|", info?.Flags ?? []));
                    }
                }
            }
            ArchiveThumbnailRenderer.PersonFrontY = 1f;
            string keptDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Illusion", "archive_thumbs");
            if (Directory.Exists(keptDir)) Directory.Delete(keptDir, recursive: true);   // the -Y pictures must not be read back as the tiles'

            // The pane itself: the street people's folder open in a real content browser, left to fill in.
            LibraryCatalog catalog = LibraryCatalog.Build(Path.Combine(MafiaEnvironment.PcFolder, "sds"));
            LibraryFolder? people = Find(catalog.Roots, "traffic");
            Check("the catalog has the street people's folder", people != null, people == null ? "" : $"{people.Entries.Count} archives");
            if (people != null)
            {
                var browser = new Views.ContentBrowser();
                var window = new System.Windows.Window
                {
                    Width = 1280, Height = 420, Left = -4000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    WindowStyle = System.Windows.WindowStyle.None, Content = browser,
                };
                window.Show();
                browser.SetCatalog(catalog);
                browser.OpenFolder(people);
                // The pane has to stay alive while it fills: the longest the UI thread is held between two
                // turns of the pump. Reading an archive used to happen on it, a second or more at a time.
                var beat = System.Diagnostics.Stopwatch.StartNew();
                long longest = 0, last = 0;
                var gaps = new List<(long At, long Gap)>();
                var pulse = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send)
                {
                    Interval = TimeSpan.FromMilliseconds(5),
                };
                pulse.Tick += (_, _) =>
                {
                    long now = beat.ElapsedMilliseconds;
                    // The first three seconds hold two things that happen once per run and are not what is
                    // being asked about: the pane laying out a hundred and fifty tiles while the GPU stack is
                    // made, and the first picture drawn through shaders nothing has compiled yet. Both are in
                    // the list the check prints.
                    if (last > 0 && now - last > 60) gaps.Add((last, now - last));
                    if (last > 3000) longest = Math.Max(longest, now - last);
                    last = now;
                };
                pulse.Start();
                Pump(TimeSpan.FromSeconds(30));
                pulse.Stop();
                var images = Images(browser).ToList();
                int tiles = images.Count;
                int looked = images.Count(Views.ArchiveThumb.OnScreen);
                int filled = images.Count(i => Views.ArchiveThumb.OnScreen(i) && i.Source != null);
                int unseenFilled = images.Count(i => !Views.ArchiveThumb.OnScreen(i) && i.Source != null);
                Check("the tiles on screen get their pictures while the pane is open", looked > 0 && filled == looked,
                    $"{filled} of {looked} on screen ({tiles} tiles in the folder) after 30 s");
                Check("archives of tiles nobody is looking at are not read", unseenFilled == 0, $"{unseenFilled} off-screen tiles have a picture");
                Check("the UI thread is never held for long while the folder fills", longest < 400,
                    $"longest gap between two pumps after the first 3 s: {longest} ms; every gap over 60 ms (at ms: length): "
                    + string.Join(", ", gaps.Take(40).Select(g => $"{g.At}: {g.Gap}")));
                var shot = new RenderTargetBitmap((int)browser.ActualWidth, (int)browser.ActualHeight, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                shot.Render(browser);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(shot));
                using (FileStream file = File.Create(Path.Combine(pictures, "_browser.png"))) png.Save(file);
                window.Close();
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            ArchiveThumbnailRenderer.PersonFrontY = 1f;
            sb.Insert(0, $"ARCHIVE THUMBNAIL PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static LibraryFolder? Find(IEnumerable<LibraryFolder> folders, string name)
    {
        foreach (LibraryFolder folder in folders)
        {
            if (string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase) && folder.Entries.Count > 0) return folder;
            if (Find(folder.Folders, name) is { } found) return found;
        }
        return null;
    }

    private static IEnumerable<System.Windows.Controls.Image> Images(System.Windows.DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            System.Windows.DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.Image image && Views.ArchiveThumb.GetEntry(image) != null) yield return image;
            foreach (System.Windows.Controls.Image inner in Images(child)) yield return inner;
        }
    }

    private static void Pump(TimeSpan span)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = span };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static int Colours(BitmapSource picture)
    {
        var bgra = new byte[picture.PixelWidth * picture.PixelHeight * 4];
        picture.CopyPixels(bgra, picture.PixelWidth * 4, 0);
        var colours = new HashSet<int>();
        for (int i = 0; i < bgra.Length; i += 4 * 7) colours.Add(bgra[i] | bgra[i + 1] << 8 | bgra[i + 2] << 16);
        return colours.Count;
    }
}
