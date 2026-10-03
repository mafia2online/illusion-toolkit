using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Buffers nothing draws from: how many the archives as SHIPPED carry (read off each archive's oldest backup or
/// the archive itself, never a working copy someone has edited), and how many the install's working copies have
/// collected. The sweep at save rests on the first number being zero.
/// </summary>
internal static class GeometrySweepProbes
{
    // Output: %TEMP%\illusion_geometry_sweep.txt
    internal static void RunGeometrySweepProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_geometry_sweep.txt");
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_geometry_sweep");
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
            int shipped = 0, shippedWithDead = 0, working = 0;
            long workingBytes = 0;
            var notes = new List<string>();
            foreach (string folder in new[] { "city", "shops" })
            {
                foreach (string file in Directory.GetFiles(Path.Combine(MafiaEnvironment.PcFolder, "sds", folder), "*.sds"))
                {
                    var sds = new FileInfo(file);
                    // As shipped: the oldest backup, or the archive when it was never built — extracted to scratch.
                    IReadOnlyList<SdsWriter.BackupInfo> backups = SdsWriter.ListBackups(sds);
                    FileInfo pristine = backups.Count == 0 ? sds : backups[^1].File;
                    if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
                    SdsArchive.Open(pristine.FullName).Extract(scratch);
                    ExtractedSds stock = ExtractedSds.Load(scratch);
                    if (stock.FrameResource is not { } scene) continue;
                    shipped++;
                    (List<ulong> v, List<ulong> i, long bytes) = GeometrySweep.Unreferenced(scene);
                    if (v.Count + i.Count > 0)
                    {
                        shippedWithDead++;
                        notes.Add($"    shipped {Path.GetFileName(file)}: {v.Count} vertex + {i.Count} index buffers, {bytes:N0} bytes");
                    }

                    string dir = MafiaEnvironment.ExtractedDir(sds);
                    if (!File.Exists(Path.Combine(dir, "SDSContent.xml"))) continue;
                    ExtractedSds copy = ExtractedSds.Load(dir);
                    if (copy.FrameResource is not { } edited) continue;
                    (List<ulong> wv, List<ulong> wi, long wbytes) = GeometrySweep.Unreferenced(edited);
                    if (wv.Count + wi.Count == 0) continue;
                    working++;
                    workingBytes += wbytes;
                    notes.Add($"    working copy {Path.GetFileName(file)}: {wv.Count} vertex + {wi.Count} index buffers, {wbytes:N0} bytes");
                }
            }
            foreach (string note in notes) sb.AppendLine(note);
            Check("no archive as shipped carries a buffer nothing draws from", shipped > 0 && shippedWithDead == 0,
                $"{shippedWithDead} of {shipped} do");
            sb.AppendLine($"    working copies with dead buffers: {working}, {workingBytes:N0} bytes in all");
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* scratch left behind */ }
            sb.Insert(0, $"GEOMETRY SWEEP PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }
}
