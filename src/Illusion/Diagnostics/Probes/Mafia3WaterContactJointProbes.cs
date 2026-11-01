using System.IO;
using System.Numerics;
using System.Reflection;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Textures;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Textures;


namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestNativeWaterContactJointViewport(Action<string, bool> check)
    {
        string root = Path.GetFullPath(MafiaEnvironment.GameRoot ?? ".");
        if (!root.Contains(Path.DirectorySeparatorChar + ".scratch" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        string config = Path.Combine(root, "edit/config/render_config.xml"), lookup = Path.Combine(root, "edit/maps/engine/sincos.dds"), variance = Path.Combine(root, "edit/maps/engine/water_normal_variance.dds");
        if (!File.Exists(config) || !File.Exists(lookup) || !File.Exists(variance) || !File.Exists(MafiaEnvironment.CityUniversSds)) return;
        var document = new CompiledSceneDocument(new FileInfo(MafiaEnvironment.CityUniversSds));
        var definitions = document.Meshes.Where(m => m.Parts.Any(p => p.Surface?.NativeWaterDefinition is not null)).ToArray();
        if (definitions.Length == 0) return;
        var material = MafiaMaterials.GetMaterialInfo(0x1A227ADE53424B91, 0, 0);
        string? detail = TextureSearchIndex.FindPath(material.TextureSlots.FirstOrDefault(t => t.SlotId == "T000")?.TextureName);
        string? normal = TextureSearchIndex.FindPath(material.TextureSlots.FirstOrDefault(t => t.SlotId == "T001")?.TextureName);
        if (detail is null || normal is null) return;
        using var gpu = new GpuContext(); using var renderer = new SceneRenderer(gpu) { ShowSky = false, Mode = RenderMode.MaterialPreview };
        using var target = new SharedRenderTarget(gpu, 256, 144);
        renderer.Textures.SetFallbackResolver(TextureSearchIndex.FindPath);
        renderer.Textures.AddFolder(Path.Combine(MafiaEnvironment.ResourcesFolder!, "sds_retail", "city_univers", Path.GetFileName(MafiaEnvironment.CityUniversSds)));
        var meshes = definitions.Select(renderer.AddMesh).ToArray();
        renderer.Camera.LookAt(new(1777.0494f, -2800.3513f, 15.001f), new(1777.0494f, -2774.3513f, .001f)); renderer.Camera.Fov = MathF.PI / 2;
        renderer.ConfigureNativeWater(CompiledWaterWaves.LoadConfiguration(config).PackConstants(), CompiledWaterWaveLookup.Load(lookup).Mips);
        renderer.ConfigureNativeWaterFineNormals(File.ReadAllBytes(variance), .175f, 3);
        Vector3 center = new(1777.0494f, -2774.3513f, -.1f);
        renderer.AddMesh(new MeshData
        {
            Name = "known-submerged-foam-depth-plane", World = Matrix4x4.Identity,
            Positions = [center + new Vector3(-10, -10, 0), center + new Vector3(10, -10, 0), center + new Vector3(10, 10, 0), center + new Vector3(-10, 10, 0)],
            Normals = Enumerable.Repeat(Vector3.UnitZ, 4).ToArray(), Indices = [0, 1, 2, 0, 2, 3], Parts = [new MeshPart(0, 6, null)]
        });
        byte[] Draw() { renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
        int Leases() => meshes.Sum(m => ((List<List<TextureLease>>)typeof(GpuMesh).GetField("_partLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(m)!).Sum(p => p.Count));
        var resources = document.Graph.Resources.Select(r => r.ToArray()).ToArray();
        byte[] baseline = Draw(); int nativeLeases = Leases();
        var origin = new Vector2(center.X, center.Y);
        int[] texels = Enumerable.Range(244, 24).SelectMany(y => Enumerable.Range(244, 24).Select(x => y * 512 + x)).ToArray();
        float[] influences = Enumerable.Repeat(.7f, texels.Length).ToArray();
        renderer.SetNativeWaterContactPreview(origin, texels, influences, 12); Draw(); Draw(); Draw(); byte[] independent = Draw();
        check("joint contact missing foam inputs refuses without changing independent preview", renderer.SetNativeWaterContactPreview(origin, texels, influences, 12, jointFoam: true) is not null
            && !renderer.NativeWaterContactJointFoam && renderer.NativeWaterContactFrame == 12 && Draw().AsSpan().SequenceEqual(independent));
        renderer.ClearNativeWaterContact();
        var parameters = new NativeWaterFoamParameters(new(.12f, .7f, .001f), new(.7f, 1, .01f, .0001f), 6, new(.7f, .99f));
        bool configured = renderer.ConfigureNativeWaterFoam(File.ReadAllBytes(detail), File.ReadAllBytes(normal), parameters, new(.6f, .6f, .6f, .5f), new(.2f, .175f, .5f, 3));
        check("joint contact configuration is explicit and preserves disabled baseline", configured && !renderer.NativeWaterFoamEnabled && Draw().AsSpan().SequenceEqual(baseline));
        check("joint contact accepts configured native stages atomically", renderer.SetNativeWaterContactPreview(origin, texels, influences, 12, jointFoam: true) is null
            && renderer.NativeWaterContactJointFoam && renderer.NativeWaterFoamEnabled);
        Draw();
        check("joint contact and foam advance together within four-step budget", renderer.NativeWaterContactFrame == 4 && renderer.NativeWaterFoamFrame == 4
            && !renderer.NativeWaterContactPreviewReady && !renderer.NativeWaterContactConfigured);
        Draw(); Draw(); byte[] joint = Draw();
        check("joint contact and foam reach one native frame and publish final state", renderer.NativeWaterContactPreviewReady && renderer.NativeWaterFoamReady
            && renderer.NativeWaterContactFrame == 12 && renderer.NativeWaterFoamFrame == 12 && renderer.NativeWaterContactConfigured
            && !joint.AsSpan().SequenceEqual(independent));
        check("joint contact paused rendering is pixel exact", Draw().AsSpan().SequenceEqual(joint) && renderer.NativeWaterContactFrame == 12 && renderer.NativeWaterFoamFrame == 12);
        check("joint contact invalid replacement preserves native preview and clocks", renderer.SetNativeWaterContactPreview(origin, [1,1], [.5f,.5f], 12, jointFoam: true) is not null
            && renderer.NativeWaterContactFrame == 12 && renderer.NativeWaterFoamFrame == 12 && Draw().AsSpan().SequenceEqual(joint));
        check("joint contact refuses independent foam disable without partial changes", renderer.SetNativeWaterFoam(false, null, null, null, false) is not null
            && renderer.NativeWaterContactJointFoam && renderer.NativeWaterFoamEnabled && Draw().AsSpan().SequenceEqual(joint));
        renderer.SeekNativeWaterContactPreview(0, true); Draw();
        check("joint contact rewind resets both clocks coherently", renderer.NativeWaterContactFrame == 0 && renderer.NativeWaterFoamFrame == 0 && renderer.NativeWaterContactPreviewReady && !renderer.NativeWaterContactConfigured);
        renderer.SeekNativeWaterContactPreview(12, true); Draw(); Draw(); Draw();
        check("joint contact seeded reset replay is pixel exact", renderer.NativeWaterContactPreviewReady && Draw().AsSpan().SequenceEqual(joint));
        var originalPosition = renderer.Camera.Position; renderer.Camera.Position += new Vector3(2,0,0); Draw();
        check("joint contact camera changes restart BOTH native histories", renderer.NativeWaterContactFrame == 4 && renderer.NativeWaterFoamFrame == 4 && !renderer.NativeWaterContactPreviewReady);
        renderer.Camera.Position = originalPosition; Draw(); Draw(); Draw();
        check("joint contact original camera restores exact replay", renderer.NativeWaterContactPreviewReady && Draw().AsSpan().SequenceEqual(joint));
        renderer.NotifyNativeWaterFoamSourceChanged(); Draw();
        check("joint contact source invalidation restarts BOTH histories", renderer.NativeWaterContactFrame == 4 && renderer.NativeWaterFoamFrame == 4 && !renderer.NativeWaterContactPreviewReady);
        Draw(); Draw(); check("joint contact source replay restores exact paused pixels", Draw().AsSpan().SequenceEqual(joint));
        renderer.ClearNativeWaterContact();
        check("joint contact clear restores prior foam settings clock and baseline", !renderer.NativeWaterContactJointFoam && !renderer.NativeWaterFoamEnabled
            && renderer.NativeWaterTime == 0 && Draw().AsSpan().SequenceEqual(baseline));
        renderer.SetNativeWaterContactPreview(origin, texels, influences, 12); Draw(); Draw(); Draw();
        check("joint contact leaves independent snapshot mode unchanged", !renderer.NativeWaterContactJointFoam && Draw().AsSpan().SequenceEqual(independent));
        renderer.ClearNativeWaterContact();
        check("joint contact preview preserves every compiled payload", document.Graph.Resources.Select((r,i)=>r.ToArray().AsSpan().SequenceEqual(resources[i])).All(v=>v) && Leases() == nativeLeases);
    }
}
