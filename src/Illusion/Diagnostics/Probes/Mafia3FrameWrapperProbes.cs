using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledFrameWrapperCreation(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_block_a_lod0.sds"));
        var templates = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city_crash", "bc_city_crash.sds"));
        if (!archive.Exists || !templates.Exists || !Directory.Exists(MafiaEnvironment.ExtractedDir(templates))) return;
        string? source = Directory.EnumerateFiles(MafiaEnvironment.ExtractedDir(templates), "Generic_4637_*.ires.compiled").FirstOrDefault();
        if (source is null) return;
        ulong[] identities = [0x146d28921dc3816e, 0x146d28921c6e01fc, 0x146d28921b16806c, 0x146d289219778028];
        var document = new CompiledSceneDocument(archive);
        var parent = EntityCreationRows(document.Roots).Select(node => node.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .FirstOrDefault(frame => frame.Name == "train_car_a_v1_bv01_00");
        if (parent is null) return;
        var baseline = document.Graph.Resources.ToDictionary(resource => resource.Path, resource => File.ReadAllBytes(resource.Path));
        var imports = new List<IFrameDuplication>(); IFrameDuplication? group = null, duplicate = null;
        try
        {
            group = document.CreateGroup("Native frame wrapper probe", parent, out var refusal) ?? throw new InvalidDataException(refusal);
            foreach (ulong identity in identities)
                imports.Add(document.ImportFrame(source, identity, (IFrameNode)group.Root.Source!, out refusal) ?? throw new InvalidDataException(refusal));
            foreach (var resource in document.Graph.Resources) baseline.TryAdd(resource.Path, File.ReadAllBytes(resource.Path));
            var original = imports.Select(item => (CompiledSceneDocument.CompiledFrameNode)item.Root.Source!).ToArray();
            var positions = original.Select(frame => frame.GeometryBinding!.DecodeLod(0)!.Positions.ToArray()).ToArray();
            duplicate = document.Duplicate((IFrameNode)group.Root.Source!, out refusal) ?? throw new InvalidDataException(refusal);
            var copied = EntityCreationRows([duplicate.Root]).Select(node => node.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
                .Distinct().Where(frame => frame.GeometryBinding is not null).ToArray();
            check("native FrameWrapper35 leaf imports form a duplicable Kit with four independent entity wrappers",
                copied.Length == 4 && document.CanDuplicate((IFrameNode)group.Root.Source!)
                && copied.All(frame => original.All(sourceFrame => frame.SceneIdentity != sourceFrame.SceneIdentity)));
            var binding = copied[0].GeometryBinding!;
            var payload = binding.Export(document, 0, out refusal) ?? throw new InvalidDataException(refusal);
            payload.Positions = payload.Positions.Select(position => position + new Vector3(.125f, 0, 0)).ToArray();
            var edit = binding.TryApply(payload, 0, out refusal) ?? throw new InvalidDataException(refusal); edit.ApplyNew();
            check("nested FrameWrapper35 copy geometry edits preserve all original imported leaves",
                Enumerable.Range(0, original.Length).All(index => original[index].GeometryBinding!.DecodeLod(0)!.Positions.SequenceEqual(positions[index])));
            document.SaveWorkingCopy();
            var manifests = document.Graph.Resources.Select(resource => Path.Combine(Path.GetDirectoryName(resource.Path)!, "SDSContent.xml"))
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToDictionary(path => path, File.ReadAllBytes);
            try
            {
                foreach (var companion in document.CompanionArchives.Append(archive).DistinctBy(file => file.FullName))
                {
                    var packed = SdsArchive.Pack(MafiaEnvironment.ExtractedDir(companion), MafiaEnvironment.Profile);
                    using var stream = new MemoryStream(); packed.Save(stream, new SdsWriteOptions { Compress = true });
                    stream.Position = 0; SdsArchive.Load(stream).Extract(MafiaEnvironment.ExtractedDir(companion));
                }
                var fresh = new CompiledSceneDocument(archive);
                var frames = EntityCreationRows(fresh.Roots).Select(node => node.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct().ToArray();
                var root = frames.Single(frame => frame.SceneIdentity == ((CompiledSceneDocument.CompiledFrameNode)duplicate.Root.Source!).SceneIdentity);
                check("nested FrameWrapper35 native ownership and authored geometry survive compressed fresh reload",
                    fresh.CanDuplicate(root) && frames.Single(frame => frame.SceneIdentity == copied[0].SceneIdentity)
                        .GeometryBinding!.DecodeLod(0)!.Positions.SequenceEqual(binding.DecodeLod(0)!.Positions));
            }
            finally { foreach (var (path, bytes) in manifests) File.WriteAllBytes(path, bytes); }
            duplicate.Detach(); foreach (var item in imports.AsEnumerable().Reverse()) item.Detach(); group.Detach(); document.SaveWorkingCopy();
            check("nested FrameWrapper35 group creation/import/duplication undo restores every native resource byte exactly",
                baseline.All(pair => File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value)));
        }
        finally
        {
            duplicate?.Detach(); foreach (var item in imports.AsEnumerable().Reverse()) item.Detach(); group?.Detach(); document.SaveWorkingCopy();
            foreach (var (path, bytes) in baseline) File.WriteAllBytes(path, bytes);
        }
    }
}
