using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Bridge;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledWaterGeometryAuthoring(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city_univers", "bc_city_univers.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        static IEnumerable<SdsFrameNode> Walk(IEnumerable<SdsFrameNode> rows)
        { foreach (var row in rows) { yield return row; foreach (var child in Walk(row.Children)) yield return child; } }
        var main = Walk(document.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .Distinct().Single(frame => frame.Name == "bc00_water_main");
        var binding = main.GeometryBinding ?? throw new InvalidDataException("Native main water has no geometry binding.");
        const ulong nativeMeshId = 0x0E21ACF92C39008C;
        var nativeMesh = document.Graph.Objects[nativeMeshId]; var sidecar = document.Graph.Owner(nativeMesh);
        check("ungrouped SceneCollection water uses its actual typed lod2 geometry sidecar", document.ValidateSceneCollectionMutation(main) is null
            && sidecar.Path.EndsWith("Generic_272_AAD7EF8BEB4871EB.scene.lod2.compiled", StringComparison.OrdinalIgnoreCase));
        var snapshot = document.Graph.Resources.ToDictionary(resource => resource.Path, resource => File.ReadAllBytes(resource.Path), StringComparer.OrdinalIgnoreCase);
        foreach (string folder in document.Graph.Resources.Select(resource => Path.GetDirectoryName(resource.Path)!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string manifest = Path.Combine(folder, "SDSContent.xml"); if (File.Exists(manifest)) snapshot.TryAdd(manifest, File.ReadAllBytes(manifest));
        }
        byte[] archiveHash = SHA256.HashData(File.ReadAllBytes(archive.FullName));
        string freshFolder = Path.GetFullPath(Path.Combine(".scratch", "water-geometry-authoring-" + Guid.NewGuid().ToString("N")));
        BridgeMeshApplier.ApplyResult? edit = null;
        try
        {
            var original = binding.DecodeLod(0) ?? throw new InvalidDataException("Native main-water mesh did not decode.");
            var payload = binding.Export(document, 0, out var refusal) ?? throw new InvalidOperationException(refusal);
            int vertex = checked((int)payload.LoopVertexIndices[0]);
            float delta = MathF.Max(.125f, MathF.Abs(payload.DecompressionFactor) * 2);
            payload.Positions[vertex] += new Vector3(0, 0, delta);
            edit = binding.TryApply(payload, 0, out refusal);
            check("native main-water fixed-topology vertex edit is supported without weakening a guard", edit is { Unchanged: false });
            if (edit is null) throw new InvalidOperationException("Native water geometry edit refused: " + refusal);
            edit.ApplyNew();
            var changed = binding.DecodeLod(0)!;
            check("native water sidecar edit moves real vertex Z while preserving topology", changed.Positions.Length == original.Positions.Length
                && changed.Indices.SequenceEqual(original.Indices) && changed.Positions[vertex].Z != original.Positions[vertex].Z
                && MathF.Abs(changed.Positions[vertex].Z - payload.Positions[vertex].Z) <= MathF.Abs(edit.NewDecompressionFactor) * .51f + .00001f);
            document.SaveWorkingCopy();
            check("native water vertex edit persists to its actual sidecar owner", !File.ReadAllBytes(sidecar.Path).SequenceEqual(snapshot[sidecar.Path]));
            var reloaded = new CompiledSceneDocument(archive);
            var reloadMain = Walk(reloaded.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct()
                .Single(frame => frame.Name == "bc00_water_main");
            check("native water geometry fresh document reproduces edited positions and indices", reloadMain.GeometryBinding!.DecodeLod(0) is { } reloadMesh
                && reloadMesh.Positions.SequenceEqual(changed.Positions) && reloadMesh.Indices.SequenceEqual(changed.Indices));
            using (var compressed = new MemoryStream())
            {
                SdsArchive.Pack(MafiaEnvironment.ExtractedDir(archive), MafiaEnvironment.Profile).Save(compressed, new SdsWriteOptions { Compress = true });
                compressed.Position = 0; SdsArchive.Load(compressed).Extract(freshFolder);
            }
            var manifest = SdsManifest.Load(freshFolder);
            var graph = new CompiledGraph(manifest.GetFiles("Generic").Select(CompiledResource.Load).ToArray());
            var decoder = new CompiledMeshDecoder(graph);
            var fresh = decoder.Decode(graph.Objects[binding.Visual.ObjectId], main.Name, main.WorldTransform, 0, bindToLocal: binding.GeometryBind)
                ?? throw new InvalidDataException("Compressed fresh water sidecar did not decode.");
            check("compressed water build independently parses the same typed sidecar geometry", graph.Objects.ContainsKey(nativeMeshId)
                && graph.Owner(graph.Objects[nativeMeshId]).Path.EndsWith(Path.GetFileName(sidecar.Path), StringComparison.OrdinalIgnoreCase)
                && fresh.Positions.SequenceEqual(changed.Positions) && fresh.Indices.SequenceEqual(changed.Indices));
            edit.RestoreOriginal(); document.SaveWorkingCopy();
            check("native water geometry undo restores ALL compiled resources and manifests exactly", snapshot.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)));
            edit.ApplyNew(); document.SaveWorkingCopy();
            check("native water geometry redo restores independently saved sidecar edit", binding.DecodeLod(0)!.Positions.SequenceEqual(changed.Positions));
            edit.RestoreOriginal(); document.SaveWorkingCopy();
            check("native water geometry redo then undo restores every byte without archive writes", snapshot.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
                && SHA256.HashData(File.ReadAllBytes(archive.FullName)).SequenceEqual(archiveHash));
        }
        finally
        {
            edit?.RestoreOriginal();
            foreach (var pair in snapshot) File.WriteAllBytes(pair.Key, pair.Value);
            string scratch = Path.GetFullPath(".scratch") + Path.DirectorySeparatorChar;
            if (freshFolder.StartsWith(scratch, StringComparison.OrdinalIgnoreCase) && Directory.Exists(freshFolder)) Directory.Delete(freshFolder, true);
        }
    }
}
