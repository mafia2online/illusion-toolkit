using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledVegetationCreation(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_block_a_lod0.sds"));
        var templates = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city_crash", "bc_city_crash.sds"));
        if (!archive.Exists || !templates.Exists || !Directory.Exists(MafiaEnvironment.ExtractedDir(templates))) return;
        const ulong sourceId = 0x16bca9682a820108;
        string? source = Directory.EnumerateFiles(MafiaEnvironment.ExtractedDir(templates), "*.ires.compiled")
            .FirstOrDefault(path => CompiledResource.ReadObjectIds(path).Contains(sourceId));
        if (source is null) return;
        var document = new CompiledSceneDocument(archive);
        var parent = EntityCreationRows(document.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .FirstOrDefault(frame => frame.Name == "train_car_a_v1_bv01_00");
        if (parent is null) return;
        var baseline = document.Graph.Resources.ToDictionary(resource => resource.Path, resource => File.ReadAllBytes(resource.Path));
        IFrameDuplication? imported = null, duplicate = null;
        try
        {
            imported = document.ImportFrame(source, sourceId, parent, out var refusal) ?? throw new InvalidDataException(refusal);
            foreach (var resource in document.Graph.Resources) baseline.TryAdd(resource.Path, File.ReadAllBytes(resource.Path));
            var original = (CompiledSceneDocument.CompiledFrameNode)imported.Root.Source!;
            var mesh = original.GeometryBinding!.DecodeLod(0)!;
            duplicate = document.Duplicate(original, out refusal) ?? throw new InvalidDataException(refusal);
            var copy = (CompiledSceneDocument.CompiledFrameNode)duplicate.Root.Source!;
            var payload = copy.GeometryBinding!.Export(document, 0, out refusal) ?? throw new InvalidDataException(refusal);
            payload.Positions = payload.Positions.Select(position => position + new Vector3(.125f, 0, 0)).ToArray();
            var edit = copy.GeometryBinding.TryApply(payload, 0, out refusal) ?? throw new InvalidDataException(refusal);
            edit.ApplyNew(); copy.LocalTransform = Matrix4x4.CreateTranslation(2, 4, 6);
            check("native Tree22 imports and duplicates independent editable geometry with its own initialization contract",
                mesh.TriangleCount == 1173 && document.CanDuplicate(original) && document.CanDuplicate(copy)
                && original.GeometryBinding.DecodeLod(0)!.Positions.SequenceEqual(mesh.Positions)
                && !copy.GeometryBinding.DecodeLod(0)!.Positions.SequenceEqual(mesh.Positions));
            document.SaveWorkingCopy();
            bool Fresh()
            {
                var loaded = new CompiledSceneDocument(archive);
                var found = EntityCreationRows(loaded.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
                    .Distinct().Single(frame => frame.SceneIdentity == copy.SceneIdentity);
                return loaded.CanDuplicate(found) && !found.GeometryBinding!.DecodeLod(0)!.Positions.SequenceEqual(mesh.Positions);
            }
            check("native Tree22 save and fresh document preserve edited geometry and wrapper role", Fresh());
            var manifests = document.CompanionArchives.Append(archive).DistinctBy(file => file.FullName)
                .Select(file => Path.Combine(MafiaEnvironment.ExtractedDir(file), "SDSContent.xml")).Where(File.Exists)
                .ToDictionary(path => path, File.ReadAllBytes);
            try
            {
                foreach (var companion in document.CompanionArchives.Append(archive).DistinctBy(file => file.FullName))
                {
                    var packed = SdsArchive.Pack(MafiaEnvironment.ExtractedDir(companion), MafiaEnvironment.Profile);
                    using var stream = new MemoryStream(); packed.Save(stream, new SdsWriteOptions { Compress = true });
                    stream.Position = 0; SdsArchive.Load(stream).Extract(MafiaEnvironment.ExtractedDir(companion));
                }
                check("native Tree22 compressed companion archives reload with independent native geometry", Fresh());
            }
            finally { foreach (var (path, bytes) in manifests) File.WriteAllBytes(path, bytes); }
            edit.RestoreOriginal(); duplicate.Detach(); imported.Detach(); document.SaveWorkingCopy();
            check("native Tree22 import and duplicate undo restore every compiled resource byte exactly",
                baseline.All(pair => File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value)));
        }
        finally
        {
            duplicate?.Detach(); imported?.Detach(); document.SaveWorkingCopy();
            foreach (var (path, bytes) in baseline) File.WriteAllBytes(path, bytes);
        }
    }
}
