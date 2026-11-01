using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativeVegetationWindScene(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_general.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        using var gpu = new GpuContext(); using var renderer = new SceneRenderer(gpu) { ShowSky = false, Mode = RenderMode.MaterialPreview };
        using var target = new SharedRenderTarget(gpu, 256, 256);
        foreach (string name in new[] { "leaves_11_4m", "weed_17_1m_00" })
        {
            renderer.Clear(); var source = document.Meshes.First(m => m.Name == name && m.Lod == 0);
            var mesh = new MeshData
            {
                Name = source.Name, World = Matrix4x4.Identity, Positions = source.Positions, Normals = source.Normals, UVs = source.UVs,
                Tangents = source.Tangents, Binormals = source.Binormals, Colors = source.Colors, SecondaryColors = source.SecondaryColors,
                NativeWindGeometryBind = source.NativeWindGeometryBind, NativeWindObjectOrigin = source.NativeWindObjectOrigin,
                Indices = source.Indices, Parts = source.Parts, Instances = [source.Instances![0]]
            };
            var uploaded = renderer.AddMesh(mesh); var origin = mesh.Instances![0].Translation;
            renderer.Camera.LookAt(origin + new Vector3(4, -7, 4), origin + new Vector3(0, 0, 1.5f));
            void Set(bool enabled, float time) => renderer.ConfigureNativeVegetationWind(enabled, time, new(1, 0, 0, 15), new(2000, 2000, 0), new(.001f, 0));
            byte[] Render() { renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
            Set(false, 0); var before = Render(); int drawCalls = renderer.DrawCalls;
            Set(true, 0); var first = Render();
            check(name + ": actual native object and COLOR1 are wind eligible", renderer.NativeVegetationWindEligibleMeshes == 1 && renderer.NativeVegetationWindDrawnParts > 0);
            Set(true, 1.25f); var later = Render();
            int changed = Enumerable.Range(0, 256 * 256).Count(i => !first.AsSpan(i * 4, 4).SequenceEqual(later.AsSpan(i * 4, 4)));
            check(name + ": clock follows authored native amplitudes", name.Contains("weed", StringComparison.Ordinal) ? changed == 0 : changed > 5);
            Set(true, 0);
            check(name + ": exact native wind GPU rewind", first.SequenceEqual(Render()));
            renderer.SetGhostFocus([uploaded]); var suspended = Render();
            check(name + ": bridge rest authoring suspends wind and restores static pixels", renderer.NativeVegetationWindBridgeSuspended && before.SequenceEqual(suspended));
            renderer.SetGhostFocus(null); Set(false, 0);
            check(name + ": disabled native wind restores exact pixels and draw counts", before.SequenceEqual(Render()) && renderer.DrawCalls == drawCalls);
        }
        check("native vegetation preview preserves every copied compiled working resource", document.Graph.Resources.All(resource => !resource.IsDirty
            && resource.ToArray().SequenceEqual(File.ReadAllBytes(resource.Path))));
    }
}
