using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using Illusion.Assets.Library;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The prop library: what the scan finds in the install's extracted archives, shelf by shelf, how long a full
/// scan and a remembered one take, and whether a handful of tiles get a picture. Writes the pictures it drew
/// beside the report so they can be looked at.
/// </summary>
internal static class PropCatalogProbes
{
    // Output: %TEMP%\illusion_prop_catalog.txt, pictures in %TEMP%\illusion_prop_catalog\
    internal static void RunPropCatalogProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_prop_catalog.txt");
        string pictures = Path.Combine(Path.GetTempPath(), "illusion_prop_catalog");
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
            if (File.Exists(PropCatalog.CachePath)) File.Delete(PropCatalog.CachePath);

            var clock = Stopwatch.StartNew();
            IReadOnlyList<PropEntry> entries = PropCatalog.Load();
            long full = clock.ElapsedMilliseconds;
            clock.Restart();
            IReadOnlyList<PropEntry> again = PropCatalog.Load();
            long remembered = clock.ElapsedMilliseconds;

            Check("the scan finds props", entries.Count > 50, $"{entries.Count} in {full} ms");
            Check("a second scan is answered from what was remembered, and says the same",
                again.Count == entries.Count && remembered < full / 4 + 200, $"{remembered} ms");
            foreach (IGrouping<string, PropEntry> shelf in entries.GroupBy(e => e.Category))
            {
                sb.AppendLine($"    {shelf.Key,-8} {shelf.Count(),5}  e.g. "
                    + string.Join(", ", shelf.Take(6).Select(e => $"{e.Label} ({e.ArchiveName})")));
            }
            Check("Harry's indoor door is on the Doors shelf",
                entries.Any(e => e.Category == "Doors" && e.Archive.EndsWith("harry.sds", StringComparison.OrdinalIgnoreCase)
                                 && e.Label == "HG_door_indoor"));
            Check("Francesca's kitchen chair is on the Seating shelf",
                entries.Any(e => e.Category == "Seating" && e.Label == "LFH_zidle"));
            Check("every entry has a size and a triangle count",
                entries.All(e => e.Triangles > 0 && e.Size.Length == 3 && e.Size.Max() > 0f));

            if (Directory.Exists(pictures)) Directory.Delete(pictures, recursive: true);
            Directory.CreateDirectory(pictures);
            using var renderer = new PropThumbnailRenderer();
            int drawn = 0, tried = 0, kept = 0;
            foreach (PropEntry entry in entries.GroupBy(e => e.Category).Select(g => g.First()).Take(9))
            {
                tried++;
                if (renderer.Render(entry) is not BitmapSource picture) continue;
                drawn++;
                if (PropThumbnailRenderer.Cached(entry) != null) kept++;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(picture));
                using FileStream file = File.Create(Path.Combine(pictures, $"{entry.Category}_{entry.Label}.png"));
                encoder.Save(file);
                if (drawn == 1) Check("a picture is not a blank tile", HasDetail(picture));
            }
            Check("each shelf's first prop gets a picture", drawn == tried, $"{drawn} of {tried}");
            Check("and each is kept, under a key the next look-up finds", kept == drawn, $"{kept} of {drawn}");
            // A prop whose archive is not there: no picture, and no exception — the tile asks from a timer tick.
            PropEntry gone = entries[0] with { Archive = @"city\illusion_no_such_archive.sds" };
            bool quiet;
            try { quiet = renderer.Draw(renderer.Stage(gone)) == null; }
            catch (Exception) { quiet = false; }
            Check("a prop whose archive cannot be read gets no picture and throws nothing", quiet);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            sb.Insert(0, $"PROP CATALOG PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // A tile with nothing drawn on it is one flat colour.
    private static bool HasDetail(BitmapSource picture)
    {
        var bgra = new byte[picture.PixelWidth * picture.PixelHeight * 4];
        picture.CopyPixels(bgra, picture.PixelWidth * 4, 0);
        var colours = new HashSet<int>();
        for (int i = 0; i < bgra.Length; i += 4 * 7) colours.Add(bgra[i] | bgra[i + 1] << 8 | bgra[i + 2] << 16);
        return colours.Count > 20;
    }
}
