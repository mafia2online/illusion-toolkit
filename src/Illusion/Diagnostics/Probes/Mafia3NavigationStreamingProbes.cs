using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledNavigationStreaming(Action<string, bool> check)
    {
        TestCompiledNavigationStreamingCook(check);
        TestCompiledNavigationStreamingRepresentatives(check);
        TestCompiledNavigationStreamingSplit(check);
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_easthighland_north_general.sds"));
        if (!archive.Exists) return;
        byte[] before = File.ReadAllBytes(archive.FullName);
        var document = new CompiledSceneDocument(archive);
        var files = document.NavigationMeshes;
        var snapshots = files.Select(f => f.ToBytes()).ToArray();
        var a = files.Single(f => f.ResourceId == 0x93CB4A32);
        var b = files.Single(f => f.ResourceId == 0x93CB4A33);
        check("streamed North pair reciprocal mesh seams and graph metadata", a.ValidateStreamingPair(b) == (14, 8) && b.ValidateStreamingPair(a) == (14, 8));
        check("streamed North pair has additional external neighbors", a.ReadStreaming().PotentialNeighbors.Any(uid => uid != b.ResourceId) && b.ReadStreaming().PotentialNeighbors.Any(uid => uid != a.ResourceId));
        check("streamed North pair remains protected from isolated translation", !a.CanTranslateIsolatedSection(out _) && !b.CanTranslateIsolatedSection(out _));
        bool refused = false;
        try { a.ValidateStreamingPair(a); } catch (InvalidDataException) { refused = true; }
        check("streaming peer validation rejects same UID", refused);
        check("streaming pair inspection preserves every resource and archive byte", files.Select((f, i) => f.ToBytes().SequenceEqual(snapshots[i])).All(v => v) && File.ReadAllBytes(archive.FullName).SequenceEqual(before));
        string? executable = Environment.GetEnvironmentVariable("ILLUSION_MAFIA3_COOK_EXE");
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            bool mismatchRefused = false;
            try { Task.Run(() => NativeNavigationCooker.CookStreamingAsync([a.ToBytes(), b.ToBytes()], new Dictionary<int, Vector3>(), executable)).GetAwaiter().GetResult(); }
            catch (NotSupportedException ex) { mismatchRefused = ex.Message.Contains("baseline", StringComparison.OrdinalIgnoreCase); }
            check("joint streaming original native baseline mismatch refused", mismatchRefused && files.Select((f, i) => f.ToBytes().SequenceEqual(snapshots[i])).All(v => v));
        }
    }

    private static void TestCompiledNavigationStreamingCook(Action<string, bool> check)
    {
        string? executable = Environment.GetEnvironmentVariable("ILLUSION_MAFIA3_COOK_EXE");
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_easthighland_south_general.sds"));
        if (!archive.Exists || string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;
        Task.Run(async () =>
        {
            var document = new CompiledSceneDocument(archive);
            int sourceIndex = document.NavigationMeshes.ToList().FindIndex(f => f.ResourceId == 0xA98D32E0);
            int peerIndex = document.NavigationMeshes.ToList().FindIndex(f => f.ResourceId == 0xA98D32E3);
            var source = document.NavigationMeshes[sourceIndex]; var peer = document.NavigationMeshes[peerIndex];
            string folder = MafiaEnvironment.ExtractedDir(archive);
            var snapshots = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            var original = document.NavigationMeshes.Select(f => f.ToBytes()).ToArray(); byte[] archiveBefore = File.ReadAllBytes(archive.FullName);
            Vector3 proposed = source.Meshes[0].Vertices[1] + new Vector3(.01f, 0, 0);
            var edits = new Dictionary<int, Vector3> { [1] = proposed };
            try
            {
                var refused = await document.DeformNavigationStreamingAsync(sourceIndex, peerIndex, new Dictionary<int, Vector3> { [0] = proposed }, executable);
                check("joint streaming non-seam vertex refused without mutation", refused.Edit is null && document.NavigationMeshes.Select((f, i) => f.ToBytes().SequenceEqual(original[i])).All(v => v));
                var result = await document.DeformNavigationStreamingAsync(sourceIndex, peerIndex, edits, executable);
                check("joint streaming native multi-instance baseline and recook", result.Edit is not null);
                if (result.Edit is not { } action) return;
                check("joint streaming coincident propagated seam", source.Meshes[0].Vertices[1] == proposed && source.ValidateStreamingPair(peer).MeshLinks > 0);
                check("joint streaming changes both resources", !source.ToBytes().SequenceEqual(original[sourceIndex]) && !peer.ToBytes().SequenceEqual(original[peerIndex]));
                byte[] changed = source.ToBytes(), changedPeer = peer.ToBytes(), tampered = changed.ToArray(); tampered[4] ^= 1;
                bool metadataRefused = false;
                try { source.ApplyStreamingCook(peer, edits, tampered, changedPeer); } catch (InvalidDataException) { metadataRefused = true; }
                check("joint streaming metadata tampering refused atomically", metadataRefused && source.ToBytes().SequenceEqual(changed) && peer.ToBytes().SequenceEqual(changedPeer));
                document.SaveWorkingCopy();
                var reloaded = new CompiledSceneDocument(archive);
                check("joint streaming save/reload", reloaded.NavigationMeshes.Single(f => f.ResourceId == source.ResourceId).Meshes[0].Vertices[1] == proposed);
                var packed = SdsArchive.Pack(folder, MafiaEnvironment.Profile); using var stream = new MemoryStream();
                packed.Save(stream, new SdsWriteOptions { Compress = true }); stream.Position = 0; SdsArchive.Load(stream).Extract(folder);
                reloaded = new CompiledSceneDocument(archive);
                check("joint streaming compressed fresh seam", reloaded.NavigationMeshes.Single(f => f.ResourceId == source.ResourceId)
                    .ValidateStreamingPair(reloaded.NavigationMeshes.Single(f => f.ResourceId == peer.ResourceId)) == source.ValidateStreamingPair(peer));
                action.Undo(); document.SaveWorkingCopy();
                check("joint streaming every resource/manifest undo exact", snapshots.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
                action.Redo(); document.SaveWorkingCopy();
                check("joint streaming redo geometry", new CompiledSceneDocument(archive).NavigationMeshes.Single(f => f.ResourceId == source.ResourceId).Meshes[0].Vertices[1] == proposed);
                action.Undo(); document.SaveWorkingCopy();
                check("joint streaming final undo and source archive exact", snapshots.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)) && File.ReadAllBytes(archive.FullName).SequenceEqual(archiveBefore));
            }
            finally { foreach (var (path, bytes) in snapshots) File.WriteAllBytes(path, bytes); }
        }).GetAwaiter().GetResult();
    }
}
