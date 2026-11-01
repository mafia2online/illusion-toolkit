using System.IO;
using System.Security.Cryptography;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Assets.World;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

/// <summary>Run against a copied install: --probe-mafia3 &lt;copied-root&gt;. Never builds into its input archives.</summary>
internal static partial class Mafia3Probes
{
    internal static void RunEditing(string root)
    {
        // Tests construct and close unshown windows. Keep the CLI application alive
        // for the entire suite; App.OnStartup shuts it down after this method returns.
        if (System.Windows.Application.Current is { } application) application.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var report = new StringBuilder();
        int failures = 0;
        bool ready = false;
        void Check(string name, bool passed)
        {
            report.AppendLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed) failures++;
        }
        try
        {
            if (!MafiaEnvironment.TryInitialize(root, out string? error)) throw new InvalidOperationException(error);
            if (MafiaEnvironment.Profile != GameProfile.MafiaIII) throw new InvalidOperationException("A copied Mafia III root is required.");
            ready = true;
            TestEdits(Check);
            TestStructuralEdits(Check);
            TestPropImport(Check);
            TestCompiledAuthoring(Check);
            TestCompiledSceneCollections(Check);
            TestCompiledSceneCollectionGuards(Check);
            TestCompiledNativeLod(Check);
            TestCompiledNativeLodGpu(Check);
            TestLinkedProxyHostPreparation(Check);
            TestCompiledLinkedProxyAuthoring(Check);
            TestCompiledLinkedProxyMaterials(Check);
            TestCompiledLinkedProxyGeometry(Check);
            TestCompiledNavigationNetworkUi(Check);
            TestCompiledNavigationNetwork(Check);
            TestCompiledPooledProxyGeometry(Check);
            TestCompiledProxyGeometryBatch(Check);
            TestLinkedProxyGeometryBatchHost(Check);
            TestCompiledLinkedProxyReplacement(Check);
            TestCompiledLinkedProxyStructure(Check);
            TestPreparedProxyRigStructure(Check);
            TestPreparedProxyRigHost(Check);
            TestPreparedProxyRigCollision(Check);
            TestPreparedProxyRigCollisionHost(Check);
            TestPreparedProxyRigGlass(Check);
            TestPreparedProxyRigGlassHost(Check);
            TestPreparedProxySkin(Check);
            TestPreparedProxySkinHost(Check);
            TestCompiledLinkedProxyHighMaterials(Check);
            TestLinkedProxyGeometryHost(Check);
            TestLinkedProxyStructureHost(Check);
            TestCompiledNativeLodBounds(Check);
            TestCompiledNativeLodRendering(Check);
            TestNativeInstanceLod(Check);
            TestNativePlacementRest(Check);
            TestNativeVegetationWind(Check);
            TestNativeVegetationWindBounds(Check);
            TestNativeVegetationWindScene(Check);
            TestNativeInstanceUpdates(Check);
            TestNativePaintTriangle(Check);
            TestNativePaintSpacing(Check);
            TestNativePaintClipping(Check);
            TestNativePaintTransforms(Check);
            TestNativePaintCells(Check);
            TestNativePaintFields(Check);
            TestNativePaintHost(Check);
            TestCompiledLinkedProxyBreadth(Check);
            TestVehicleVisualMaterials(Check);
            TestCompiledNavigation(Check);
            TestCompiledNavigationCook(Check);
            TestCompiledNavigationTopology(Check);
            TestCompiledNavigationSubdivision(Check);
            TestCompiledNavigationExtrusion(Check);
            TestCompiledCollisionRemoval(Check);
            TestCompiledCollisionAttachments(Check);
            TestCompiledTriangleCollision(Check);
            TestCompiledWorldCollision(Check);
            TestNativePhysicsOracle(Check);
            TestCompiledEffects(Check);
            TestCompiledWaterWaves(Check);
            TestCompiledWaterWavePreview(Check);
            TestCompiledWaterGeometryAuthoring(Check);
            TestNativeWaterSurface(Check);
            TestNativeWaterOptics(Check);
            TestNativeWaterFoam(Check);
            TestNativeWaterFoamContext(Check);
            TestNativeWaterFoamSurface(Check);
            TestNativeWaterFoamViewport(Check);
            TestNativeRainRipple(Check);
            TestNativeWaterContact(Check);
            TestNativeWaterContactViewport(Check);
            TestNativeWaterContactJointViewport(Check);
            TestMipmappedWaterTextures(Check);
            TestCompiledSceneAnimation(Check);
            TestNativeAnimationAuthoring(Check);
            TestSkinnedPreviewBounds(Check);
            TestCompiledAnimationPreviewUi(Check);
            TestCompiledMorphPreview(Check);
            TestCompiledMorphAnimation(Check);
            TestCompiledStaticEntityCreation(Check);
            TestCompiledVegetationCreation(Check);
            TestCompiledFrameWrapperCreation(Check);
            TestCompiledStockFrameKit(Check);
            TestCompiledVehicleTemplateCreation(Check);
            TestCompiledVehicleMapImport(Check);
        }
        catch (Exception ex) { failures++; report.AppendLine("[FAIL] " + ex); }
        if (ready)
        {
            try { TestCopiedResourceRestoration(Check); }
            catch (Exception ex) { failures++; report.AppendLine("[FAIL] final working-resource audit: " + ex); }
        }
        report.AppendLine($"Failures: {failures}");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "illusion_mafia3_editing.txt"), report.ToString());
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    internal static void Run(string root)
    {
        if (System.Windows.Application.Current is { } application) application.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var report = new StringBuilder();
        int failures = 0;
        bool ready = false;
        string output = Path.Combine(Path.GetTempPath(), "illusion_mafia3.txt");
        File.WriteAllText(output, "Running copied-file Mafia III regression..." + Environment.NewLine);
        void Check(string name, bool result)
        {
            string line = $"[{(result ? "PASS" : "FAIL")}] {name}";
            report.AppendLine(line);
            File.AppendAllText(output, line + Environment.NewLine);
            if (!result) failures++;
        }
        try
        {
            if (!MafiaEnvironment.TryInitialize(root, out string? error)) throw new InvalidOperationException(error);
            Check("detect Mafia III", MafiaEnvironment.Profile == GameProfile.MafiaIII);
            ready = MafiaEnvironment.Profile == GameProfile.MafiaIII;
            var files = ResourceUnpacker.EnumerateGameSds();
            Check("fixture has archives", files.Count > 0);
            foreach (var file in files)
            {
                byte[] original = File.ReadAllBytes(file.FullName);
                byte[] hash = SHA256.HashData(original);
                var archive = SdsArchive.Open(file.FullName);
                string extracted = SdsMeshLoader.EnsureExtracted(file);
                var packed = SdsArchive.Pack(extracted, MafiaEnvironment.Profile);
                bool Equal(SdsArchive left, SdsArchive right) => left.Version == right.Version && left.Flags == right.Flags && left.ResourceInfoXml == right.ResourceInfoXml
                    && left.SlotRamRequired == right.SlotRamRequired && left.SlotVramRequired == right.SlotVramRequired
                    && left.OtherRamRequired == right.OtherRamRequired && left.OtherVramRequired == right.OtherVramRequired
                    && left.Unknown20.SequenceEqual(right.Unknown20) && left.ResourceTypes.SequenceEqual(right.ResourceTypes)
                    && left.Entries.Count == right.Entries.Count && left.Entries.Zip(right.Entries).All(pair =>
                        pair.First.TypeId == pair.Second.TypeId && pair.First.Version == pair.Second.Version
                        && pair.First.FileHash == pair.Second.FileHash && pair.First.SlotRamRequired == pair.Second.SlotRamRequired
                        && pair.First.SlotVramRequired == pair.Second.SlotVramRequired && pair.First.OtherRamRequired == pair.Second.OtherRamRequired
                        && pair.First.OtherVramRequired == pair.Second.OtherVramRequired && pair.First.Data!.SequenceEqual(pair.Second.Data!));
                Check(file.Name + " extraction/repack preserves all resources", Equal(archive, packed));
                foreach (bool compress in new[] { false, true })
                {
                    using var buffer = new MemoryStream();
                    packed.Save(buffer, new SdsWriteOptions { Compress = compress });
                    buffer.Position = 0;
                    Check(file.Name + $" save/reopen compress={compress}", Equal(archive, SdsArchive.Load(buffer)));
                }
                var manifest = SdsManifest.Load(extracted);
                foreach (string tablePath in manifest.GetFiles("SystemObjectDatabase"))
                    if (Illusion.Formats.Compiled.CompiledGearboxTable.TryRead(File.ReadAllBytes(tablePath)) is { } gearboxes)
                    {
                        byte[] tableBefore = gearboxes.ToBytes();
                        Check("gearbox database rows are decoded", gearboxes.Count > 0);
                        for (int row = 0; row < gearboxes.Count; row++)
                            foreach (var property in new Illusion.Assets.Compiled.CompiledGearboxProperties(gearboxes, row)
                                .GetPropertyGroups().SelectMany(g => g.Properties).Where(p => p.Set is not null))
                            {
                                object? value = property.Get();
                                object changedValue = value switch { float f => f + 0.25f, long n => n == 0 ? 1L : 0L, bool b => !b,
                                    _ => throw new InvalidOperationException("Unexpected gearbox field kind.") };
                                property.Set!(changedValue);
                                Check("gearbox edit reaches " + property.Id, !gearboxes.ToBytes().SequenceEqual(tableBefore));
                                property.Set(value);
                                Check("gearbox undo preserves all bytes " + property.Id, gearboxes.ToBytes().SequenceEqual(tableBefore));
                            }
                    }
                Check(file.Name + " names match MCP", archive.ResolveEntryNames().SequenceEqual(manifest.Entries.Select(e => e.File)));
                Check(file.Name + " resource browser files exist", ArchiveContents.ReadFrom(file, extracted).Resources.All(r => r.File.Exists));
                if (archive.Entries.Count > 0 && archive.Entries[0].Data!.Length > 0)
                {
                    var first = manifest.Entries[0];
                    string path = Path.Combine(extracted, first.File);
                    byte[] before = File.ReadAllBytes(path);
                    if (before.Length > 0)
                    {
                        byte[] changed = (byte[])before.Clone();
                        changed[^1] ^= 1;
                        try
                        {
                            File.WriteAllBytes(path, changed);
                            var edited = SdsArchive.Pack(extracted, MafiaEnvironment.Profile);
                            using var editedBytes = new MemoryStream();
                            edited.Save(editedBytes, new SdsWriteOptions());
                            editedBytes.Position = 0;
                            var reread = SdsArchive.Load(editedBytes);
                            Check(file.Name + " edited payload survives build", reread.Entries[0].Data![^1] == changed[^1]
                                && reread.Entries[0].FileHash == archive.Entries[0].FileHash);
                        }
                        finally { File.WriteAllBytes(path, before); }
                    }
                }
                Check(file.Name + " input unchanged", hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file.FullName))));
            }
            MapCatalog map = MapCatalog.Build(MafiaEnvironment.CityFolder, SdsMeshLoader.EnsureExtracted);
            Check("map catalog discovers copied city archives", map.Areas.Count > 0);
            Check("LOD companions form one district", map.Areas.Count(a => a.BaseName == "bc_bayou_block_a") == 1);
            foreach (var file in files)
            {
                var manifest = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(file));
                if (!manifest.HasType("Generic")) continue;
                var scene = SdsMeshLoader.LoadHierarchy(file);
                Check(file.Name + " compiled scene loads", scene.Document is not null);
                Check(file.Name + " geometry is finite and bounded", scene.Meshes.All(m =>
                    m.Positions.All(v => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z))
                    && m.Indices.All(i => i < m.Positions.Length)
                    && m.Parts.All(p => p.StartIndex >= 0 && p.IndexCount <= m.Indices.Length - p.StartIndex)));
            }
            var district = SdsMeshLoader.LoadHierarchy(map.Areas.First(a => a.BaseName == "bc_bayou_block_a").Summer);
            Check("district renders placed geometry and terrain", district.Meshes.Count > 400 && district.Meshes.Sum(m => m.Indices.Length) > 100000
                && district.Meshes.Any(m => m.Name.StartsWith("terrain_a_geometry_island", StringComparison.Ordinal)));
            Check("district resolves texture bindings", district.Meshes.SelectMany(m => m.Parts).Any(p => p.DiffuseTexture is not null));
            string materialPath = Path.Combine(root, "edit", "materials", "default.mtl");
            var library = new Illusion.Formats.Materials.MaterialLibrary(Illusion.Formats.Materials.MaterialVersion.V_63);
            library.ReadMatFile(materialPath);
            Check("MTLB 63 reads every material", library.Materials.Count == 13653);
            Check("MTLB 63 byte-exact round trip", library.ToBytes().SequenceEqual(File.ReadAllBytes(materialPath)));
            TestEdits(Check);
            TestStructuralEdits(Check);
            TestPropImport(Check);
            TestCompiledAuthoring(Check);
            TestCompiledSceneCollections(Check);
            TestCompiledSceneCollectionGuards(Check);
            TestCompiledNativeLod(Check);
            TestCompiledNativeLodGpu(Check);
            TestLinkedProxyHostPreparation(Check);
            TestCompiledLinkedProxyAuthoring(Check);
            TestCompiledLinkedProxyMaterials(Check);
            TestCompiledLinkedProxyGeometry(Check);
            TestCompiledNavigationNetworkUi(Check);
            TestCompiledNavigationNetwork(Check);
            TestCompiledPooledProxyGeometry(Check);
            TestCompiledProxyGeometryBatch(Check);
            TestLinkedProxyGeometryBatchHost(Check);
            TestCompiledLinkedProxyReplacement(Check);
            TestCompiledLinkedProxyStructure(Check);
            TestPreparedProxyRigStructure(Check);
            TestPreparedProxyRigHost(Check);
            TestPreparedProxyRigCollision(Check);
            TestPreparedProxyRigCollisionHost(Check);
            TestPreparedProxyRigGlass(Check);
            TestPreparedProxyRigGlassHost(Check);
            TestPreparedProxySkin(Check);
            TestPreparedProxySkinHost(Check);
            TestCompiledLinkedProxyHighMaterials(Check);
            TestLinkedProxyGeometryHost(Check);
            TestLinkedProxyStructureHost(Check);
            TestCompiledNativeLodBounds(Check);
            TestCompiledNativeLodRendering(Check);
            TestNativeInstanceLod(Check);
            TestNativePlacementRest(Check);
            TestNativeVegetationWind(Check);
            TestNativeVegetationWindBounds(Check);
            TestNativeVegetationWindScene(Check);
            TestNativeInstanceUpdates(Check);
            TestNativePaintTriangle(Check);
            TestNativePaintSpacing(Check);
            TestNativePaintClipping(Check);
            TestNativePaintTransforms(Check);
            TestNativePaintCells(Check);
            TestNativePaintFields(Check);
            TestNativePaintHost(Check);
            TestCompiledLinkedProxyBreadth(Check);
            TestVehicleVisualMaterials(Check);
            TestCompiledNavigation(Check);
            TestCompiledNavigationCook(Check);
            TestCompiledNavigationTopology(Check);
            TestCompiledNavigationSubdivision(Check);
            TestCompiledNavigationExtrusion(Check);
            TestCompiledCollisionRemoval(Check);
            TestCompiledCollisionAttachments(Check);
            TestCompiledTriangleCollision(Check);
            TestCompiledWorldCollision(Check);
            TestNativePhysicsOracle(Check);
            TestCompiledEffects(Check);
            TestCompiledWaterWaves(Check);
            TestCompiledWaterWavePreview(Check);
            TestCompiledWaterGeometryAuthoring(Check);
            TestNativeWaterSurface(Check);
            TestNativeWaterOptics(Check);
            TestNativeWaterFoam(Check);
            TestNativeWaterFoamContext(Check);
            TestNativeWaterFoamSurface(Check);
            TestNativeWaterFoamViewport(Check);
            TestNativeRainRipple(Check);
            TestNativeWaterContact(Check);
            TestNativeWaterContactViewport(Check);
            TestNativeWaterContactJointViewport(Check);
            TestMipmappedWaterTextures(Check);
            TestCompiledSceneAnimation(Check);
            TestNativeAnimationAuthoring(Check);
            TestSkinnedPreviewBounds(Check);
            TestCompiledAnimationPreviewUi(Check);
            TestCompiledMorphPreview(Check);
            TestCompiledMorphAnimation(Check);
            TestCompiledStaticEntityCreation(Check);
            TestCompiledVegetationCreation(Check);
            TestCompiledFrameWrapperCreation(Check);
            TestCompiledStockFrameKit(Check);
            TestCompiledVehicleTemplateCreation(Check);
            TestCompiledVehicleMapImport(Check);

        }
        catch (Exception ex)
        {
            failures++;
            report.AppendLine("[FAIL] " + ex);
        }
        if (ready)
        {
            try { TestCopiedResourceRestoration(Check); }
            catch (Exception ex) { failures++; report.AppendLine("[FAIL] final working-resource audit: " + ex); }
        }
        report.AppendLine($"Failures: {failures}");
        File.WriteAllText(output, report.ToString());
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private static void TestStructuralEdits(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_bayou_block_a_lod0.sds"));
        var document = new Illusion.Assets.Compiled.CompiledSceneDocument(archive);
        var snapshot = document.Graph.Resources.ToDictionary(r => r.Path, r => r.ToArray());
        static IEnumerable<SdsFrameNode> Walk(IEnumerable<SdsFrameNode> roots)
        {
            foreach (var node in roots) { yield return node; foreach (var child in Walk(node.Children)) yield return child; }
        }
        try
        {
            Illusion.Domain.IFrameDuplication? duplicate = null;
            int before = document.ObjectCount;
            foreach (var node in Walk(document.Roots))
                if ((node.Mesh is not null || node.LodMeshes.Count > 0)
                    && node.Source is Illusion.Domain.IFrameNode frame && document.CanDuplicate(frame)
                    && document.Duplicate(frame, out _) is { } made) { duplicate = made; break; }
            check("compiled static leaf duplication succeeds", duplicate is not null);
            if (duplicate is null) return;
            check("duplication adds one canonical object", document.ObjectCount == before + 1);
            var copy = (Illusion.Domain.IFrameNode)duplicate.Root.Source!;
            var matrix = copy.LocalTransform; matrix.M41 += 2f; copy.LocalTransform = matrix;
            document.SaveWorkingCopy();
            var reloaded = new Illusion.Assets.Compiled.CompiledSceneDocument(archive);
            check("duplicated object survives save/reload", reloaded.ObjectCount == before + 1
                && Walk(reloaded.Roots).Any(n => n.Name == duplicate.Root.Name));
            duplicate.Detach(); document.SaveWorkingCopy();
            check("duplicate undo restores all compiled bytes", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
            duplicate.Reattach();
            check("duplicate redo restores canonical object", document.ObjectCount == before + 1);
            duplicate.Detach(); document.SaveWorkingCopy();
            var candidates = document.Roots.Select(n => n.Source).OfType<Illusion.Domain.IFrameNode>();
            foreach (var candidate in candidates)
                if (document.CaptureDetachment([candidate]) is { } deletion)
                {
                    deletion.Detach(); document.SaveWorkingCopy();
                    check("compiled deletion survives reload", new Illusion.Assets.Compiled.CompiledSceneDocument(archive).ObjectCount < before);
                    deletion.Reattach(); document.SaveWorkingCopy();
                    check("deletion undo restores all compiled bytes", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
                    break;
                }
        }
        finally { foreach (var (path, bytes) in snapshot) File.WriteAllBytes(path, bytes); }
    }

    private static void TestPropImport(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_bayou_block_a_lod0.sds"));
        var stock = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city_crash", "bc_city_crash.sds"));
        if (!stock.Exists) return;
        SdsMeshLoader.EnsureExtracted(stock);
        var entry = Illusion.Assets.Compiled.CompiledPropCatalog.Load(stock)
            .FirstOrDefault(p => p.Name == "bc_01_patio_dining_chair_a");
        check("stock compiled prop catalog contains chair", entry is not null);
        if (entry is null) return;
        var document = new Illusion.Assets.Compiled.CompiledSceneDocument(archive);
        static IEnumerable<SdsFrameNode> Walk(IEnumerable<SdsFrameNode> roots)
        {
            foreach (var node in roots) { yield return node; foreach (var child in Walk(node.Children)) yield return child; }
        }
        var parent = Walk(document.Roots).Select(n => n.Source).OfType<Illusion.Assets.Compiled.CompiledSceneDocument.CompiledFrameNode>()
            .First(f => f.Name == "train_car_a_v1_bv01_00");
        var snapshot = document.Graph.Resources.ToDictionary(r => r.Path, r => File.ReadAllBytes(r.Path));
        try
        {
            var imported = document.ImportFrame(entry.SourceResourcePath, entry.SourceObjectId, parent, out string? refusal);
            check("compiled prop import succeeds: " + refusal, imported is not null);
            if (imported is null) return;
            foreach (var resource in document.Graph.Resources)
                snapshot.TryAdd(resource.Path, File.ReadAllBytes(resource.Path));
            var frame = (Illusion.Assets.Compiled.CompiledSceneDocument.CompiledFrameNode)imported.Root.Source!;
            string name = frame.Name;
            var matrix = frame.LocalTransform; matrix.M41 += 1.25f; frame.LocalTransform = matrix;
            document.SaveWorkingCopy();
            var loaded = new Illusion.Assets.Compiled.CompiledSceneDocument(archive);
            var copy = Walk(loaded.Roots).Select(n => n.Source).OfType<Illusion.Assets.Compiled.CompiledSceneDocument.CompiledFrameNode>()
                .FirstOrDefault(f => f.Name == name);
            check("imported prop preserves parent and transform after reload", copy is not null
                && ReferenceEquals(copy.Parent, Walk(loaded.Roots).First(n => n.Name == parent.Name).Source)
                && Math.Abs(copy.LocalTransform.M41 - matrix.M41) < 1e-5);
            check("imported prop renders after reload", loaded.Meshes.Any(m => m.Name == name && m.TriangleCount > 0));
            imported.Detach(); document.SaveWorkingCopy();
            check("compiled prop undo restores every loaded resource byte", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
            imported.Reattach(); document.SaveWorkingCopy();
            check("compiled prop redo survives reload", Walk(new Illusion.Assets.Compiled.CompiledSceneDocument(archive).Roots)
                .Any(n => n.Name == name));
            imported.Detach(); document.SaveWorkingCopy();
            check("compiled prop second undo restores every byte", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
        }
        finally { foreach (var (path, bytes) in snapshot) File.WriteAllBytes(path, bytes); }
    }

    private static void TestEdits(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "cars", "bruno_ravine_sheriff.sds"));
        var document = new Illusion.Assets.Compiled.CompiledSceneDocument(archive);
        var snapshot = document.Graph.Resources.ToDictionary(r => r.Path, r => r.ToArray());
        static IEnumerable<SdsFrameNode> Walk(IEnumerable<SdsFrameNode> roots)
        {
            foreach (var node in roots) { yield return node; foreach (var child in Walk(node.Children)) yield return child; }
        }
        var nodes = Walk(document.Roots).ToList();
        var collision = document.CollisionMeshes();
        check("compiled vehicle collision shapes decode", collision.Count == 11
            && collision.All(m => m.Indices.All(index => index < m.Positions.Length)));
        foreach (var property in nodes.Select(n => n.Source).OfType<Illusion.Assets.Compiled.CompiledVehicleProperties>()
                     .SelectMany(p => p.GetPropertyGroups()).SelectMany(g => g.Properties).Where(p => p.Kind == Illusion.Domain.Properties.PropertyKind.Float))
        {
            object? value = property.Get();
            property.Set!((float)value! + 0.25f);
            check("native vehicle tuning edit " + property.Id, Equals(property.Get(), (float)value + 0.25f));
            property.Set(value);
            check("native vehicle tuning byte-exact undo " + property.Id, document.Graph.Resources.All(r => r.ToArray().SequenceEqual(snapshot[r.Path])));
        }
        check("vehicle has eight detachable parts", new[] { "bumperB", "bumperF", "coverF", "coverBL", "coverBR", "doorFL", "doorFR", "exhaust" }
            .All(name => nodes.Any(n => n.Name == name && n.Source is Illusion.Domain.IFrameNode)));
        check("vehicle has four assembled wheels", nodes.Count(n => n.Kind == "Vehicle wheel") == 4);
        check("vehicle parts partition the original triangles", document.Meshes.Where(m => !m.Name.StartsWith("Wheel ", StringComparison.Ordinal)).Sum(m => m.TriangleCount) == 23778);
        check("packed position handedness is not height", document.Meshes.All(m => m.Indices.All(i => Math.Abs(System.Numerics.Vector3.Transform(m.Positions[i], m.World).Z) < 4)));
        var sedan = new Illusion.Assets.Compiled.CompiledSceneDocument(new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "cars", "smith_mainline.sds")));
        check("standard vehicle resolves axle wheel models", Walk(sedan.Roots).Count(n => n.Kind == "Vehicle wheel") == 4);
        check("vehicle components expose axle and joint parameters", nodes.Select(n => n.Source).OfType<Illusion.Domain.Properties.IPropertySource>()
            .SelectMany(p => p.GetPropertyGroups()).SelectMany(g => g.Properties).Any(p => p.Id.Contains("m_BrakeDrumMass", StringComparison.Ordinal)));
        var door = (Illusion.Domain.IFrameNode)nodes.First(n => n.Name == "doorFL").Source!;
        var original = door.LocalTransform;
        var changed = original;
        changed.M41 += 1.25f;
        try
        {
            door.LocalTransform = changed;
            document.SaveWorkingCopy();
            var reopened = new Illusion.Assets.Compiled.CompiledSceneDocument(archive);
            var reloadedDoor = (Illusion.Domain.IFrameNode)Walk(reopened.Roots).First(n => n.Name == "doorFL").Source!;
            check("vehicle transform survives save and reopen", Math.Abs(reloadedDoor.LocalTransform.M41 - changed.M41) < 1e-5);
            var beforeMesh = document.Meshes.First(m => m.Name == "doorFL");
            var afterMesh = reopened.Meshes.First(m => m.Name == "doorFL");
            int vertex = (int)beforeMesh.Indices[0];
            var before = System.Numerics.Vector3.Transform(beforeMesh.Positions[vertex], beforeMesh.World);
            var after = System.Numerics.Vector3.Transform(afterMesh.Positions[vertex], afterMesh.World);
            check("saved component edit moves rendered geometry", Math.Abs(after.X - before.X - 1.25f) < 1e-4);
            var packed = SdsArchive.Pack(MafiaEnvironment.ExtractedDir(archive), MafiaEnvironment.Profile);
            using var bytes = new MemoryStream();
            packed.Save(bytes, new SdsWriteOptions()); bytes.Position = 0;
            check("edited scene builds as version 20", SdsArchive.Load(bytes).Version == 20);
            door.LocalTransform = original;
            document.SaveWorkingCopy();
            check("transform undo restores every byte", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
            var source = nodes.Select(n => n.Source).OfType<Illusion.Domain.Properties.IPropertySource>()
                .SelectMany(p => p.GetPropertyGroups()).SelectMany(g => g.Properties)
                .First(p => p.Id.EndsWith("m_BrakeDrumMass", StringComparison.Ordinal) && p.Set is not null);
            object? oldValue = source.Get();
            source.Set!((float)oldValue! + 2f);
            document.SaveWorkingCopy();
            var updated = new Illusion.Assets.Compiled.CompiledSceneDocument(archive);
            check("vehicle component scalar survives save", Walk(updated.Roots).Select(n => n.Source).OfType<Illusion.Domain.Properties.IPropertySource>()
                .SelectMany(p => p.GetPropertyGroups()).SelectMany(g => g.Properties)
                .Any(p => p.Id == source.Id && Equals(p.Get(), (float)oldValue! + 2f)));
            source.Set(oldValue);
            document.SaveWorkingCopy();
            check("component undo restores every byte", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
        }
        finally { foreach (var (path, bytes) in snapshot) File.WriteAllBytes(path, bytes); }
    }
}
