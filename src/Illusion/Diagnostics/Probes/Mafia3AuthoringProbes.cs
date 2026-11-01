using System.IO;
using System.Buffers.Binary;
using Illusion.Formats.Compiled;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Domain.Materials;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledAuthoring(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_block_a_lod0.sds"));
        if (!archive.Exists) throw new FileNotFoundException("The copied Bayou fixture is required.", archive.FullName);
        var document = new CompiledSceneDocument(archive);
        var snapshot = document.Graph.Resources.ToDictionary(resource => resource.Path, resource => File.ReadAllBytes(resource.Path));
        try
        {
            int count = document.CollisionMeshes().Count;
            var group = document.CreateGroup("probe_physical_group", null, out var refusal)
                ?? throw new InvalidOperationException(refusal);
            var frame = (IFrameNode)group.Root.Source!;
            var volume = document.AddCollision(frame, "box", new Vector3(.25f, .5f, .75f),
                Matrix4x4.CreateTranslation(1, 2, 3), null, 103, out refusal)
                ?? throw new InvalidOperationException(refusal);
            foreach (var resource in document.Graph.Resources)
                snapshot.TryAdd(resource.Path, File.ReadAllBytes(resource.Path));
            check("authored collision appears", document.CollisionMeshes().Count == count + 1);
            var source = (IFrameNode)volume.Root.Source!;
            var mesh = document.CollisionMesh(source)!;
            check("collision overlay and editing share world matrix", mesh.World == source.WorldTransform);
            check("authored box retains local half extents", mesh.Positions.Aggregate(Vector3.Max) == new Vector3(.25f, .5f, .75f));
            document.SaveWorkingCopy();
            check("authored collision survives working-copy reload", new CompiledSceneDocument(archive).CollisionMeshes().Count == count + 1);
            volume.Detach(); group.Detach(); document.SaveWorkingCopy();
            check("authored collision undo restores every resource", snapshot.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)));
            group.Reattach(); volume.Reattach(); document.SaveWorkingCopy();
            check("authored collision redo survives reload", new CompiledSceneDocument(archive).CollisionMeshes().Count == count + 1);
            volume.Detach(); group.Detach(); document.SaveWorkingCopy();
            check("authored collision redo then undo restores bytes", snapshot.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)));
        }
        finally
        {
            foreach (var (path, bytes) in snapshot) File.WriteAllBytes(path, bytes);
        }
    }
    private static void TestVehicleVisualMaterials(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "cars", "bruno_ravine_sheriff.sds"));
        var document = new CompiledSceneDocument(archive);
        static IEnumerable<Illusion.Assets.Sds.SdsFrameNode> Walk(IEnumerable<Illusion.Assets.Sds.SdsFrameNode> rows)
        { foreach (var row in rows) { yield return row; foreach (var child in Walk(row.Children)) yield return child; } }
        ISceneSource Source(CompiledSceneDocument scene) => Walk(scene.Roots).Where(row => row.Name == "Root")
            .Select(row => row.GeometrySource ?? row.Source).First(source => source is ICompiledMeshSource)!;
        var source = Source(document);
        var snapshot = document.Graph.Resources.ToDictionary(resource => resource.Path, resource => File.ReadAllBytes(resource.Path));
        try
        {
            var graph = new CompiledGraph(document.Graph.Resources);
            bool Indexed() => graph.Nodes.GroupBy(n => n.TypeId).All(group => group.SequenceEqual(graph.NodesOfType(group.Key)))
                && graph.NodesOfType(0xDEADBEEF).Count == 0;
            check("compiled type index matches canonical constructor nodes", Indexed());
            var entity = graph.NodesOfType(0xDEB11995).Single();
            var resource = graph.Owner(entity);
            int entityCount = graph.NodesOfType(entity.TypeId).Count;
            var clone = resource.CloneSubtree(entity, new Dictionary<ulong, ulong>
                { [entity.ObjectId] = entity.ObjectId ^ 0x1000000000000000 });
            graph.RegisterSubtree(resource, clone); graph.RegisterSubtree(resource, clone);
            check("compiled type index registers subtree once", Indexed() && graph.NodesOfType(entity.TypeId).Count == entityCount + 1);
            graph.SetSubtreeActive(resource, clone, false);
            check("compiled type index removes inactive subtree", Indexed() && graph.NodesOfType(entity.TypeId).Count == entityCount
                && !graph.Objects.ContainsKey(clone.ObjectId));
            graph.SetSubtreeActive(resource, clone, true);
            check("compiled type index reactivates canonical identity", Indexed() && graph.NodesOfType(entity.TypeId).Contains(clone)
                && graph.Objects.GetValueOrDefault(clone.ObjectId) == clone);
            graph.SetSubtreeActive(resource, clone, false);
            var original = CompiledResource.FromBytes(resource.ToArray());
            var remap = original.Elements.Where(n => n.Event == 2 && n.ObjectId != 0).DistinctBy(n => n.ObjectId)
                .ToDictionary(n => n.ObjectId, n => n.ObjectId ^ 0x8000000000000000);
            graph.AddResource(original.RemapIds(remap, "probe-type-index-independent-resource"));
            check("compiled type index includes added resource", Indexed());

            var canonical = document.Graph;
            var paintEntity = canonical.NodesOfType(0xDEB11995).Single();
            var prefab = canonical.Resolve(canonical.Field(paintEntity, "m_BuiltPrefabsCPtr"));
            var paintRow = canonical.Items(canonical.Field(canonical.Field(prefab, "m_SShaderEffectInit"), "m_ColorAndDirty")).First();
            var flag = canonical.Field(paintRow, "m_Flags")!;
            var material = canonical.Integer(canonical.Field(paintRow, "m_Guid")!);
            var flagsBefore = canonical.Data(flag).ToArray();
            var flagsAfter = flagsBefore.ToArray();
            uint oldFlags = checked((uint)canonical.Integer(flag));
            BinaryPrimitives.WriteUInt32LittleEndian(flagsAfter, oldFlags ^ 1u);
            var flagResource = canonical.Owner(flag);
            var serializedBefore = flagResource.ToArray();
            try
            {
                flagResource.Replace(flag, flagsAfter);
                check("vehicle paint index reads fresh canonical flags", document.VehiclePaintMaterials[material] == (oldFlags ^ 1u));
            }
            finally { flagResource.Replace(flag, flagsBefore); }
            check("vehicle paint flag undo restores canonical value and bytes", document.VehiclePaintMaterials[material] == oldFlags
                && flagResource.ToArray().AsSpan().SequenceEqual(serializedBefore));

            TestVehicleDirtGpu(document, check);

            var isolation = document.IsolateVisualGeometry(((ICompiledMeshSource)source).GeometryBinding!.Visual, out var refusal)
                ?? throw new InvalidOperationException(refusal);
            var slots = (IMaterialSlotEditor)source;
            var before = slots.GetSlotTable().Select(table => (ulong[])table.Clone()).ToArray();
            check("isolated skinned vehicle exposes material slots", before.Length == 3 && before[0].Length > 1);
            ulong replacement = before[0][1];
            check("skinned visual material assignment succeeds", slots.SetSlotMaterial(0, replacement));
            document.SaveWorkingCopy();
            var reloaded = (IMaterialSlotEditor)Source(new CompiledSceneDocument(archive));
            check("skinned visual material survives reload", reloaded.GetSlotMaterial(0) == replacement);
            slots.SetSlotTable(before); isolation.Undo(); document.SaveWorkingCopy();
            check("skinned material and isolation undo restore every resource", snapshot.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)));
        }
        finally { foreach (var (path, bytes) in snapshot) File.WriteAllBytes(path, bytes); }
    }

    private static void TestVehicleDirtGpu(CompiledSceneDocument document, Action<string, bool> check)
    {
        var source = document.Meshes.First(m => m.Parts.Any(p => p.Surface?.VehicleDirt is not null));
        using var gpu = new GpuContext();
        using var renderer = new SceneRenderer(gpu) { ShowSky = false, Mode = RenderMode.MaterialPreview };
        using var target = new SharedRenderTarget(gpu, 256, 192);
        renderer.Textures.AddFolder(Path.GetDirectoryName(document.Graph.Resources[0].Path)!);
        renderer.Camera.LookAt(new Vector3(-5, 6, 3), new Vector3(0, 0, 1));
        var uploaded = renderer.AddMesh(source);
        var surfaces = uploaded.Parts.Select(p => p.Surface).ToArray();
        byte[] Render() { renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
        var clean = Render();
        check("native vehicle decal has its independent UV2 GPU stream", source.DecalUVs is not null
            && !source.DecalUVs.SequenceEqual(source.UVs!) && uploaded.HasDecalUvs);
        int decals = 0;
        for (int i = 0; i < uploaded.Parts.Count; i++)
        {
            var part = uploaded.Parts[i];
            if (part.Surface is not { Layers: < 0, VehicleDecal: not null } surface) continue;
            part.Surface = surface with { VehicleDecal = null }; uploaded.Parts[i] = part; decals++;
        }
        var withoutDecals = Render();
        int decalPixels = Enumerable.Range(0, 256 * 192).Count(i => !clean.AsSpan(i * 4, 3).SequenceEqual(withoutDecals.AsSpan(i * 4, 3)));
        check("native vehicle T018 decal changes actual body pixels", decals > 0 && decalPixels > 100);
        for (int i = 0; i < uploaded.Parts.Count; i++)
        { var part = uploaded.Parts[i]; part.Surface = surfaces[i]; uploaded.Parts[i] = part; }
        check("native vehicle decal GPU undo is byte exact", Render().AsSpan().SequenceEqual(clean));
        int affected = 0;
        for (int i = 0; i < uploaded.Parts.Count; i++)
        {
            var part = uploaded.Parts[i];
            if (part.Surface is not { Layers: < 0, VehicleDirt: { } dirt } surface) continue;
            part.Surface = surface with { VehicleDirt = dirt with { Amount = new Vector2(1, .7f) } };
            uploaded.Parts[i] = part; affected++;
        }
        var changed = Render();
        int pixels = Enumerable.Range(0, 256 * 192).Count(i => !clean.AsSpan(i * 4, 3).SequenceEqual(changed.AsSpan(i * 4, 3)));
        check("native vehicle T045 dirt controls change actual body pixels", affected > 0 && pixels > 100);
        check("native vehicle dirt preserves unrelated surface slots", uploaded.Parts.Select((p, i) =>
            surfaces[i]?.VehicleDirt is not null || Equals(p.Surface, surfaces[i])).All(same => same));
        for (int i = 0; i < uploaded.Parts.Count; i++)
        { var part = uploaded.Parts[i]; part.Surface = surfaces[i]; uploaded.Parts[i] = part; }
        check("native vehicle dirt GPU undo is byte exact", Render().AsSpan().SequenceEqual(clean));
        renderer.Mode = RenderMode.Render;
        int normals = 0;
        for (int i = 0; i < uploaded.Parts.Count; i++)
        {
            var part = uploaded.Parts[i];
            if (part.Surface is not { Layers: < 0, VehicleDirt: { Normal: not null } dirt } surface) continue;
            part.Surface = surface with { VehicleDirt = dirt with { Amount = new Vector2(.8f, .3f) } };
            uploaded.Parts[i] = part; normals++;
        }
        var withNormals = Render();
        var normalSurfaces = uploaded.Parts.Select(p => p.Surface).ToArray();
        for (int i = 0; i < uploaded.Parts.Count; i++)
        {
            var part = uploaded.Parts[i];
            if (part.Surface is not { VehicleDirt: { } dirt } surface) continue;
            part.Surface = surface with { VehicleDirt = dirt with { Normal = null } }; uploaded.Parts[i] = part;
        }
        var withoutNormals = Render();
        int normalPixels = Enumerable.Range(0, 256 * 192).Count(i => !withNormals.AsSpan(i * 4, 3).SequenceEqual(withoutNormals.AsSpan(i * 4, 3)));
        check("native vehicle T019 detail normals affect actual lit body pixels", normals > 0 && normalPixels > 20);
        for (int i = 0; i < uploaded.Parts.Count; i++)
        { var part = uploaded.Parts[i]; part.Surface = normalSurfaces[i]; uploaded.Parts[i] = part; }
        check("native vehicle detail-normal GPU undo is byte exact", Render().AsSpan().SequenceEqual(withNormals));

    }

    private static void TestVehiclePaintScopes(CompiledSceneDocument document,
        CompiledSceneDocument.CompiledFrameNode first, CompiledSceneDocument.CompiledFrameNode second, Action<string, bool> check)
    {
        const ulong material = 0x5407517966BA7DEC;
        var graph = document.Graph;
        CompiledElement Prefab(IFrameNode frame)
        {
            for (var current = frame; current is not null; current = current.Parent)
                if (current is CompiledSceneDocument.CompiledFrameNode owner)
                {
                    var element = graph.Objects[Convert.ToUInt64(owner.SceneIdentity[5..], 16)];
                    foreach (var entity in graph.Items(graph.Field(element, "m_Components")).Select(graph.Resolve)
                        .OfType<CompiledElement>().Where(n => n.TypeId == 0xDEB11995))
                    {
                        var prefab = graph.Resolve(graph.Field(entity, "m_BuiltPrefabsCPtr"));
                        if (graph.Field(graph.Field(prefab, "m_SShaderEffectInit"), "m_ColorAndDirty") is not null) return prefab!;
                    }
                }
            throw new InvalidDataException("No owning vehicle paint prefab.");
        }
        var prefabA = Prefab(first); var prefabB = Prefab(second);
        var firstMesh = first.GeometryBinding!.DecodeLod(0)!; var secondMesh = second.GeometryBinding!.DecodeLod(0)!;
        var fields = graph.Items(graph.Field(graph.Field(prefabA, "m_SShaderEffectInit"), "m_ColorAndDirty"))
            .Select(row => graph.Field(row, "m_Flags")!).ToArray();
        var snapshots = fields.ToDictionary(field => field, field => graph.Data(field).ToArray());
        bool cachedDonor = graph.NodesOfType(0xDEB11995).Select(e => graph.Resolve(graph.Field(e, "m_BuiltPrefabsCPtr")))
            .OfType<CompiledElement>().Where(p => !ReferenceEquals(p, prefabA) && !ReferenceEquals(p, prefabB))
            .Any(p => graph.Items(graph.Field(graph.Field(p, "m_SShaderEffectInit"), "m_ColorAndDirty"))
                .Any(row => graph.Field(row, "m_Guid") is { } id && graph.Integer(id) == material
                    && graph.Field(row, "m_Flags") is { } flags && (graph.Integer(flags) & 1) != 0));
        check("two Car10 paint test includes a cached registered donor", cachedDonor && prefabA.ObjectId != prefabB.ObjectId);
        try
        {
            foreach (var field in fields) graph.Owner(field).Replace(field, new byte[4]);
            check("two Car10 paint registrations stay scoped despite shared material hashes",
                document.VehiclePaintMaterialsFor(first).GetValueOrDefault(material) == 0
                && (document.VehiclePaintMaterialsFor(second).GetValueOrDefault(material) & 1) != 0
                && (document.VehiclePaintMaterials.GetValueOrDefault(material) & 1) != 0);
            document.ApplyVehiclePaint(firstMesh); document.ApplyVehiclePaint(secondMesh);
            check("dropping one Car10 paint registration changes only its live visual",
                firstMesh.Parts.All(p => p.Surface is not { Layers: < 0 }) && secondMesh.Parts.Any(p => p.Surface is { Layers: < 0 }));
            var materialPart = firstMesh.Parts.First(p => p.MaterialHash == material);
            var refreshA = document.ApplyVehiclePaint(new MeshData { Parts = [materialPart], Colors = [Vector4.One] }, first).Parts[0];
            var refreshB = document.ApplyVehiclePaint(new MeshData { Parts = [materialPart], Colors = [Vector4.One] }, second).Parts[0];
            check("Car10 material refresh resolves the visual owner rather than global registrations",
                refreshA.Surface is not { Layers: < 0 } && refreshB.Surface is { Layers: < 0 });
            check("fresh Car10 decodes exclude other cars and cached donor paint flags",
                first.GeometryBinding.DecodeLod(0)!.Parts.All(p => p.Surface is not { Layers: < 0 })
                && second.GeometryBinding.DecodeLod(0)!.Parts.Any(p => p.Surface is { Layers: < 0 }));
        }
        finally { foreach (var (field, bytes) in snapshots) graph.Owner(field).Replace(field, bytes); }
        document.ApplyVehiclePaint(firstMesh);
        check("Car10 paint registration undo restores native paint effects", firstMesh.Parts.Any(p => p.Surface is { Layers: < 0 })
            && snapshots.All(p => graph.Data(p.Key).SequenceEqual(p.Value)));
    }

}
