using System.IO;
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
            FileInfo added = CreateArchive("shops/new_room.sds");
            FileInfo drifted = CreateArchive("city/drifted.sds");
            byte[] original = File.ReadAllBytes(summer.FullName);
            // the archives under shops/ are new to the game; drifted.sds is shipped, and no copy of its shipped bytes is left
            (bool, FileInfo?) Stock(FileInfo archive, string target) => (!target.StartsWith("/sds/shops/", StringComparison.Ordinal), archive == drifted ? null : archive);
            IReadOnlyList<M2oExportResult> Export(IReadOnlyList<FileInfo> archives, string folder) => M2oMapExporter.Export(archives, folder, null, [], Stock);
            string destination = Path.Combine(root, "export");
            var results = Export([summer, winter, added], destination);
            Check(results.Count == 3 && results.Select(r => r.OutputPath).Distinct().Count() == 3, "season targets remain distinct");
            Check(results.Select(r => Path.GetRelativePath(destination, r.OutputPath).Replace('\\', '/')).SequenceEqual(
                ["stream/sds/city/district.sds.patch", "stream/sds/city/district_z.sds.patch", "stream/sds/shops/new_room.sds"]), "every file sits at its game path under stream/");
            Check(!File.Exists(Path.Combine(destination, "map_patches.json")) && !Directory.Exists(Path.Combine(destination, ".edited")), "no manifest and no scratch files are published");
            foreach (M2oExportResult result in results.Where(r => r.Kind == M2oExportKind.Patch))
            {
                using FileStream input = File.OpenRead(result.OutputPath);
                SdsPatchFile patch = SdsPatchFile.Load(input);
                Check(patch.Entries.Count == 1 && patch.Entries[0].Data!.SequenceEqual(new byte[] { 4, 3, 2, 1 }), "a shipped archive exports as a readable native patch with edited bytes");
            }
            M2oExportResult whole = results.Single(r => r.Kind == M2oExportKind.NewArchive);
            Check(whole.Diff == null && SdsArchive.Open(whole.OutputPath).Entries.Single().Data!.SequenceEqual(new byte[] { 4, 3, 2, 1 }), "a new archive exports whole, as edited");
            Refuses(() => Export([summer, sameName], Path.Combine(root, "same-stem")), "archives sharing a file name refused (their libraries would collide)");
            Refuses(() => Export([drifted], Path.Combine(root, "drifted")), "a shipped archive without its shipped bytes refused");
            Refuses(() => Export([CreateArchive("city/default_x.sds")], Path.Combine(root, "default")), "a library named like the game's own refused");
            Refuses(() => M2oMapExporter.TargetOf(CreateArchive("city/Bad-Name.sds")), "a name M2O cannot stream refused");
            Check(original.SequenceEqual(File.ReadAllBytes(summer.FullName)), "original game archive remains unchanged");
            Refuses(() => PatchExporter.Export(summer, summer.FullName), "GUI exporter refuses to overwrite its base archive");
            PatchProbes.RunBuildPatch(["--build-patch", summer.FullName, summer.FullName, "--delete", "0"]);
            Check(File.ReadAllText(Path.Combine(Path.GetTempPath(), "illusion_patch.txt")).Contains("overwrite", StringComparison.Ordinal), "build command reports archive overwrite refusal");
            PatchProbes.RunRemoveFrames(["--remove-frames", summer.FullName, Path.Combine(summer.DirectoryName!, ".", summer.Name), "--frame", "*"]);
            Check(File.ReadAllText(Path.Combine(Path.GetTempPath(), "illusion_patch.txt")).Contains("overwrite", StringComparison.Ordinal), "removal command normalizes output before overwrite check");
            Check(original.SequenceEqual(File.ReadAllBytes(summer.FullName)), "refused commands preserve archive bytes");
            PatchProbes.RunPatchDiff(["--patch-diff", summer.FullName, results[0].OutputPath]);
            Check(File.ReadAllText(Path.Combine(Path.GetTempPath(), "illusion_patch.txt")).Contains("comparison is unavailable", StringComparison.Ordinal), "non-scene archive comparison reports missing frames");
            PatchProbes.RunFrameCollision(["--frame-collision", summer.FullName, "example"]);
            Check(File.ReadAllText(Path.Combine(Path.GetTempPath(), "illusion_patch.txt")).Contains("no FrameResource", StringComparison.Ordinal), "non-scene collision command reports missing frames");

            void RejectData(Action read, string name)
            {
                bool rejected = false;
                try { read(); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, name);
            }
            foreach (uint size in new uint[] { 1, 29, uint.MaxValue })
            {
                byte[] payload = new byte[30];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), size);
                using var invalidPatch = new MemoryStream();
                using (var writer = new BinaryWriter(invalidPatch, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(SdsPatchFile.Signature);
                    writer.Write(1u);
                    writer.Write(SdsPatchFile.Marker);
                    writer.Write(0u);
                    writer.Write(0u);
                    writer.Write(1u);
                }
                SdsBlockStream.Write(invalidPatch, payload);
                invalidPatch.Position = 0;
                RejectData(() => SdsPatchFile.Load(invalidPatch), $"invalid resource length {size} rejected before allocation");
            }
            foreach (uint size in new uint[] { 1, 31, uint.MaxValue })
            {
                using var invalidBlock = new MemoryStream();
                using (var writer = new BinaryWriter(invalidBlock, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(SdsBlockStream.Signature);
                    writer.Write((uint)SdsBlockStream.ChunkSize);
                    writer.Write((byte)4);
                    writer.Write(size);
                    writer.Write((byte)1);
                    writer.Write(new byte[32]);
                }
                invalidBlock.Position = 0;
                RejectData(() => SdsBlockStream.Read(invalidBlock), $"invalid compressed block length {size} rejected before allocation");
            }
            var frames = new Illusion.Formats.Frames.FrameResource();
            var unnamed = new Illusion.Formats.Frames.ObjectTypes.FrameObjectBase(frames);
            unnamed.Name.String = null!;
            var named = new Illusion.Formats.Frames.ObjectTypes.FrameObjectBase(frames);
            named.Name.String = "ExampleFrame";
            frames.FrameObjects.Add(1, unnamed);
            frames.FrameObjects.Add(2, named);
            var match = typeof(ScenePatchAuthor).GetMethod("Match", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var matches = (IEnumerable<Illusion.Formats.Frames.ObjectTypes.FrameObjectBase>)match.Invoke(null, [frames, "example*"])!;
            Check(matches.Single() == named, "wildcard matching skips null names and preserves case-insensitive matching");
            Refuses(() => Export([summer], destination), "existing export protected");
            Check(File.Exists(Path.Combine(destination, ExportFolder.MarkerName)), "an export folder carries its marker");
            File.WriteAllText(Path.Combine(destination, "stale.txt"), "left by the previous export");
            Check(M2oMapExporter.Export([summer], destination, null, [], Stock, replace: true).Count == 1 && !File.Exists(Path.Combine(destination, "stale.txt"))
                && File.Exists(Path.Combine(destination, "stream", "sds", "city", "district.sds.patch")) && !File.Exists(Path.Combine(destination, "stream", "sds", "city", "district_z.sds.patch")),
                "a re-export replaces its own folder whole");
            string foreign = Path.Combine(root, "foreign");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, "mine.txt"), "the user's");
            Refuses(() => M2oMapExporter.Export([summer], foreign, null, [], Stock, replace: true), "a folder no export wrote is never replaced");
            Check(File.Exists(Path.Combine(foreign, "mine.txt")), "...and is left as it was");
            string flat = Path.Combine(root, "flat");
            Check(M2oMapExporter.Export([summer, added], flat, null, [], Stock, streamFolder: false).Count == 2
                && File.Exists(Path.Combine(flat, "sds", "city", "district.sds.patch")) && File.Exists(Path.Combine(flat, "sds", "shops", "new_room.sds")),
                "patch files sit at their game paths without stream/");
            var project = new ExportProject("Greenfield", ExportDestination.M2oResource, destination, [ExportProject.GamePathOf(summer), ExportProject.GamePathOf(added)]);
            project.Save();
            ExportProject? loaded = ExportProject.LoadAll().SingleOrDefault();
            Check(loaded is { Name: "Greenfield", Destination: ExportDestination.M2oResource } && loaded.Archives.SequenceEqual(project.Archives)
                && loaded.ArchiveFiles().Select(f => f.FullName).SequenceEqual([summer.FullName, added.FullName], StringComparer.OrdinalIgnoreCase)
                && ExportProject.LastUsed() == "Greenfield", "an export project round-trips and is remembered as the last one");
            Check(ExportProject.CheckName("") != null && ExportProject.CheckName("a/b") != null && ExportProject.CheckName(".hidden") != null && ExportProject.CheckName("Uptown 2") == null,
                "project names are checked");
            Refuses(() => Export([summer, summer], Path.Combine(root, "duplicate")), "duplicate target refused");
            Refuses(() => Export([new FileInfo(Path.Combine(root, "outside.sds"))], Path.Combine(root, "outside")), "archive outside pc/sds refused");
            string failed = Path.Combine(root, "failed");
            Refuses(() => Export([summer, new FileInfo(Path.Combine(pc, "sds", "missing.sds"))], failed), "second archive failure reported");
            Check(!Directory.Exists(failed) && Directory.GetDirectories(root, "*.tmp-*").Length == 0, "failed export publishes nothing and removes staging");
            FileInfo unchanged = CreateArchive("city/unchanged.sds");
            SdsArchive.Open(unchanged.FullName).Extract(MafiaEnvironment.ExtractedDir(unchanged));
            string mixed = Path.Combine(root, "mixed");
            Check(Export([unchanged, summer], mixed).Count == 1 && !File.Exists(Path.Combine(mixed, "stream", "sds", "city", "unchanged.sds.patch")), "unchanged archives are skipped without blocking changed ones");
            string empty = Path.Combine(root, "empty");
            Check(Export([unchanged], empty).Count == 0 && !Directory.Exists(empty), "unchanged export creates no empty map");
            Refuses(() => PatchExporter.ExportAll([summer, sameName], Path.Combine(root, "raw")), "raw batch refuses filename collisions");
            Refuses(() => PatchExporter.ExportWithSeasonVariant(summer, Path.Combine(root, "district_z.sds.patch")), "season filename collision refused before writing");
            Check(!File.Exists(Path.Combine(root, "district_z.sds.patch")), "season collision leaves destination untouched");
            Check(PatchExporter.ExportAll([unchanged, summer], Path.Combine(root, "raw-mixed")).Count == 1, "raw batch skips unchanged archives");
            Refuses(() => PatchExporter.ExportAll([new FileInfo(Path.Combine(pc, "sds", "missing.sds"))], Path.Combine(root, "raw-failed")), "raw batch propagates missing input errors");

#pragma warning disable WPF0001
            Application.Current.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
            var window = new Views.BuildWindow([winter], ExportDestination.M2oResource);
            Check(window.Destination == ExportDestination.M2oResource && window.Rows.Any(r => r.Path == winter.FullName && !r.IsChecked)
                && window.Rows.Where(r => r.IsChecked).Select(r => r.Path).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(new[] { summer.FullName, added.FullName }.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
                "the export window opens on the last project: its archives ticked, another edited archive listed unticked");
            var content = (FrameworkElement)window.Content;
            ((System.Windows.Controls.Panel)content).Background = new SolidColorBrush((Color)Application.Current.FindResource("SurfaceWindowColor"));
            content.Measure(new Size(760, 620));
            content.Arrange(new Rect(0, 0, 760, 620));
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(760, 620, 96, 96, PixelFormats.Pbgra32);
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
