using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledSceneCollections(Action<string, bool> check)
    {
        foreach (var (name, roots, meshes, waterNames) in new[]
        {
            ("bc_city_univers", 461, 109, new[] { "bc00_water_main", "bc00_water_pools", "bc00_water_fountains", "bc00_water_pools_dlc" }),
            ("si_city_univers", 246, 253, new[] { "dlc2_si_city20_water_main", "dlc2_si_loc_crash_site_water_surfaces", "dlc2_si_city20_water_ground" })
        })
        {
            var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city_univers", name + ".sds"));
            if (!archive.Exists) continue;
            var document = new CompiledSceneDocument(archive);
            var ids = document.Roots.Select(row => row.Source).OfType<ICompiledMeshSource>().Select(source => source.SceneIdentity).ToArray();
            check($"{name}: actual native SceneCollection roots expose placed water geometry once",
                document.Roots.Count == roots && document.Meshes.Count == meshes && ids.Distinct().Count() == ids.Length
                && waterNames.All(water => document.Roots.Count(row => row.Name == water) == 1));
            var templates = document.Graph.Nodes.Where(node => node.TypeId == 0x8121a443).SelectMany(Descendants)
                .Where(node => node.TypeId == 0x315fbb48).SelectMany(Descendants).Where(node => node.TypeId == 0x4403597e)
                .Select(node => $"guid:{node.ObjectId:X16}").ToHashSet();
            check($"{name}: nested model registries remain templates and resources remain clean",
                !ids.Any(templates.Contains) && document.Graph.Resources.All(resource => !resource.IsDirty));
        }
        foreach (var (relative, roots, meshes, bakedIds) in new[]
        {
            ("city/bc_bayou_block_a_lod0.sds", 458, 510, new ulong[] { 0x6072DDA1AE804E37, 0x6072DDA1AE804E3F, 0x6072DDA1AE804E47, 0x6072DDA1AE804E4F, 0x6072DDA1AE804E57 }),
            ("city/bc_french_east_block_b_lod0.sds", 121, 305, new ulong[] { 0x5B403B2A66404E33, 0x5B403B2A66404E3B, 0x5B403B2A66404E43, 0x5B403B2A66404E4B })
        })
        {
            var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, relative));
            if (!archive.Exists) continue;
            var document = new CompiledSceneDocument(archive);
            var placed = new HashSet<ulong>();
            var sceneFolder = Path.GetDirectoryName(document.Graph.Owner(document.Graph.Objects[bakedIds[0]]).Path);
            foreach (var resource in document.Graph.Resources.Where(resource => Path.GetDirectoryName(resource.Path) == sceneFolder))
                foreach (var root in resource.Roots.Where(root => root.Event == 2))
                    if (document.Graph.Resolve(document.Graph.Field(root, "m_Object")) is { TypeId: 0x8fa874c4 } collection)
                        foreach (var item in document.Graph.Items(document.Graph.Field(collection, "m_Objects")))
                            if (document.Graph.Resolve(item) is { TypeId: 0x4403597e } scene) placed.Add(scene.ObjectId);
            var rootIds = document.Roots.Select(row => row.Source).OfType<ICompiledMeshSource>().Select(source => source.SceneIdentity).ToArray();
            check($"{archive.Name}: coarse LOD companion scenes have native ownership but are not activated by the primary LOD0 document",
                document.Roots.Count == roots && document.Meshes.Count == meshes && rootIds.Distinct().Count() == rootIds.Length
                && bakedIds.All(id => placed.Contains(id) && !rootIds.Contains($"guid:{id:X16}")));
            var overlaps = document.Meshes.GroupBy(mesh =>
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(MemoryMarshal.AsBytes(mesh.Positions.AsSpan())); hash.AppendData(MemoryMarshal.AsBytes(mesh.Indices.AsSpan()));
                var world = new[] { mesh.World }; hash.AppendData(MemoryMarshal.AsBytes(world.AsSpan()));
                return Convert.ToHexString(hash.GetHashAndReset());
            }).Where(group => group.Count() > 1 && group.Any(mesh => mesh.Name.StartsWith("built_", StringComparison.Ordinal))).ToArray();
            check($"{archive.Name}: primary LOD0 geometry contains no duplicate baked proxy placement", overlaps.Length == 0);
        }
        var car = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "cars", "bruno_ravine_sheriff.sds"));
        if (car.Exists)
        {
            var document = new CompiledSceneDocument(car);
            check("standalone native vehicle model authoring remains unchanged by scene collection activation",
                document.Roots.Count == 1 && document.Meshes.Count == 25 && document.ObjectCount == 80);
        }
        static IEnumerable<CompiledElement> Descendants(CompiledElement root)
        {
            yield return root;
            foreach (var child in root.Children)
                foreach (var node in Descendants(child)) yield return node;
        }
    }
}
