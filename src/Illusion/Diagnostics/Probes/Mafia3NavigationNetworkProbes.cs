using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Navigation;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledNavigationNetwork(Action<string, bool> check)
    {
        string? executable = Environment.GetEnvironmentVariable("ILLUSION_MAFIA3_COOK_EXE");
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_easthighland_south_general.sds"));
        if (!archive.Exists || string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;
        Task.Run(async () =>
        {
            var document = new CompiledSceneDocument(archive);
            var ids = new uint[] { 0xA98D32E3, 0xA98D32E0, 0xAA8D345C };
            var indices = ids.Select(uid => document.NavigationMeshes.ToList().FindIndex(file => file.ResourceId == uid)).ToArray();
            if (indices.Any(index => index < 0)) return;
            var sections = indices.Select(index => document.NavigationMeshes[index]).ToArray();
            var before = sections.Select(file => file.ToBytes()).ToArray();
            var representatives = sections[0].ReadClusterRepresentatives().ToArray();
            var changes = new[] { 100, 101 }.ToDictionary(vertex => vertex, vertex => sections[0].Meshes[0].Vertices[vertex] + new Vector3(2, 0, 0));
            string folder = MafiaEnvironment.ExtractedDir(archive);
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            byte[] archiveBefore = File.ReadAllBytes(archive.FullName);
            string? helper = Environment.GetEnvironmentVariable("ILLUSION_MAFIA3_COOK_HELPER");
            Illusion.Domain.IEditAction? action = null; bool applied = false;
            try
            {
                bool pairRefused = false;
                try { _ = await NativeNavigationCooker.CookStreamingAsync([before[0], before[1]], changes, executable, helperPath: helper, moveRepresentatives: true); }
                catch (NotSupportedException ex) { pairRefused = ex.Message.Contains("third section", StringComparison.OrdinalIgnoreCase); }
                check("navigation network extends a real third-section refusal without changing source bytes", pairRefused && sections.Select((file, index) => file.ToBytes().SequenceEqual(before[index])).All(value => value));
                var result = await document.DeformNavigationNetworkAsync(indices[0], indices, changes, executable,
                    moveRepresentatives: true, helperPath: helper);
                action = result.Edit ?? throw new InvalidDataException(result.Refusal); applied = true;
                check("three-section navigation original native baseline and representative recook", changes.All(pair => sections[0].Meshes[0].Vertices[pair.Key] == pair.Value)
                    && sections[0].ReadClusterRepresentatives().Where((value, index) => value != representatives[index]).Count() == 2);
                var cooked = sections.Select(file => file.ToBytes()).ToArray();
                check("three-section navigation retains every reciprocal seam and cost record", sections[0].ValidateStreamingPair(sections[1]).GraphLinks > 0
                    && sections[0].ValidateStreamingPair(sections[2]).GraphLinks > 0);
                document.SaveWorkingCopy();
                var reload = new CompiledSceneDocument(archive);
                check("three-section navigation saves and reloads all native bytes", sections.Select((file, index) => reload.NavigationMeshes.Single(other => other.ResourceId == file.ResourceId).ToBytes().SequenceEqual(cooked[index])).All(value => value));
                var packed = SdsArchive.Pack(folder, MafiaEnvironment.Profile); using var stream = new MemoryStream();
                packed.Save(stream, new SdsWriteOptions { Compress = true }); stream.Position = 0; SdsArchive.Load(stream).Extract(folder);
                var fresh = new CompiledSceneDocument(archive);
                check("three-section navigation compressed archive preserves native graph and geometry", sections.Select((file, index) => fresh.NavigationMeshes.Single(other => other.ResourceId == file.ResourceId).ToBytes().SequenceEqual(cooked[index])).All(value => value));
                action.Undo(); applied = false;
                var bad = cooked.Select(bytes => bytes.ToArray()).ToArray(); bad[2][4] ^= 1; bool refused = false;
                try { _ = Mafia3NavMeshFile.ApplyStreamingNetworkCook(sections, ids[0], changes, bad, true); }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { refused = true; }
                check("three-section navigation tampered peer metadata refuses before any file changes", refused && sections.Select((file, index) => file.ToBytes().SequenceEqual(before[index])).All(value => value));
                action.Redo(); applied = true; action.Undo(); applied = false;
                check("three-section navigation Undo Redo restores all native resource bytes exactly", sections.Select((file, index) => file.ToBytes().SequenceEqual(before[index])).All(value => value));
            }
            finally
            {
                if (applied) action!.Undo(); document.SaveWorkingCopy();
            }
            check("three-section navigation copied archive and all payloads/manifests remain byte exact", files.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
                && File.ReadAllBytes(archive.FullName).SequenceEqual(archiveBefore));
        }).GetAwaiter().GetResult();
    }
}
