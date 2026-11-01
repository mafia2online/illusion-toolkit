using System.Buffers.Binary;
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
    private static IEnumerable<ISceneHierarchyNode> EntityCreationRows(IEnumerable<ISceneHierarchyNode> roots)
    {
        foreach (var row in roots)
        {
            yield return row;
            foreach (var child in EntityCreationRows(row.Children)) yield return child;
        }
    }

    private static void TestCompiledStaticEntityCreation(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_block_a_lod0.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        var source = EntityCreationRows(document.Roots).Select(n => n.Source)
            .OfType<CompiledSceneDocument.CompiledFrameNode>().FirstOrDefault(f => f.SceneIdentity == "guid:193582078FE98132");
        if (source is null) return;
        var baseline = document.Graph.Resources.ToDictionary(r => r.Path, r => File.ReadAllBytes(r.Path));
        var manifests = document.CompanionArchives.Append(archive).DistinctBy(a => a.FullName)
            .Select(a => Path.Combine(MafiaEnvironment.ExtractedDir(a), "SDSContent.xml")).Where(File.Exists)
            .ToDictionary(p => p, File.ReadAllBytes);
        IFrameDuplication? copy = null;
        try
        {
            copy = document.Duplicate(source, out var why) ?? throw new InvalidDataException(why);
            var frame = (CompiledSceneDocument.CompiledFrameNode)copy.Root.Source!;
            var original = source.GeometryBinding!.DecodeLod(0)!;
            var payload = frame.GeometryBinding!.Export(document, 0, out why) ?? throw new InvalidDataException(why);
            payload.Positions = payload.Positions.Select(p => p + new Vector3(.125f, 0, 0)).ToArray();
            var geometry = frame.GeometryBinding.TryApply(payload, 0, out why) ?? throw new InvalidDataException(why);
            geometry.ApplyNew();
            frame.LocalTransform = Matrix4x4.CreateTranslation(3, 5, 7);
            check("native StaticEntity32 copy owns independent editable geometry", source.GeometryBinding.DecodeLod(0)!.Positions.SequenceEqual(original.Positions)
                && !frame.GeometryBinding.DecodeLod(0)!.Positions.SequenceEqual(original.Positions));
            document.SaveWorkingCopy();
            EntityCreationPackChanged(document, baseline);
            var fresh = new CompiledSceneDocument(archive);
            var loaded = EntityCreationRows(fresh.Roots).Where(n => n.Name == copy.Root.Name).Select(n => n.Source)
                .OfType<CompiledSceneDocument.CompiledFrameNode>().First();
            check("native StaticEntity32 compressed fresh reload retains role and geometry", fresh.CanDuplicate(loaded)
                && loaded.LocalTransform == frame.LocalTransform
                && !loaded.GeometryBinding!.DecodeLod(0)!.Positions.SequenceEqual(original.Positions));
            geometry.RestoreOriginal(); copy.Detach(); document.SaveWorkingCopy();
            check("native StaticEntity32 creation undo restores every resource byte", baseline.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
        }
        finally
        {
            copy?.Detach();
            foreach (var (path, bytes) in baseline.Concat(manifests)) File.WriteAllBytes(path, bytes);
        }
    }

    private static void TestCompiledVehicleTemplateCreation(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "cars", "bruno_ravine_sheriff.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        var root = document.Roots.FirstOrDefault(n => n.Name == "bruno_ravine_sheriff");
        if (root?.Source is not CompiledSceneDocument.CompiledFrameNode source) return;
        var baseline = document.Graph.Resources.ToDictionary(r => r.Path, r => File.ReadAllBytes(r.Path));
        string manifest = Path.Combine(MafiaEnvironment.ExtractedDir(archive), "SDSContent.xml");
        byte[] manifestBytes = File.ReadAllBytes(manifest);
        IFrameDuplication? copy = null;
        try
        {
            copy = document.Duplicate(source, out var why) ?? throw new InvalidDataException(why);
            CompiledSceneDocument.CompiledFrameNode Body(ISceneHierarchyNode row) => EntityCreationRows([row]).Select(n => n.Source)
                .OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct().Single(f => f.Name == "Root");
            var original = Body(root); var copied = Body(copy.Root);
            var originalRig = original.VehicleRig!; var copiedRig = copied.VehicleRig!;
            var before = originalRig.LocalRest(13);
            copiedRig.BoneSource(13).LocalTransform = before * Matrix4x4.CreateTranslation(.025f, 0, 0);
            check("native Car10 template owns independent authored rig", originalRig.Immutable.ObjectId != copiedRig.Immutable.ObjectId
                && originalRig.LocalRest(13) == before);
            document.SaveWorkingCopy(); EntityCreationPackChanged(document, baseline);
            var fresh = new CompiledSceneDocument(archive);
            var loaded = fresh.Roots.Single(n => n.Name == copy.Root.Name);
            check("native Car10 template compressed fresh reload retains independent rig", fresh.CanDuplicate((IFrameNode)loaded.Source!)
                && Body(loaded).VehicleRig!.LocalRest(13) == copiedRig.LocalRest(13));
            copiedRig.BoneSource(13).LocalTransform = before;
            copy.Detach(); document.SaveWorkingCopy();
            check("native Car10 template undo restores every resource byte", baseline.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
            copy.Reattach(); document.SaveWorkingCopy();
            check("native Car10 template redo survives fresh reload", new CompiledSceneDocument(archive).Roots.Any(n => n.Name == copy.Root.Name));
            copy.Detach(); document.SaveWorkingCopy();
            check("native Car10 template redo/undo restores every resource byte", baseline.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
        }
        finally
        {
            copy?.Detach();
            foreach (var (path, bytes) in baseline) File.WriteAllBytes(path, bytes);
            File.WriteAllBytes(manifest, manifestBytes);
        }
    }

    private static void EntityCreationPackChanged(CompiledSceneDocument document, IReadOnlyDictionary<string, byte[]> baseline)
    {
        foreach (var archive in document.CompanionArchives.Append(document.SourceArchive).DistinctBy(a => a.FullName))
        {
            string folder = MafiaEnvironment.ExtractedDir(archive);
            if (!baseline.Any(p => Path.GetDirectoryName(p.Key) == folder && !File.ReadAllBytes(p.Key).SequenceEqual(p.Value))) continue;
            var packed = SdsArchive.Pack(folder, MafiaEnvironment.Profile);
            using var stream = new MemoryStream();
            packed.Save(stream, new SdsWriteOptions { Compress = true }); stream.Position = 0;
            SdsArchive.Load(stream).Extract(folder);
        }
    }

    private static void TestCompiledVehicleMapImport(Action<string, bool> check)
    {
        var scene = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_block_a_lod0.sds"));
        var car = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "cars", "bruno_ravine_sheriff.sds"));
        if (!scene.Exists || !car.Exists) return;
        var document = new CompiledSceneDocument(scene);
        var parent = EntityCreationRows(document.Roots).FirstOrDefault(n => n.Name == "train_car_a_v1_bv01_00")?.Source as CompiledSceneDocument.CompiledFrameNode;
        if (parent is null) return;
        var resource = document.Graph.Owner(document.Graph.Objects[Convert.ToUInt64(parent.SceneIdentity[5..], 16)]);
        string folder = Path.GetDirectoryName(resource.Path)!;
        var baseline = document.Graph.Resources.ToDictionary(r => r.Path, r => File.ReadAllBytes(r.Path));
        string manifest = Path.Combine(folder, "SDSContent.xml"); byte[] manifestBytes = File.ReadAllBytes(manifest);
        var originalFiles = Directory.GetFiles(folder).Select(Path.GetFileName).ToHashSet();
        string tempPath = Path.Combine(MafiaEnvironment.CityFolder, "bc_probe_vehicle_" + Guid.NewGuid().ToString("N") + "_lod1.sds");
        string extracted = MafiaEnvironment.ExtractedDir(new FileInfo(tempPath));
        IFrameDuplication? first = null, second = null;
        try
        {
            first = document.ImportVehicle(car, parent, out var why) ?? throw new InvalidDataException(why);
            second = document.ImportVehicle(car, parent, out why) ?? throw new InvalidDataException(why);
            foreach (var added in document.Graph.Resources) baseline.TryAdd(added.Path, File.ReadAllBytes(added.Path));
            CompiledSceneDocument.CompiledFrameNode Body(ISceneHierarchyNode row) => EntityCreationRows([row]).Select(n => n.Source)
                .OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct().Single(f => f.Name == "Root");
            check("placed Car10 instances own independent rig and native geometry", Body(first.Root).VehicleRig!.Immutable.ObjectId != Body(second.Root).VehicleRig!.Immutable.ObjectId
                && !ReferenceEquals(Body(first.Root).GeometryBinding!.Visual, Body(second.Root).GeometryBinding!.Visual));
            TestVehiclePaintScopes(document, Body(first.Root), Body(second.Root), check);
            TestVehiclePlacementBounds(document, parent, first.Root, check);
            VerifyVehiclePlacementHostGuards(document, (IFrameNode)first.Root.Source!, check);
            document.SaveWorkingCopy();
            TestFarVehicleImportRefusal(document, parent, car, check);
            TestVehicleIncrementalCollisionUndo(document, first.Root, check);
            var packed = SdsArchive.Pack(folder, MafiaEnvironment.Profile);
            using (var stream = File.Create(tempPath)) packed.Save(stream, new SdsWriteOptions { Compress = true });
            var fresh = new CompiledSceneDocument(new FileInfo(tempPath));
            var reloaded = EntityCreationRows(fresh.Roots).Single(n => n.Name == first.Root.Name);
            check("placed Car10 compressed standalone archive retains native body and wheels", Body(reloaded).GeometryBinding!.DecodeLod(0)!.TriangleCount > 18000
                && EntityCreationRows([reloaded]).Any(n => n.Kind == "Vehicle wheel"));
            check("placed Car10 resolves without a foreign car GENR dependency", fresh.Graph.Resources.All(r => !r.Path.Contains(Path.DirectorySeparatorChar + "cars" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            var tokenTables = fresh.Graph.Resources.ToDictionary(r => r, r => CompiledDependencyToken.Entries(fresh.Graph, r)
                .Select(e => CompiledDependencyToken.EntryKey(fresh.Graph, e)).ToHashSet());
            var tokens = fresh.Graph.Resources.SelectMany(r => r.Elements).Where(n => n.Event == 4 && n.DataSize == 8
                && fresh.Graph.Kind(n) == 442 && BinaryPrimitives.ReadUInt32LittleEndian(fresh.Graph.Data(n)[4..]) == n.TypeId).ToArray();
            check("placed Car10 retains resource-local explicit texture and model dependencies", tokens.All(t =>
            {
                ulong key = CompiledDependencyToken.Key(fresh.Graph, t);
                return key == 0 || tokenTables[fresh.Graph.Owner(t)].Contains(key);
            }));
            check("imported wheel model templates remain outside placed world roots", fresh.Roots.All(n => !n.Name.StartsWith("itk_", StringComparison.Ordinal)));
            first.Detach(); document.SaveWorkingCopy();
            check("undo one Car10 import retains the other and shared resource payloads", EntityCreationRows(new CompiledSceneDocument(scene).Roots).Any(n => n.Name == second.Root.Name));
            second.Detach(); document.SaveWorkingCopy();
            check("Car10 map import undo restores every GENR and exact resource manifest", baseline.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value))
                && File.ReadAllBytes(manifest).SequenceEqual(manifestBytes));
            check("Car10 map import undo removes only its carried files", Directory.GetFiles(folder).Select(Path.GetFileName).ToHashSet().SetEquals(originalFiles));
        }
        finally
        {
            first?.Detach(); second?.Detach();
            try { document.SaveWorkingCopy(); }
            finally
            {
                foreach (var (path, bytes) in baseline) File.WriteAllBytes(path, bytes);
                File.WriteAllBytes(manifest, manifestBytes);
                File.Delete(tempPath);
                string expected = Path.GetFullPath(MafiaEnvironment.ResourcesFolder!) + Path.DirectorySeparatorChar;
                if (Directory.Exists(extracted) && Path.GetFullPath(extracted).StartsWith(expected, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(extracted).StartsWith("bc_probe_vehicle_", StringComparison.Ordinal)) Directory.Delete(extracted, true);
            }
        }
    }
}
