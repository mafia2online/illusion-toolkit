using System.IO;
using System.Buffers.Binary;
using Illusion.Formats.Compiled;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Domain.Materials;

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

}
