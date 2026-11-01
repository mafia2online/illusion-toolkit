using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledNativeLodBounds(Action<string, bool> check)
    {
        var sources = Enumerable.Range(0, 3).Select(lod => new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, $"bc_french_east_block_b_lod{lod}.sds"))).ToArray();
        if (sources.Any(source => !source.Exists)) return;
        byte[] Hash(string path) { using var input = File.OpenRead(path); return SHA256.HashData(input); }
        var hashes = sources.ToDictionary(source => source.FullName, source => Hash(source.FullName));
        string unique = "native-lod-bounds-probe-" + Guid.NewGuid().ToString("N");
        string fixture = Path.Combine(MafiaEnvironment.GameRoot, unique); Directory.CreateDirectory(Path.Combine(fixture, "city"));
        string mirror = Path.Combine(MafiaEnvironment.ResourcesFolder!, unique);
        foreach (var source in sources) File.Copy(source.FullName, Path.Combine(fixture, "city", source.Name));
        try
        {
            var archive = new FileInfo(Path.Combine(fixture, "city", sources[0].Name)); var document = new CompiledSceneDocument(archive);
            var graph = document.NativeLods ?? throw new InvalidDataException(document.NativeLodRefusal);
            var baseline = document.NativeLodBoundsBaseline;
            static IEnumerable<SdsFrameNode> Walk(IEnumerable<SdsFrameNode> rows)
            { foreach (var row in rows) { yield return row; foreach (var child in Walk(row.Children)) yield return child; } }
            var frame = Walk(document.Roots).Where(row => row.Mesh?.NativeLod is { } a && graph.Groups[a.Group].Parent >= 0
                    || row.LodMeshes.Any(mesh => mesh.NativeLod is { } b && graph.Groups[b.Group].Parent >= 0))
                .Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct().First();
            var originalWorld = frame.WorldTransform; var movedWorld = originalWorld * Matrix4x4.CreateTranslation(4000, 0, 0);
            var transform = document.Graph.Field(frame.Element, "m_Transform")!; var owner = document.Graph.Owner(transform);
            byte[] originalMatrix = document.Graph.Data(transform).ToArray();
            var before = document.Graph.Resources.ToDictionary(resource => resource.Path, resource => File.ReadAllBytes(resource.Path));
            foreach (string folder in document.Graph.Resources.Select(resource => Path.GetDirectoryName(resource.Path)!).Distinct())
                before.TryAdd(Path.Combine(folder, "SDSContent.xml"), File.ReadAllBytes(Path.Combine(folder, "SDSContent.xml")));
            var originalBounds = graph.Groups.Select(group => (group.Id, group.WorldMin, group.WorldMax)).ToArray();
            bool Exact() => before.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value));
            var resources = document.Graph.Resources.ToDictionary(resource => resource, resource => resource.ToArray());
            long revision = graph.BoundsRevision;
            var invalid = movedWorld; invalid.M41 = float.NaN; bool refused = false;
            try { _ = document.PrepareNativeLodBounds(new Dictionary<CompiledSceneDocument.CompiledFrameNode, Matrix4x4> { [frame] = invalid }); }
            catch (InvalidDataException) { refused = true; }
            check("native LOD invalid proposed world refuses before any resource or bounds mutation", refused && graph.BoundsRevision == revision && resources.All(pair => pair.Key.ToArray().SequenceEqual(pair.Value)));
            var invalidBaseline = baseline.Where(pair => pair.Key != baseline.Keys.First()).ToDictionary(pair => pair.Key, pair => pair.Value); refused = false;
            try { document.InstallNativeLodBoundsBaseline(invalidBaseline); } catch (InvalidDataException) { refused = true; }
            check("native LOD incomplete persisted baseline refuses atomically", refused && baseline.All(pair => document.NativeLodBoundsBaseline[pair.Key] == pair.Value));
            var initial = document.PrepareNativeLodBounds();
            check("native LOD bounds preflight performs no compiled writes", resources.All(pair => pair.Key.ToArray().SequenceEqual(pair.Value)) && graph.BoundsRevision == revision);
            initial.Redo(); var preparedBounds = graph.Groups.Select(group => (group.Id, group.WorldMin, group.WorldMax)).ToArray();
            var preparedBytes = document.Graph.Resources.ToDictionary(resource => resource, resource => resource.ToArray());
            var edit = document.PrepareNativeLodBounds(new Dictionary<CompiledSceneDocument.CompiledFrameNode, Matrix4x4> { [frame] = movedWorld });
            check("native LOD complete proposed movement preflight preserves prepared bytes", preparedBytes.All(pair => pair.Key.ToArray().SequenceEqual(pair.Value)));
            try
            {
                owner.Replace(transform, CompiledSceneDocument.WriteMatrix(movedWorld)); edit.Redo();
                check("native LOD expanded boxes preserve graph and existing membership identities", ReferenceEquals(graph, document.NativeLods)
                    && document.Meshes.Where(mesh => mesh.NativeLod is not null).All(mesh => ReferenceEquals(mesh.NativeLod!.Graph, graph))
                    && graph.Groups.Any(group => group.WorldMax.X > preparedBounds.Single(old => old.Id == group.Id).WorldMax.X + 1000));
                var movedBounds = graph.Groups.Select(group => (group.Id, group.WorldMin, group.WorldMax)).ToArray(); document.SaveWorkingCopy();
                var reload = new CompiledSceneDocument(archive);
                check("native LOD persisted typed world boxes reproduce exactly on fresh load", reload.NativeLods!.Groups.Select(group => (group.Id, group.WorldMin, group.WorldMax)).SequenceEqual(movedBounds));
                var fresh = new List<CompiledResource>();
                foreach (var source in sources)
                {
                    var copy = new FileInfo(Path.Combine(fixture, "city", source.Name)); string freshFolder = Path.Combine(fixture, "fresh", source.Name);
                    using var stream = new MemoryStream(); SdsArchive.Pack(MafiaEnvironment.ExtractedDir(copy), MafiaEnvironment.Profile).Save(stream, new SdsWriteOptions { Compress = true });
                    stream.Position = 0; SdsArchive.Load(stream).Extract(freshFolder);
                    fresh.AddRange(SdsManifest.Load(freshFolder).GetFiles("Generic").Select(CompiledResource.Load));
                }
                var parsed = new CompiledGraph(fresh);
                check("native LOD compressed companions retain exact updated world-box payloads", graph.Groups.All(group => parsed.Objects.TryGetValue(group.Id, out var node)
                    && parsed.Data(parsed.Field(node, "m_WorldAABB")!).SequenceEqual(document.Graph.Data(document.Graph.Field(document.Graph.Objects[group.Id], "m_WorldAABB")!))));
                var loadedFrame = Walk(reload.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct().Single(row => row.SceneIdentity == frame.SceneIdentity);
                reload.InstallNativeLodBoundsBaseline(baseline);
                var loadedBack = reload.PrepareNativeLodBounds(new Dictionary<CompiledSceneDocument.CompiledFrameNode, Matrix4x4> { [loadedFrame] = originalWorld });
                loadedBack.Redo();
                check("native LOD original persisted baseline avoids bounds creep after reopen", reload.NativeLods!.Groups.Select(group => (group.Id, group.WorldMin, group.WorldMax)).SequenceEqual(preparedBounds));
                loadedBack.Undo();
                var back = document.PrepareNativeLodBounds(new Dictionary<CompiledSceneDocument.CompiledFrameNode, Matrix4x4> { [frame] = originalWorld });
                owner.Replace(transform, originalMatrix); back.Redo();
                check("native LOD move outward then back restores deterministic prepared bounds", graph.Groups.Select(group => (group.Id, group.WorldMin, group.WorldMax)).SequenceEqual(preparedBounds));
                back.Undo(); owner.Replace(transform, CompiledSceneDocument.WriteMatrix(movedWorld));
                edit.Undo(); owner.Replace(transform, originalMatrix); initial.Undo(); document.SaveWorkingCopy();
                check("native LOD undo restores ALL original resources manifests and exact graph boxes", Exact()
                    && graph.Groups.Select(group => (group.Id, group.WorldMin, group.WorldMax)).SequenceEqual(originalBounds));
            }
            finally
            {
                foreach (var pair in before) File.WriteAllBytes(pair.Key, pair.Value);
            }
            check("native LOD bounds validation never overwrites source archives", hashes.All(pair => Hash(pair.Key).SequenceEqual(pair.Value)));
        }
        finally
        {
            string root = Path.GetFullPath(MafiaEnvironment.GameRoot) + Path.DirectorySeparatorChar;
            foreach (string path in new[] { fixture, mirror })
                if (Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path) == unique && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
