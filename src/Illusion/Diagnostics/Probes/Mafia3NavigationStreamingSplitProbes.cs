using System.IO;
using System.Numerics;
using System.Xml.Linq;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Navigation;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledNavigationStreamingSplit(Action<string, bool> check)
    {
        string? executable = Environment.GetEnvironmentVariable("ILLUSION_MAFIA3_COOK_EXE");
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_easthighland_south_general.sds"));
        if (!archive.Exists || string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;
        Task.Run(async () =>
        {
            var document = new CompiledSceneDocument(archive);
            int a = document.NavigationMeshes.ToList().FindIndex(f => f.ResourceId == 0xAA8D3450);
            int b = document.NavigationMeshes.ToList().FindIndex(f => f.ResourceId == 0xAA8D3451);
            var source = document.NavigationMeshes[a]; var peer = document.NavigationMeshes[b];
            var sourceMesh = source.Meshes[0]; var peerMesh = peer.Meshes[0];
            var counts = new[] { (sourceMesh.Vertices.Count, sourceMesh.FaceCount, sourceMesh.EdgeCount), (peerMesh.Vertices.Count, peerMesh.FaceCount, peerMesh.EdgeCount) };
            var oldSource = source.ToBytes(); var oldPeer = peer.ToBytes();
            var sourceLinks = source.ReadStreaming(); var peerLinks = peer.ReadStreaming();
            string folder = MafiaEnvironment.ExtractedDir(archive), manifest = Path.Combine(folder, "SDSContent.xml");
            var snapshots = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            byte[] archiveBefore = File.ReadAllBytes(archive.FullName); var oldManifest = XElement.Load(manifest);
            string ResourceName(uint uid) => Path.GetFileName(Directory.GetFiles(folder, "NAV_PATH*").Single(p => Mafia3NavMeshFile.Load(p).ResourceId == uid));
            string nameA = ResourceName(source.ResourceId), nameB = ResourceName(peer.ResourceId);
            static long Entry(XElement root, string file) => long.Parse(root.Elements("ResourceEntry").Single(e => e.Element("File")?.Value == file).Attribute("SlotRam")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            static long Request(int length) => (length + 27L) & ~15L;
            try
            {
                check("stream split geometric candidate discovery", source.StreamingEdgeSplitCandidates(peer, 128).Contains(730));
                var refused = await document.SplitNavigationStreamingEdgeAsync(a, b, 730, new Vector3(float.MaxValue, 0, 0), executable);
                check("stream split invalid point refused atomically", refused.Edit is null && source.ToBytes().SequenceEqual(oldSource) && peer.ToBytes().SequenceEqual(oldPeer));
                var result = await document.SplitNavigationStreamingEdgeAsync(a, b, 730, copiedExecutablePath: executable);
                check("stream split native original/new graphs, exact offline links and isolated half paths", result.Edit is not null);
                if (result.Edit is not { } action) return;
                check("stream split canonical mesh adapters retained", ReferenceEquals(source.Meshes[0], sourceMesh) && ReferenceEquals(peer.Meshes[0], peerMesh));
                check("stream split counts grow on BOTH sections", sourceMesh.Vertices.Count == counts[0].Item1 + 1 && sourceMesh.FaceCount == counts[0].FaceCount + 1 && sourceMesh.EdgeCount == counts[0].EdgeCount + 3 && peerMesh.Vertices.Count == counts[1].Item1 + 1 && peerMesh.FaceCount == counts[1].FaceCount + 1 && peerMesh.EdgeCount == counts[1].EdgeCount + 3);
                check("stream split original link IDs retained and one reciprocal link appended", sourceLinks.Mesh.All(source.ReadStreaming().Mesh.Contains) && peerLinks.Mesh.All(peer.ReadStreaming().Mesh.Contains) && source.ReadStreaming().Mesh.Count == sourceLinks.Mesh.Count + 1 && peer.ReadStreaming().Mesh.Count == peerLinks.Mesh.Count + 1);
                check("stream split strict graph stream records exact", source.ReadStreaming().Graph.SequenceEqual(sourceLinks.Graph) && peer.ReadStreaming().Graph.SequenceEqual(peerLinks.Graph));
                check("stream split every seam remains reciprocal and coincident", source.ValidateStreamingPair(peer).MeshLinks == sourceLinks.Mesh.Count(c => c.OppositeUid == peer.ResourceId) + 1);
                byte[] cooked = source.ToBytes(), cookedPeer = peer.ToBytes();
                action.Undo();
                byte[] tampered = cooked.ToArray(); tampered[4] ^= 1; bool metadataRefused = false;
                try { source.ApplyStreamingEdgeSplitCook(peer, 730, tampered, cookedPeer); } catch (InvalidDataException) { metadataRefused = true; }
                check("stream split metadata tamper refused before either resource applies", metadataRefused && source.ToBytes().SequenceEqual(oldSource) && peer.ToBytes().SequenceEqual(oldPeer));
                action.Redo(); document.SaveWorkingCopy();
                var budget = XElement.Load(manifest); long deltaA = Request(cooked.Length) - Request(oldSource.Length), deltaB = Request(cookedPeer.Length) - Request(oldPeer.Length);
                check("stream split both entry and aggregate native RAM budgets", Entry(budget, nameA) == Entry(oldManifest, nameA) + deltaA && Entry(budget, nameB) == Entry(oldManifest, nameB) + deltaB && long.Parse(budget.Attribute("SlotRam")!.Value, System.Globalization.CultureInfo.InvariantCulture) == long.Parse(oldManifest.Attribute("SlotRam")!.Value, System.Globalization.CultureInfo.InvariantCulture) + deltaA + deltaB);
                var packed = SdsArchive.Pack(folder, MafiaEnvironment.Profile); using var stream = new MemoryStream(); packed.Save(stream, new SdsWriteOptions { Compress = true }); stream.Position = 0; SdsArchive.Load(stream).Extract(folder);
                var reload = new CompiledSceneDocument(archive); var freshA = reload.NavigationMeshes.Single(f => f.ResourceId == source.ResourceId); var freshB = reload.NavigationMeshes.Single(f => f.ResourceId == peer.ResourceId);
                check("stream split compressed fresh BOTH resources exact", freshA.ToBytes().SequenceEqual(cooked) && freshB.ToBytes().SequenceEqual(cookedPeer) && freshA.ValidateStreamingPair(freshB) == source.ValidateStreamingPair(peer));
                action.Undo(); document.SaveWorkingCopy(); check("stream split undo EVERY file and manifest exact", snapshots.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
                action.Redo(); document.SaveWorkingCopy(); check("stream split redo reload", new CompiledSceneDocument(archive).NavigationMeshes.Single(f => f.ResourceId == source.ResourceId).Meshes[0].FaceCount == counts[0].FaceCount + 1);
                action.Undo(); document.SaveWorkingCopy(); check("stream split final undo and source archive exact", snapshots.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)) && File.ReadAllBytes(archive.FullName).SequenceEqual(archiveBefore));
            }
            finally { foreach (var (path, bytes) in snapshots) File.WriteAllBytes(path, bytes); }
        }).GetAwaiter().GetResult();
    }
}
