using System.Numerics;
using System.IO;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativeInstanceLod(Action<string, bool> check)
    {
        var table = new NativeMeshLod([100, 400, 900], [true, true, true], [false, false, false]);
        int At(float x) => table.Select(new(x, 0, 0), Vector3.Zero, Vector3.Zero);
        check("native mesh LOD integer distance thresholds match original query boundaries", At(0) == 0 && At(9.999f) == 0 && At(10) == 1 && At(20) == 2 && At(30) == -1);
        var empty = new NativeMeshLod([100, 400, 900], [true, true, true], [false, true, false]);
        check("native desired-empty level suppresses geometry rather than substituting another mesh", empty.Select(new(15, 0, 0), Vector3.Zero, Vector3.Zero) == -1 && empty.Select(new(25, 0, 0), Vector3.Zero, Vector3.Zero) == 2);
        var missing = new NativeMeshLod([100, 400, 900], [true, false, true], [false, false, false]);
        check("native unavailable mesh level is omitted from packed LOD index", missing.Select(new(15, 0, 0), Vector3.Zero, Vector3.Zero) == 2);
        check("native mesh LOD uses closest world AABB point and explicit scale/bias", table.Select(new(15, 0, 0), new(10, -1, -1), new(20, 1, 1)) == 0
            && table.Select(new(15, 0, 0), Vector3.Zero, Vector3.Zero, 2) == 2 && table.Select(Vector3.Zero, Vector3.Zero, Vector3.Zero, 1, 100) == 1);
        MeshData EqualTemplate(float uv = 0, SurfaceMaterial? surface = null, NativeLodMembership? membership = null) => new()
        {
            Positions = [Vector3.Zero], Normals = [Vector3.UnitZ], UVs = [new(uv, 0)], Indices = [0, 0, 0],
            Parts = [new(0, 3, null, materialHash: 123, surface: surface)], NativeLod = membership
        };
        check("native template interning compares full decoded values beyond storage identity", CompiledSceneDocument.NativePlacementTemplatesEqual([EqualTemplate()], [EqualTemplate()]));
        check("native template interning keeps differing UV channels separate", !CompiledSceneDocument.NativePlacementTemplatesEqual([EqualTemplate()], [EqualTemplate(.125f)]));
        var surface = new SurfaceMaterial(1, null, null, null, null, null, null, null, null, Vector2.One, Vector2.One, Vector2.One, Vector2.One, Vector2.One);
        check("native template interning keeps material surface roles separate", !CompiledSceneDocument.NativePlacementTemplatesEqual([EqualTemplate()], [EqualTemplate(surface: surface)]));
        var membershipGraph = new NativeLodGraph([new(1, -1, [], Vector3.Zero, Vector3.One, 0, false)]);
        check("native template interning keeps incompatible scene LOD membership separate", !CompiledSceneDocument.NativePlacementTemplatesEqual([EqualTemplate()], [EqualTemplate(membership: new(membershipGraph, 0))]));
        check("native template interning compares mesh LOD thresholds and desired-empty markers by value", table.Equals(new NativeMeshLod([100, 400, 900], [true, true, true], [false, false, false])) && !table.Equals(empty));
        using var gpu = new GpuContext(); using var renderer = new SceneRenderer(gpu) { ShowSky = false };
        using var target = new SharedRenderTarget(gpu, 96, 96);
        var transforms = new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(3, 0, 0), Matrix4x4.CreateTranslation(0, 0, -50) };
        for (int level = 0; level < 3; level++)
            renderer.AddMesh(new MeshData
            {
                Name = "native per-instance LOD " + level, World = Matrix4x4.Identity, Instances = transforms,
                InstanceLod = new(table, level, new(-1, -1, 0), new(1, 1, 0)),
                Positions = [new(-1 + level * .2f, -1, 0), new(1 - level * .2f, -1, 0), new(0, 1 - level * .2f, 0)], Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                Indices = [0, 1, 2], Parts = [new(0, 3, null, tint: level == 0 ? new(0, 1, 0, 1) : level == 1 ? new(1, 0, 0, 1) : new(0, 0, 1, 1))]
            });
        renderer.Camera.Orthographic = true; renderer.Camera.OrthoHeight = 8;
        byte[] Draw(float distance)
        {
            renderer.Camera.LookAt(new(0, 0, distance), Vector3.Zero); renderer.Render(target); return RenderTargetReadback.Read(gpu, target);
        }
        byte[] near = Draw(5);
        check("native instanced GPU draws two near copies once each and culls third beyond authored range", renderer.DrawnInstances == 2 && renderer.DrawnMeshes == 1);
        byte[] far = Draw(15);
        check("native instanced GPU switches selected level per copy and changes pixels", renderer.DrawnInstances == 2 && renderer.DrawnMeshes == 1 && !near.SequenceEqual(far));
        check("native instanced GPU rewind restores exact image", Draw(5).SequenceEqual(near));
        Draw(35); check("native instanced GPU excludes all copies past last mesh threshold", renderer.DrawnInstances == 0);
        renderer.Clear(); check("native cloud GPU clear releases all meshes", renderer.Meshes.Count == 0);
        int[] order = new int[3];
        var original = new[] { Matrix4x4.CreateTranslation(250, 0, 0), Matrix4x4.CreateTranslation(-250, 0, 0), Matrix4x4.Identity };
        var sorted = InstanceChunks.Build(original, Vector3.Zero, Vector3.One, sortedIndices: order);
        check("native instance sort mapping preserves exact parallel activation-origin identity", order.SequenceEqual(new[] { 1, 2, 0 })
            && Enumerable.Range(0, 3).All(i => sorted.Sorted[i] == original[order[i]]));
        renderer.AddMesh(new MeshData
        {
            Name = "native root activation distance", World = Matrix4x4.Identity, Instances = [Matrix4x4.Identity], InstanceDrawDistances = [10],
            InstanceDrawOrigins = [new(100, 0, 0)], InstanceLod = new(table, 0, new(-1, -1, 0), new(1, 1, 0)),
            Positions = [new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Indices = [0, 1, 2], Parts = [new(0, 3, null)]
        });
        Draw(5);
        check("native activation range uses root center independently of offset child visual bounds", renderer.DrawnInstances == 0);
        renderer.Clear();
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_general.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        var clouds = document.Meshes.Where(mesh => mesh.Name == "weed_17_1m_00").ToArray();
        check("actual General root activates explicit native placements with internal LOD metadata", document.NativeTranslocatorResourceIds.Contains(0x40D56B1CCCC04E21UL)
            && document.NativeTranslocatorPlacementCount == 3421 && document.NativeTranslocatorRenderedPlacements > 3000
            && clouds.Length == 2 && clouds.All(mesh => mesh.InstanceLod is not null && mesh.Instances?.Length == 1315));
        check("native placement catalogue resolves regional General and the matching global archive once", CompiledNativePlacementCatalog.ForNativePlacements(
            new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_block_b_lod0.sds"))).Select(file => file.Name.ToLowerInvariant())
            .SequenceEqual(new[] { "bc_bayou_general.sds", "bc_city_crash.sds" }));
        foreach (var mesh in clouds) renderer.AddMesh(mesh);
        Vector3 focus = clouds[0].Instances![0].Translation; renderer.Camera.OrthoHeight = 12;
        byte[] Actual(float height)
        {
            renderer.Camera.LookAt(focus + new Vector3(0, 0, height), focus); renderer.Render(target);
            return RenderTargetReadback.Read(gpu, target);
        }
        byte[] actualNear = Actual(2), actualFar = Actual(60);
        check("actual native weed placements change LOD pixels and rewind exactly", !actualNear.SequenceEqual(actualFar) && Actual(2).SequenceEqual(actualNear));
        check("native placement decode and GPU rendering preserve every compiled working resource", document.Graph.Resources.All(resource => !resource.IsDirty
            && resource.ToArray().SequenceEqual(File.ReadAllBytes(resource.Path))));

    }
}
