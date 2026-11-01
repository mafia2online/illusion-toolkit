using System.Buffers.Binary;
using System.IO;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledStockFrameKit(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_block_a_lod0.sds"));
        var templates = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city_crash", "bc_city_crash.sds"));
        if (!archive.Exists || !templates.Exists || !Directory.Exists(MafiaEnvironment.ExtractedDir(templates))) return;
        string? source = Directory.EnumerateFiles(MafiaEnvironment.ExtractedDir(templates), "Generic_4637_*.ires.compiled").FirstOrDefault();
        if (source is null) return;
        var document = new CompiledSceneDocument(archive);
        var parent = EntityCreationRows(document.Roots).Select(node => node.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .FirstOrDefault(frame => frame.Name == "train_car_a_v1_bv01_00");
        if (parent is null) return;
        var baseline = document.Graph.Resources.ToDictionary(resource => resource.Path, resource => File.ReadAllBytes(resource.Path));
        var manifestBaseline = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var expectedDependencies = new Dictionary<ulong, byte[]>();
        IFrameDuplication? first = null, second = null, duplicate = null;
        IEnumerable<CompiledSceneDocument.CompiledFrameNode> Frames(ISceneHierarchyNode root) => EntityCreationRows([root])
            .Select(node => node.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct();
        bool Tokens(CompiledSceneDocument doc, IEnumerable<CompiledSceneDocument.CompiledFrameNode> frames)
        {
            var graph = doc.Graph; int count = 0;
            foreach (var frame in frames)
                foreach (var component in graph.Items(graph.Field(graph.Objects[Convert.ToUInt64(frame.SceneIdentity[5..], 16)], "m_Components")).Select(graph.Resolve))
                    foreach (var token in graph.Fields(component!).Values.Where(field => field.Event == 4 && field.DataSize == 8 && field.TypeId == 0x5458bf13))
                    {
                        ulong key = CompiledDependencyToken.Key(graph, token); if (key == 0) continue;
                        var row = CompiledDependencyToken.Entries(graph, graph.Owner(token)).SingleOrDefault(entry => CompiledDependencyToken.EntryKey(graph, entry) == key);
                        if (row is null || graph.Data(row).Length != 16 || BinaryPrimitives.ReadUInt32LittleEndian(graph.Data(row)[8..]) != 0x0d125062
                            || !expectedDependencies.TryGetValue(key, out var expected) || !graph.Data(row).SequenceEqual(expected)) return false;
                        count++;
                    }
            return count == 5;
        }
        try
        {
            first = document.ImportFrame(source, 0x146d289222110168, parent, out var refusal) ?? throw new InvalidDataException(refusal);
            second = document.ImportFrame(source, 0x146d289222110168, parent, out refusal) ?? throw new InvalidDataException(refusal);
            var sourceOwner = document.Graph.Resources.Single(resource => resource.Path.Equals(source, StringComparison.OrdinalIgnoreCase));
            foreach (var entry in CompiledDependencyToken.Entries(document.Graph, sourceOwner))
                expectedDependencies[CompiledDependencyToken.EntryKey(document.Graph, entry)] = document.Graph.Data(entry).ToArray();
            foreach (var resource in document.Graph.Resources)
            {
                baseline.TryAdd(resource.Path, File.ReadAllBytes(resource.Path));
                string manifest = Path.Combine(Path.GetDirectoryName(resource.Path)!, "SDSContent.xml");
                if (File.Exists(manifest)) manifestBaseline.TryAdd(manifest, File.ReadAllBytes(manifest));
            }
            var firstFrames = Frames(first.Root).ToArray();
            check("actual stock Kit imports its four FrameWrapper35 children and audio-equipped Tree22 with independent ownership",
                firstFrames.Length == 6 && firstFrames.Count(frame => frame.GeometryBinding is not null) == 5
                && document.CanDuplicate((IFrameNode)first.Root.Source!) && document.CanDuplicate((IFrameNode)second.Root.Source!));
            duplicate = document.Duplicate((IFrameNode)first.Root.Source!, out refusal) ?? throw new InvalidDataException(refusal);
            document.SaveWorkingCopy();
            var fresh = new CompiledSceneDocument(archive);
            var root = EntityCreationRows(fresh.Roots).Single(node => node.Name == duplicate.Root.Name);
            check("stock audio-tree event tokens resolve as five resource-local unsigned-int dependencies after save",
                Tokens(fresh, Frames(root)) && fresh.CanDuplicate((IFrameNode)root.Source!));
            foreach (var companion in document.CompanionArchives.Append(archive).DistinctBy(file => file.FullName))
            {
                var packed = SdsArchive.Pack(MafiaEnvironment.ExtractedDir(companion), MafiaEnvironment.Profile);
                using var stream = new MemoryStream(); packed.Save(stream, new SdsWriteOptions { Compress = true });
                stream.Position = 0; SdsArchive.Load(stream).Extract(MafiaEnvironment.ExtractedDir(companion));
            }
            fresh = new CompiledSceneDocument(archive); root = EntityCreationRows(fresh.Roots).Single(node => node.Name == duplicate.Root.Name);
            check("actual stock nested Kit and native audio dependency values survive compressed fresh reload",
                Tokens(fresh, Frames(root)) && Frames(root).Where(frame => frame.GeometryBinding is not null)
                    .Sum(frame => frame.GeometryBinding!.DecodeLod(0)!.TriangleCount) == 3035);
            first.Detach(); document.SaveWorkingCopy();
            fresh = new CompiledSceneDocument(archive); root = EntityCreationRows(fresh.Roots).Single(node => node.Name == second.Root.Name);
            check("undo one stock Kit import retains shared audio dependency entries used by another import", Tokens(fresh, Frames(root)));
            second.Detach(); document.SaveWorkingCopy();
            fresh = new CompiledSceneDocument(archive); root = EntityCreationRows(fresh.Roots).Single(node => node.Name == duplicate.Root.Name);
            check("independent duplicate retains native dependency and schema leases after both original imports are removed",
                Tokens(fresh, Frames(root)) && fresh.CanDuplicate((IFrameNode)root.Source!));
            var surviving = document.Graph.Resources.ToDictionary(resource => resource.Path, resource => File.ReadAllBytes(resource.Path));
            duplicate.Detach(); document.SaveWorkingCopy(); duplicate.Reattach(); document.SaveWorkingCopy();
            var changed = surviving.Where(pair => !File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value)).Select(pair => Path.GetFileName(pair.Key)).ToArray();
            check("duplicate dependency and schema leases redo without either original import and preserve exact saved resource bytes"
                + (changed.Length == 0 ? "" : " (changed: " + string.Join(",", changed) + ")"), changed.Length == 0);
            duplicate.Detach(); document.SaveWorkingCopy();
            check("actual stock Kit import/duplicate undo restores every compiled resource and manifest byte exactly",
                baseline.All(pair => File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value))
                && manifestBaseline.All(pair => File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value)));
        }
        finally
        {
            duplicate?.Detach(); second?.Detach(); first?.Detach(); document.SaveWorkingCopy();
            foreach (var (path, bytes) in baseline) File.WriteAllBytes(path, bytes);
            foreach (var (path, bytes) in manifestBaseline) File.WriteAllBytes(path, bytes);
        }
    }
}
