using System.Numerics;
using System.Security.Cryptography;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledInlineMorphGpu(Action<string, bool> check)
    {
        var fixture = ReadInlineMorphProbe(); if (fixture is null) return;
        var (document, binding, sibling) = fixture.Value;
        var original = binding.DecodeLod(0)!; var cap = sibling.DecodeLod(0)!;
        var hashes = document.Graph.Resources.ToDictionary(resource => resource, resource => SHA256.HashData(resource.ToArray()));
        using var gpu = new GpuContext(); using var renderer = new SceneRenderer(gpu) { Mode = RenderMode.Solid, ShowSky = false };
        using var target = new SharedRenderTarget(gpu, 192, 192);
        var body = renderer.AddMesh(original); var capGpu = renderer.AddMesh(cap);
        var center = (body.BoundsMin + body.BoundsMax) * .5f;
        renderer.Camera.Orthographic = true; renderer.Camera.OrthoHeight = 1.7f;
        renderer.Camera.LookAt(center + Vector3.Normalize(new Vector3(2, -3, 1)) * 3, center);
        byte[] Draw() { renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
        var initial = Draw();
        var deformed = document.PreviewMorph(binding, 0, new Dictionary<int, float> { [0] = 1, [1] = .7f, [2] = .3f, [3] = .5f });
        using (var preview = renderer.CreateMeshGpu(deformed))
        using (body.OverrideVertexGeometry(preview))
        {
            var moved = Draw();
            check("native inline barrel atlas changes actual uploaded body pixels beside its untouched cap", !moved.SequenceEqual(initial)
                && capGpu.Visible && renderer.DrawnMeshes >= 2);
        }
        check("native inline morph GPU lease restores exact authored pixels and opaque source bytes", Draw().SequenceEqual(initial)
            && hashes.All(pair => SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value)));
    }
}
