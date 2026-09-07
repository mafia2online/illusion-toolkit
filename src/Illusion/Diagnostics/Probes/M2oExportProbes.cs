using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static class M2oExportProbes
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "illusion-m2o-export-" + Guid.NewGuid().ToString("N"));
        var log = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            log.Add("PASS: " + name);
        }
        void Refuses(Action action, string name)
        {
            bool refused = false;
            try { action(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { refused = true; }
            Check(refused, name);
        }
        try
        {
            string pc = Path.Combine(root, "pc");
            Directory.CreateDirectory(pc);
            Check(MafiaEnvironment.TryInitialize(pc, out _), "isolated game environment");
            FileInfo CreateArchive(string relative)
            {
                var file = new FileInfo(Path.Combine(pc, "sds", relative));
                Directory.CreateDirectory(file.DirectoryName!);
                var archive = new SdsArchive();
                archive.ResourceTypes.Add(new SdsResourceTypeEntry { Id = 0, Name = "Effects" });
                archive.Entries.Add(new ResourceEntry { TypeId = 0, Version = 1, Data = [1, 2, 3, 4] });
                using (FileStream output = File.Create(file.FullName)) archive.Save(output, new SdsWriteOptions());
                archive.Entries[0].Data = [4, 3, 2, 1];
                archive.Extract(MafiaEnvironment.ExtractedDir(file));
                return file;
            }
            FileInfo summer = CreateArchive("city/district.sds");
            FileInfo winter = CreateArchive("city/district_z.sds");
            FileInfo sameName = CreateArchive("other/district.sds");
            byte[] original = File.ReadAllBytes(summer.FullName);
            string destination = Path.Combine(root, "export");
            var results = M2oMapExporter.Export([summer, winter, sameName], destination);
            Check(results.Count == 3 && results.Select(r => r.PatchPath).Distinct().Count() == 3, "season targets and duplicate basenames remain distinct");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, "map_patches.json")));
            foreach (JsonElement entry in manifest.RootElement.GetProperty("patches").EnumerateArray())
            {
                using FileStream input = File.OpenRead(Path.Combine(destination, entry.GetProperty("patch").GetString()!));
                SdsPatchFile patch = SdsPatchFile.Load(input);
                Check(patch.Entries.Count == 1 && patch.Entries[0].Data!.SequenceEqual(new byte[] { 4, 3, 2, 1 }), "manifest resolves a readable native patch with edited bytes");
            }
            Check(original.SequenceEqual(File.ReadAllBytes(summer.FullName)), "original game archive remains unchanged");
            Refuses(() => M2oMapExporter.Export([summer], destination), "existing export protected");
            Refuses(() => M2oMapExporter.Export([summer, summer], Path.Combine(root, "duplicate")), "duplicate target refused");
            Refuses(() => M2oMapExporter.Export([new FileInfo(Path.Combine(root, "outside.sds"))], Path.Combine(root, "outside")), "archive outside pc/sds refused");
            string failed = Path.Combine(root, "failed");
            Refuses(() => M2oMapExporter.Export([summer, new FileInfo(Path.Combine(pc, "sds", "missing.sds"))], failed), "second archive failure reported");
            Check(!Directory.Exists(failed) && Directory.GetDirectories(root, "*.tmp-*").Length == 0, "failed export publishes nothing and removes staging");

#pragma warning disable WPF0001
            Application.Current.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
            var window = new Views.M2oExportWindow([summer, winter], true, () => { });
            var content = (FrameworkElement)window.Content;
            ((System.Windows.Controls.Panel)content).Background = new SolidColorBrush((Color)Application.Current.FindResource("SurfaceWindowColor"));
            content.Measure(new Size(680, 620));
            content.Arrange(new Rect(0, 0, 680, 620));
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(680, 620, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream image = File.Create(Path.Combine(Path.GetTempPath(), "illusion_m2o_export.png"))) encoder.Save(image);
            window.Close();
            log.Add("PASS: export window rendered");
        }
        catch (Exception ex)
        {
            log.Add("FAIL: " + ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "illusion_m2o_export.txt"), log);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
