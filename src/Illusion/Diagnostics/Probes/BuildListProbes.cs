using System.Diagnostics;
using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Probes of what a Build would put into the game (<see cref="WorkingCopyDiff"/>).</summary>
internal static class BuildListProbes
{
    // The list a Build window shows, against real archives. For each archive named (default: city_univers, a
    // small one, and a district) the archive is unpacked into %TEMP% and compared with itself - an untouched
    // working copy must show NOTHING, or every Build window would cry wolf. Then one file is changed, one the
    // manifest names is removed, one loose file is dropped in beside them and one named piece is added: the
    // list must hold exactly the changed one, the removed one (also as a missing manifest entry) and the
    // manifest - and not the loose file, which a pack does not take. The game is only read.
    // With "real <archive> ..." it lists instead what a Build of the toolkit's own working copies would change.
    // Output: %TEMP%\illusion_build_list.txt
    internal static void Run(string[] args)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_build_list.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string label, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? " - " + detail : "")}");
        }
        var scratches = new List<string>();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            // "real <archive> ..." lists, for the working copies the toolkit actually keeps, what a Build of
            // each would change in the game - the same list the Build window shows. Nothing is checked.
            if (args.Length > 1 && args[0] == "real")
            {
                foreach (string name in args.Skip(1))
                {
                    var real = new FileInfo(Path.IsPathRooted(name) ? name : Path.Combine(MafiaEnvironment.PcFolder, "sds", name));
                    var watch = Stopwatch.StartNew();
                    WorkingCopyComparison found = WorkingCopyDiff.Compare(real);
                    sb.AppendLine($"{real.Name}: {found.Changes.Count} file(s) differ from the game, {found.MissingEntries.Count} manifest entr(ies) without a file ({watch.ElapsedMilliseconds} ms)");
                    foreach (WorkingCopyChange change in found.Changes.Take(40))
                    {
                        sb.AppendLine($"   {change.Kind,-8} {change.Path}  {change.Size} B  {change.Modified:yyyy-MM-dd HH:mm}");
                    }
                }
                return;
            }
            List<FileInfo> archives = args.Length > 0
                ? [.. args.Select(a => new FileInfo(Path.IsPathRooted(a) ? a : Path.Combine(MafiaEnvironment.PcFolder, "sds", a)))]
                : [new FileInfo(MafiaEnvironment.CityUniversSds), new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "arpradelna.sds"))];
            foreach (FileInfo sds in archives)
            {
                if (!sds.Exists) { Check($"{sds.Name} is in the game", false, sds.FullName); continue; }
                string scratch = Path.Combine(Path.GetTempPath(), "illusion_buildlist_probe_" + Guid.NewGuid().ToString("N"));
                scratches.Add(scratch);
                var clock = Stopwatch.StartNew();
                SdsArchive.Open(sds.FullName).Extract(scratch);
                int files = Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories).Count();
                long unpack = clock.ElapsedMilliseconds;

                clock.Restart();
                WorkingCopyComparison same = WorkingCopyDiff.Compare(sds, scratch);
                Check($"{sds.Name}: a working copy nobody touched shows nothing ({files} files; unpack {unpack} ms, compare {clock.ElapsedMilliseconds} ms)",
                    same.InGame && same.Changes.Count == 0 && same.MissingEntries.Count == 0,
                    string.Join(", ", same.Changes.Take(6).Select(c => $"{c.Kind} {c.Path}")));

                // a resource the archive does not name is unpacked under its index, and the index moves when a
                // resource is added before it: the same bytes under another number are the same resource
                string? numbered = Directory.EnumerateFiles(scratch).Select(Path.GetFileName)
                    .FirstOrDefault(n => n != null && System.Text.RegularExpressions.Regex.IsMatch(n, @"^[A-Za-z_]+_\d+\.(fr|fnt|act|col|ibp|vbp)$"));
                if (numbered != null)
                {
                    string renumbered = System.Text.RegularExpressions.Regex.Replace(numbered, @"\d+(\.[a-z]+)$", "9999$1");
                    File.Move(Path.Combine(scratch, numbered), Path.Combine(scratch, renumbered));
                    string manifestPath = Path.Combine(scratch, "SDSContent.xml");
                    File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace(">" + numbered + "<", ">" + renumbered + "<"));
                    WorkingCopyComparison moved = WorkingCopyDiff.Compare(sds, scratch);
                    Check($"{sds.Name}: {numbered} renumbered to {renumbered} is still the same resource - nothing listed",
                        moved.Changes.Count == 0 && moved.MissingEntries.Count == 0, string.Join(", ", moved.Changes.Take(6).Select(c => $"{c.Kind} {c.Path}")));
                    File.AppendAllText(Path.Combine(scratch, renumbered), "x");
                    WorkingCopyComparison both = WorkingCopyDiff.Compare(sds, scratch);
                    Check($"{sds.Name}: renumbered AND changed, it is listed once, as changed",
                        both.Changes.Count == 1 && both.Changes[0] is { Kind: WorkingCopyChangeKind.Changed } only && only.Path == renumbered,
                        string.Join(", ", both.Changes.Take(6).Select(c => $"{c.Kind} {c.Path}")));
                    // put back, for the checks below
                    File.Delete(Path.Combine(scratch, renumbered));
                    Directory.Delete(scratch, recursive: true);
                    SdsArchive.Open(sds.FullName).Extract(scratch);
                }

                // one changed, one removed, one loose
                List<string> plain = [.. Directory.EnumerateFiles(scratch).Select(Path.GetFileName).Where(n => n != "SDSContent.xml").Select(n => n!)
                    .OrderBy(n => n, StringComparer.Ordinal)];
                if (plain.Count < 2) { sb.AppendLine($"({sds.Name} has fewer than two files beside its manifest - the change checks are skipped)"); continue; }
                string changed = plain[0], removed = plain[^1];
                File.AppendAllText(Path.Combine(scratch, changed), "x");
                File.Delete(Path.Combine(scratch, removed));
                File.WriteAllText(Path.Combine(scratch, "note to self.txt"), "not part of the archive");
                WorkingCopyComparison edited = WorkingCopyDiff.Compare(sds, scratch);
                List<string> got = [.. edited.Changes.Select(c => $"{c.Kind} {c.Path}")];
                Check($"{sds.Name}: a changed file and a removed one are listed, and nothing else",
                    got.SequenceEqual(new[] { $"Changed {changed}", $"Removed {removed}" }.OrderBy(x => x[(x.IndexOf(' ') + 1)..], StringComparer.OrdinalIgnoreCase)),
                    string.Join(", ", got));
                Check($"{sds.Name}: the removed file is also named as an entry the pack would leave out",
                    edited.MissingEntries.Any(m => m.TrimStart('/', '\\').Equals(removed, StringComparison.OrdinalIgnoreCase)), string.Join(", ", edited.MissingEntries));
                Check($"{sds.Name}: a loose file the manifest does not name is not listed",
                    !edited.Changes.Any(c => c.Path.Contains("note to self")));
                Check($"{sds.Name}: the changed file carries its size and the time it was written",
                    edited.Changes.FirstOrDefault(c => c.Kind == WorkingCopyChangeKind.Changed) is { Size: > 0 } c1 && (DateTime.Now - c1.Modified).TotalMinutes < 5);

                // A resource deleted in the content browser: its entry is unsaid and its payload stays where it
                // lay, byte for byte what the archive has. The pack leaves it out, so it is a removal - and was
                // once paired with the archive's copy as "the same", which made the deletion invisible.
                if (plain.Count >= 3)
                {
                    string unsaid = plain[1];
                    string entry = Path.GetExtension(unsaid).Equals(".xml", StringComparison.OrdinalIgnoreCase) ? unsaid[..^4] : unsaid;
                    var manifest = Formats.Archive.SdsManifest.Load(scratch);
                    string? listed = manifest.Entries.Select(e => e.File).FirstOrDefault(f => f.TrimStart('/', '\\').Equals(entry, StringComparison.OrdinalIgnoreCase));
                    if (listed != null && manifest.RemoveEntry(listed))
                    {
                        WorkingCopyComparison dropped = WorkingCopyDiff.Compare(sds, scratch);
                        Check($"{sds.Name}: a resource unsaid in the list, its file still lying there, is listed as removed",
                            dropped.Changes.Any(c => c.Kind == WorkingCopyChangeKind.Removed && c.Path.Equals(unsaid, StringComparison.OrdinalIgnoreCase))
                            && dropped.Changes.Count == edited.Changes.Count + 1, string.Join(", ", dropped.Changes.Select(c => $"{c.Kind} {c.Path}")));
                    }
                    else
                    {
                        sb.AppendLine($"({sds.Name}: {unsaid} is not a plain entry of the list - the unsaid-resource check is skipped)");
                    }
                }
            }

            // an archive the game does not have yet: everything the manifest names is an addition
            if (scratches.Count > 0)
            {
                var absent = new FileInfo(Path.Combine(Path.GetTempPath(), "illusion_no_such_archive_" + Guid.NewGuid().ToString("N") + ".sds"));
                WorkingCopyComparison fresh = WorkingCopyDiff.Compare(absent, scratches[0]);
                Check("an archive not in the game yet: every packed file is an addition",
                    !fresh.InGame && fresh.Changes.Count > 0 && fresh.Changes.All(c => c.Kind == WorkingCopyChangeKind.Added)
                    && !fresh.Changes.Any(c => c.Path.Contains("note to self")), $"{fresh.Changes.Count} file(s)");
            }
            // The window itself, with a row in it. A wrong style or binding in a row's template is not a build
            // error: it throws when the first row is made - and took the application down the first time.
            {
                var window = new Views.BuildWindow([new FileInfo(MafiaEnvironment.CityUniversSds)]) { ShowInTaskbar = false, Left = -4000, Top = -4000 };
                window.Show();
                var limit = Stopwatch.StartNew();
                while (window.Rows.Any(r => r.Comparing) && limit.ElapsedMilliseconds < 20000)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() => frame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                    Thread.Sleep(20);
                }
                window.UpdateLayout();
                Views.BuildWindow.ArchiveRow row = window.Rows[0];
                Check("the Build window makes a row for an archive and finishes comparing it", !row.Comparing && row.Summary.Length > 0, row.Summary);
                row.IsOpen = true;
                window.UpdateLayout();
                Check("...and the row can be opened and ticked without the window falling over", window.IsLoaded);
                window.Close();
            }

            bool refused = false;
            try { WorkingCopyDiff.Compare(new FileInfo(MafiaEnvironment.CityUniversSds), Path.Combine(Path.GetTempPath(), "illusion_no_such_folder")); }
            catch (FileNotFoundException) { refused = true; }
            Check("a folder that is not a working copy is refused", refused);
            Check("nothing is left in %TEMP% by the comparisons themselves",
                !Directory.EnumerateDirectories(Path.GetTempPath(), "illusion_compare_*").Any());
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("unexpected exception: " + ex);
        }
        finally
        {
            foreach (string scratch in scratches)
            {
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { sb.AppendLine("(scratch left behind: " + scratch + ")"); }
            }
            File.WriteAllText(outFile, $"BUILD LIST PROBE: {pass} passed, {fail} failed\n" + sb);
        }
    }
}
