using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledNavigationStreamingRepresentatives(Action<string, bool> check)
    {
        string? executable = Environment.GetEnvironmentVariable("ILLUSION_MAFIA3_COOK_EXE");
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_easthighland_south_general.sds"));
        if (!archive.Exists || string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;
        Task.Run(async () =>
        {
            var document = new CompiledSceneDocument(archive);
            int a = document.NavigationMeshes.ToList().FindIndex(f => f.ResourceId == 0xA98D32E0);
            int b = document.NavigationMeshes.ToList().FindIndex(f => f.ResourceId == 0xA98D32E3);
            var source = document.NavigationMeshes[a]; var peer = document.NavigationMeshes[b];
            string folder = MafiaEnvironment.ExtractedDir(archive);
            var snapshots = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            byte[] archiveBefore = File.ReadAllBytes(archive.FullName);
            var original = document.NavigationMeshes.Select(f => f.ToBytes()).ToArray();
            var links = source.ReadStreaming().Graph.ToArray(); var peerLinks = peer.ReadStreaming().Graph.ToArray();
            var sourcePositions = source.ReadClusterRepresentatives(); var peerPositions = peer.ReadClusterRepresentatives();
            var target = source.Meshes[0].Vertices[4] + new Vector3(5, 0, 0);
            var edits = new Dictionary<int, Vector3> { [4] = target };
            try
            {
                int thirdSource = document.NavigationMeshes.ToList().FindIndex(f => f.ResourceId == 0xAA8D3455);
                int thirdPeer = document.NavigationMeshes.ToList().FindIndex(f => f.ResourceId == 0xA98D32E2);
                var thirdPosition = document.NavigationMeshes[thirdSource].Meshes[0].Vertices[592] + new Vector3(.01f, 0, 0);
                var third = await document.DeformNavigationStreamingAsync(thirdSource, thirdPeer, new Dictionary<int, Vector3> { [592] = thirdPosition }, executable, moveRepresentatives: true);
                check("joint representative third-peer connection refused without mutation", third.Edit is null && third.Refusal?.Contains("third section", StringComparison.OrdinalIgnoreCase) == true && document.NavigationMeshes.Select((f, i) => f.ToBytes().SequenceEqual(original[i])).All(v => v));
                var standalone = await document.DeformNavigationInteriorAsync(a, edits, executable);
                check("standalone representative edit refuses stale streaming costs", standalone.Edit is null && standalone.Refusal?.Contains("joint", StringComparison.OrdinalIgnoreCase) == true);
                var strict = await document.DeformNavigationStreamingAsync(a, b, edits, executable);
                check("joint representative recook is opt-in and refusals preserve all bytes", strict.Edit is null && document.NavigationMeshes.Select((f, i) => f.ToBytes().SequenceEqual(original[i])).All(v => v));
                var result = await document.DeformNavigationStreamingAsync(a, b, edits, executable, moveRepresentatives: true);
                check("joint representative native offline recook succeeds", result.Edit is not null);
                if (result.Edit is not { } action) return;
                var afterLinks = source.ReadStreaming().Graph; var afterPeer = peer.ReadStreaming().Graph;
                int changed = afterLinks.Zip(links).Count(p => p.First.Cost != p.Second.Cost) + afterPeer.Zip(peerLinks).Count(p => p.First.Cost != p.Second.Cost);
                check("joint representative changes six native reciprocal costs", changed == 6);
                check("joint representative moves exactly one source cluster", source.ReadClusterRepresentatives().Zip(sourcePositions).Count(p => p.First != p.Second) == 1 && peer.ReadClusterRepresentatives().SequenceEqual(peerPositions));
                check("joint representative retains exact graph link identities", afterLinks.Select(c => c with { Cost = 0 }).SequenceEqual(links.Select(c => c with { Cost = 0 })) && afterPeer.Select(c => c with { Cost = 0 }).SequenceEqual(peerLinks.Select(c => c with { Cost = 0 })));
                check("joint representative retains coincident seam", source.ValidateStreamingPair(peer).MeshLinks > 0);
                byte[] afterSource = source.ToBytes(), afterPeerBytes = peer.ToBytes();
                int changedCost = links.First(c => c.Cost != BitConverter.ToUInt16(afterSource, c.CostOffset)).CostOffset;
                byte[] tamperedCost = afterSource.ToArray(); tamperedCost[changedCost] ^= 1;
                action.Undo(); bool reciprocalCostRefused = false;
                try { source.ApplyStreamingCook(peer, edits, tamperedCost, afterPeerBytes, moveRepresentatives: true); }
                catch (InvalidDataException ex) { reciprocalCostRefused = ex.Message.Contains("reciprocal", StringComparison.OrdinalIgnoreCase); }
                check("joint representative one-sided cost tampering refuses before either resource changes", reciprocalCostRefused && source.ToBytes().SequenceEqual(original[a]) && peer.ToBytes().SequenceEqual(original[b]));
                action.Redo();
                document.SaveWorkingCopy();
                var packed = SdsArchive.Pack(folder, MafiaEnvironment.Profile); using var stream = new MemoryStream();
                packed.Save(stream, new SdsWriteOptions { Compress = true }); stream.Position = 0; SdsArchive.Load(stream).Extract(folder);
                var reload = new CompiledSceneDocument(archive); var fresh = reload.NavigationMeshes.Single(f => f.ResourceId == source.ResourceId);
                check("joint representative compressed fresh geometry and costs", fresh.Meshes[0].Vertices[4] == target && fresh.ReadStreaming().Graph.SequenceEqual(afterLinks));
                action.Undo(); document.SaveWorkingCopy(); check("joint representative undo all resources/manifest exact", snapshots.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
                action.Redo(); document.SaveWorkingCopy(); check("joint representative redo reload", new CompiledSceneDocument(archive).NavigationMeshes.Single(f => f.ResourceId == source.ResourceId).Meshes[0].Vertices[4] == target);
                action.Undo(); document.SaveWorkingCopy(); check("joint representative final undo and source archive exact", snapshots.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)) && File.ReadAllBytes(archive.FullName).SequenceEqual(archiveBefore));
            }
            finally { foreach (var (path, bytes) in snapshots) File.WriteAllBytes(path, bytes); }
        }).GetAwaiter().GetResult();
    }
}
