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
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestNativeWaterFoamViewport(Action<string, bool> check)
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
        byte[] baseline = Draw(); int calls = renderer.DrawCalls, parts = renderer.NativeWaterPartsDrawn, leases = Leases();
        check("native foam viewport defaults OFF", !renderer.NativeWaterFoamEnabled && renderer.NativeWaterActive);
        var parameters = new NativeWaterFoamParameters(new(.12f, .7f, .001f), new(.7f, 1, .01f, .0001f), 6, new(.7f, .99f));
        bool configured = renderer.ConfigureNativeWaterFoam(File.ReadAllBytes(detail), File.ReadAllBytes(normal), parameters, new(.6f, .6f, .6f, .5f), new(.2f, .175f, .5f, 3));
        check("native foam configuration preserves disabled pixels/stats/leases", configured && Draw().AsSpan().SequenceEqual(baseline)
            && renderer.DrawCalls == calls && renderer.NativeWaterPartsDrawn == parts && Leases() == leases);
        check("native foam viewport accepts explicit seeded diagnostic", renderer.SetNativeWaterFoam(true, "diffuse", 24, 19, true) is null);
        bool bounded = true; byte[] diffuse = [];
        for (int draw = 0; draw < 6; draw++)
        {
            int before = renderer.NativeWaterFoamFrame; diffuse = Draw();
            bounded &= renderer.NativeWaterFoamFrame - before is >= 0 and <= 4;
            if (draw == 0) check("native foam viewport reports pending bounded seek", renderer.NativeWaterFoamFrame == 4 && !renderer.NativeWaterFoamReady);
        }
        check("native foam viewport reaches requested frame with at most four steps per draw", bounded && renderer.NativeWaterFoamReady && renderer.NativeWaterFoamFrame == 24);
        check("native foam viewport preserves definition-part and texture-lease counts", renderer.NativeWaterPartsDrawn == parts && Leases() == leases && renderer.Textures.RetiredCount == 0);
        check("native foam viewport consumes actual depth for visible authored diagnostic", !diffuse.AsSpan().SequenceEqual(baseline)
            && Enumerable.Range(0, diffuse.Length / 4).Any(i => diffuse[i * 4] > 100 && diffuse[i * 4 + 1] > 100 && diffuse[i * 4 + 2] > 100));
        renderer.SetNativeWaterFoam(null, "normals", null, null, false); byte[] normals = Draw();
        renderer.SetNativeWaterFoam(null, "roughness", null, null, false); byte[] roughness = Draw();
        check("native foam viewport exposes distinct material diagnostics", !normals.AsSpan().SequenceEqual(diffuse) && !roughness.AsSpan().SequenceEqual(normals));
        renderer.SetNativeWaterFoam(null, "diffuse", 24, 19, true); for (int i = 0; i < 6; i++) Draw();
        check("native foam viewport seeded reset/replay is pixel exact", Draw().AsSpan().SequenceEqual(diffuse));
        int actual = renderer.NativeWaterFoamFrame;
        check("native foam viewport refuses excessive seek without mutation", renderer.SetNativeWaterFoam(null, null, 36001, null, false) is not null
            && renderer.NativeWaterFoamFrame == actual && renderer.NativeWaterFoamTargetFrame == 24 && Draw().AsSpan().SequenceEqual(diffuse));
        renderer.SetNativeWaterFoam(null, null, 8, null, false); Draw();
        check("native foam viewport backward seek resets and remains bounded", renderer.NativeWaterFoamFrame == 4 && !renderer.NativeWaterFoamReady);
        Draw(); check("native foam viewport backward seek becomes ready at exact frame", renderer.NativeWaterFoamFrame == 8 && renderer.NativeWaterFoamReady);
        renderer.SetNativeWaterFoam(null, "diffuse", 24, 19, true); for (int i = 0; i < 6; i++) Draw();
        Matrix4x4 sourceWorld = meshes[0].World; meshes[0].World = sourceWorld * Matrix4x4.CreateTranslation(0, 0, .25f);
        Draw(); check("native foam viewport placement change resets bounded replay", renderer.NativeWaterFoamFrame == 4 && !renderer.NativeWaterFoamReady);
        meshes[0].World = sourceWorld; for (int i = 0; i < 6; i++) Draw();
        check("native foam viewport placement undo restores seeded pixels", renderer.NativeWaterFoamReady && Draw().AsSpan().SequenceEqual(diffuse));
        renderer.SetNativeWaterFineNormalParameters(.175f, 2); Draw();
        check("native foam viewport live variance change resets history", renderer.NativeWaterFoamFrame == 4 && !renderer.NativeWaterFoamReady);
        renderer.SetNativeWaterFineNormalParameters(.175f, 3); for (int i = 0; i < 6; i++) Draw();
        check("native foam viewport variance undo restores seeded pixels", renderer.NativeWaterFoamReady && Draw().AsSpan().SequenceEqual(diffuse));
        int revisionFrame = renderer.NativeWaterFoamFrame;
        for (int i = 0; i < 1000; i++) renderer.NotifyNativeWaterFoamSourceChanged();
        bool pending = !renderer.NativeWaterFoamReady && renderer.NativeWaterFoamFrame == revisionFrame;
        Draw(); check("native foam viewport coalesces source notifications without immediate GPU replay", pending && renderer.NativeWaterFoamFrame == 4);
        for (int i = 0; i < 5; i++) Draw();
        check("native foam viewport revision replay remains pixel exact", renderer.NativeWaterFoamReady && Draw().AsSpan().SequenceEqual(diffuse));
        var blocker = renderer.AddMesh(new MeshData
        {
            Name = "temporary-opaque-foam-source", World = Matrix4x4.Identity,
            Positions = [center + new Vector3(-5, -5, 4), center + new Vector3(5, -5, 4), center + new Vector3(5, 5, 4), center + new Vector3(-5, 5, 4)],
            Normals = Enumerable.Repeat(Vector3.UnitZ, 4).ToArray(), Indices = [0, 1, 2, 0, 2, 3], Parts = [new MeshPart(0, 6, null)]
        });
        pending = !renderer.NativeWaterFoamReady; Draw();
        check("native foam viewport opaque insertion invalidates captured depth", pending && renderer.NativeWaterFoamFrame == 4);
        for (int i = 0; i < 5; i++) Draw();
        var eyeNode = new Illusion.Scene.SceneNode("opaque eye", "Mesh", false) { Mesh = blocker };
        eyeNode.IsVisible = false;
        check("native foam actual scene-tree eye invalidates opaque depth immediately", !renderer.NativeWaterFoamReady);
        for (int i = 0; i < 6; i++) Draw();
        check("native foam scene-tree hide restores unobstructed seeded pixels", renderer.NativeWaterFoamReady && Draw().AsSpan().SequenceEqual(diffuse));
        eyeNode.IsVisible = true;
        check("native foam scene-tree show invalidates opaque depth", !renderer.NativeWaterFoamReady);
        for (int i = 0; i < 6; i++) Draw();
        renderer.UpdateInstances(blocker, [Matrix4x4.Identity]);
        for (int i = 0; i < 6; i++) Draw();
        eyeNode.IsVisible = false;
        check("native foam actual instanced eye invalidates captured depth", blocker.Instanced && !renderer.NativeWaterFoamReady);
        for (int i = 0; i < 6; i++) Draw();
        eyeNode.IsVisible = true;
        for (int i = 0; i < 6; i++) Draw();
        renderer.DetachMeshes([blocker]);
        for (int i = 0; i < 6; i++) Draw();
        eyeNode.IsVisible = false;
        check("native foam detached mesh visibility has no renderer callback", renderer.NativeWaterFoamReady);
        renderer.AttachMesh(blocker);
        for (int i = 0; i < 6; i++) Draw();
        eyeNode.IsVisible = true;
        check("native foam reattached eye restores lifetime-safe invalidation", !renderer.NativeWaterFoamReady);
        renderer.RemoveMeshes([blocker]); for (int i = 0; i < 6; i++) Draw();
        check("native foam viewport opaque removal recaptures and restores pixels", renderer.NativeWaterFoamReady && Draw().AsSpan().SequenceEqual(diffuse));
        renderer.SetNativeWaterFoam(false, null, null, null, false); renderer.NativeWaterTime = 0;
        check("native foam viewport OFF restores original pixels/stats/leases", Draw().AsSpan().SequenceEqual(baseline)
            && renderer.DrawCalls == calls && renderer.NativeWaterPartsDrawn == parts && Leases() == leases);
        check("native foam viewport never mutates compiled resources", document.Graph.Resources.Select((r, i) => r.ToArray().AsSpan().SequenceEqual(resources[i])).All(v => v));
        renderer.NativeWaterTime = 1000; int rejectedFrame = renderer.NativeWaterFoamFrame;
        check("native foam activation refuses excessive existing time atomically", renderer.SetNativeWaterFoam(true, null, null, null, false) is not null
            && !renderer.NativeWaterFoamEnabled && renderer.NativeWaterTime == 1000 && renderer.NativeWaterFoamFrame == rejectedFrame && renderer.NativeWaterFoamConfigured);
        renderer.NativeWaterTime = .4f;
        check("native foam activation without frame inherits the preview clock", renderer.SetNativeWaterFoam(true, "diffuse", null, null, false) is null
            && renderer.NativeWaterFoamTargetFrame == 24 && renderer.NativeWaterTime == .4f);
        for (int i = 0; i < 6; i++) Draw();
        object pass = typeof(SceneRenderer).GetField("_nativeWater", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer)!;
        object? Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner);
        var module = Field(pass, "_foam")!; var context = Field(pass, "_foamContext")!;
        nint Srv(string name) => (nint)((ComPtr<ID3D11ShaderResourceView>)Field(pass, name)!).Handle;
        nint Shader(string name) => (nint)((ComPtr<ID3D11PixelShader>)Field(module, name)!).Handle;
        var identities = (Detail: Srv("_foamDetail"), Normal: Srv("_foamNormal"), Generation: Shader("_generation"), Blur: Shader("_blur"), History: Shader("_history"));
        renderer.SetNativeWaterFoam(false, null, null, null, false); renderer.InvalidateNativeWaterFoamInputs();
        check("native foam OFF material edit invalidates cached configuration", !renderer.NativeWaterFoamConfigured
            && renderer.SetNativeWaterFoam(true, null, null, null, false) is not null && !renderer.NativeWaterFoamEnabled);
        byte[] detailBytes = File.ReadAllBytes(detail), normalBytes = File.ReadAllBytes(normal);
        var editedParameters = parameters with { Amplitude = 7 };
        renderer.ConfigureNativeWaterFoam(detailBytes, normalBytes, editedParameters, new(.2f, .6f, .6f, .5f), new(.2f, .175f, .5f, 3));
        renderer.SetNativeWaterFoam(true, "diffuse", 24, 19, false); for (int i = 0; i < 6; i++) Draw();
        check("native foam parameter refresh reuses programs/context/texture SRVs", ReferenceEquals(module, Field(pass, "_foam")) && ReferenceEquals(context, Field(pass, "_foamContext"))
            && identities == (Srv("_foamDetail"), Srv("_foamNormal"), Shader("_generation"), Shader("_blur"), Shader("_history"))
            && ((NativeWaterFoamParameters)Field(module, "_parameters")!) == editedParameters && Leases() == leases);
        renderer.ConfigureNativeWaterFoam(detailBytes, normalBytes, parameters, new(.6f, .6f, .6f, .5f), new(.2f, .175f, .5f, 3));
        for (int i = 0; i < 6; i++) Draw();
        check("native foam parameter undo restores seeded output without recompilation", Draw().AsSpan().SequenceEqual(diffuse) && ReferenceEquals(module, Field(pass, "_foam")));
        Vector3 eye = renderer.Camera.Position; renderer.Camera.Position += Vector3.UnitX * .125f;
        pending = !renderer.NativeWaterFoamReady; Draw();
        check("native foam paused camera change invalidates old depth immediately", pending && renderer.NativeWaterFoamFrame == 4 && !renderer.NativeWaterFoamReady);
        renderer.Camera.Position = eye; for (int i = 0; i < 6; i++) Draw();
        check("native foam camera return replays exact seeded output", renderer.NativeWaterFoamReady && Draw().AsSpan().SequenceEqual(diffuse));
        float aspect = renderer.Camera.AspectRatio;
        using (var capture = new SharedRenderTarget(gpu, 192, 192))
        {
            string? refusal = renderer.RenderForCapture(capture);
            check("native foam different-aspect capture reaches ready within bounded work", refusal is null && renderer.NativeWaterFoamReady && renderer.Camera.AspectRatio == 1);
            renderer.SetNativeWaterFoam(null, null, 36000, null, false);
            refusal = renderer.RenderForCapture(capture);
            check("native foam excessive capture refuses pending output within 32 steps", refusal?.Contains("pending", StringComparison.OrdinalIgnoreCase) == true && renderer.NativeWaterFoamFrame <= 56);
            int cancelledFrame = renderer.NativeWaterFoamFrame; bool cancelled = false;
            try { renderer.RenderForCapture(capture, new CancellationToken(canceled: true)); } catch (OperationCanceledException) { cancelled = true; }
            check("native foam capture cancellation precedes render work", cancelled && renderer.NativeWaterFoamFrame == cancelledFrame);
        }
        renderer.Camera.AspectRatio = aspect; renderer.NotifyNativeWaterFoamSourceChanged();
        renderer.SetNativeWaterFoam(null, null, 24, 19, true); for (int i = 0; i < 6; i++) Draw();
        check("native foam actual viewport projection is restored after capture", renderer.NativeWaterFoamReady && Draw().AsSpan().SequenceEqual(diffuse));
        renderer.SetNativeWaterFoam(false, null, null, null, false); renderer.NativeWaterTime = 0;
        check("native foam viewport invalid DDS refuses without white fallback", !renderer.ConfigureNativeWaterFoam([0], [0], parameters,
            new(.6f, .6f, .6f, .5f), new(.2f, .175f, .5f, 3)) && !renderer.NativeWaterFoamConfigured
            && renderer.SetNativeWaterFoam(true, null, null, null, false) is not null && Draw().AsSpan().SequenceEqual(baseline));
        renderer.ConfigureNativeWaterFoam(detailBytes, normalBytes, parameters, new(.6f, .6f, .6f, .5f), new(.2f, .175f, .5f, 3));
        renderer.SetNativeWaterFoam(true, null, 0, 19, true); Draw(); bool beforeClear = renderer.NativeWaterFoamReady;
        renderer.Clear();
        check("native foam renderer Clear invalidates public readiness immediately", beforeClear && !renderer.NativeWaterFoamReady && !renderer.NativeWaterActive && renderer.NativeWaterPartsDrawn == 0);
    }
}
