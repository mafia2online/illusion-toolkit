using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativeWaterSurface(Action<string, bool> check)
    {
        string directory = Path.GetFullPath(Path.Combine(".scratch", "probe-native-water-surface"));
        Directory.CreateDirectory(directory);
        string Dds(string name, byte red, byte green, byte blue, byte alpha)
        {
            var bytes = new byte[132];
            void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
            U(0, 0x20534444); U(4, 124); U(8, 0x100f); U(12, 1); U(16, 1); U(20, 4); U(76, 32); U(80, 0x41); U(88, 32);
            U(92, 0xff0000); U(96, 0xff00); U(100, 0xff); U(104, 0xff000000); U(108, 0x1000);
            bytes[128] = blue; bytes[129] = green; bytes[130] = red; bytes[131] = alpha;
            string path = Path.Combine(directory, name + ".dds"); File.WriteAllBytes(path, bytes); return path;
        }
        string color = Dds("authored-color-data", 20, 130, 50, 0), mask = Dds("template-mask", 255, 0, 0, 0);
        var lookup = new byte[128];
        for (int i = 0; i < 64; i++)
        {
            float phase = 2 * MathF.PI * (i + .5f) / 64;
            lookup[i * 2] = (byte)((MathF.Sin(phase) * .5f + .5f) * 255);
            lookup[i * 2 + 1] = (byte)((MathF.Cos(phase) * .5f + .5f) * 255);
        }
        // Independently generated test waves/lookup; no retail data is embedded here.
        var packed = new float[128]; packed[4] = .5f; packed[8] = .15915493667125702f;
        packed[16] = -.15f; packed[20] = .3f; packed[28] = .075f;
        using var gpu = new GpuContext(); using var renderer = new SceneRenderer(gpu) { ShowSky = false, Mode = RenderMode.MaterialPreview };
        using var target = new SharedRenderTarget(gpu, 256, 144);
        renderer.Textures.AddFolder(directory);
        renderer.Camera.LookAt(new(0, -5, 4), Vector3.Zero);
        var surface = CompiledSurfaceMaterials.WaterDefinition with { NativeWaterDefinition = new(color, mask) };
        var mesh = new MeshData
        {
            Name = "synthetic-water-definition", World = Matrix4x4.Identity,
            Positions = [new(-4, -4, 0), new(4, -4, 0), new(4, 4, 0), new(-4, 4, 0)],
            Normals = Enumerable.Repeat(Vector3.UnitZ, 4).ToArray(), UVs = [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY],
            Tangents = Enumerable.Repeat(Vector3.UnitX, 4).ToArray(), Binormals = Enumerable.Repeat(Vector3.UnitY, 4).ToArray(),
            Indices = [0, 1, 2, 0, 2, 3], Parts = [new MeshPart(0, 6, color, surface: surface)]
        };
        var uploaded = renderer.AddMesh(mesh);
        renderer.ConfigureNativeWater(packed, new ReadOnlyMemory<byte>[] { lookup });
        byte[] Draw() { renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
        var first = Draw();
        check("native water three-stage surface accepts static definition geometry", renderer.NativeWaterActive && NativeWaterSurface.Supports(uploaded, uploaded.Parts[0]));
        check("native water definition alpha remains data rather than opacity", Enumerable.Range(0, target.Width * target.Height)
            .Count(i => first[i * 4 + 1] > first[i * 4] * 1.5 && first[i * 4 + 1] > first[i * 4 + 2] * 2) > 100);
        renderer.NativeWaterTime = .75f; var later = Draw();
        check("native water time changes the rendered analytical surface", !later.AsSpan().SequenceEqual(first));
        renderer.NativeWaterTime = 0;
        check("native water rewind restores exact GPU pixels", Draw().AsSpan().SequenceEqual(first));
        renderer.NativeWaterNormals = true;
        check("native water normal diagnostic differs from toolkit color shading", !Draw().AsSpan().SequenceEqual(first));
        renderer.NativeWaterNormals = false;
        var lighting = renderer.Lighting;
        var changedLighting = lighting; changedLighting.Gamma = new(.5f, .7f, .9f, 1);
        renderer.Lighting = changedLighting;
        check("native water respects shared output gamma controls", !Draw().AsSpan().SequenceEqual(first));
        changedLighting = lighting; changedLighting.SunColor = Vector4.Zero; changedLighting.AmbientUp = Vector4.Zero; changedLighting.AmbientDown = Vector4.Zero;
        renderer.Lighting = changedLighting;
        check("native water respects shared sun and hemisphere controls", !Draw().AsSpan().SequenceEqual(first));
        renderer.Lighting = lighting;
        check("native water lighting restoration is pixel exact", Draw().AsSpan().SequenceEqual(first));
        renderer.Mode = RenderMode.Render;
        check("native water full toolkit shading differs from simple preview", !Draw().AsSpan().SequenceEqual(first));
        renderer.Mode = RenderMode.MaterialPreview;
        uploaded.Ghost = true; check("native water leaves ghost definitions to ordinary rendering", !NativeWaterSurface.Supports(uploaded, uploaded.Parts[0])); uploaded.Ghost = false;
        uploaded.Instanced = true; check("native water refuses unverified instanced definitions", !NativeWaterSurface.Supports(uploaded, uploaded.Parts[0])); uploaded.Instanced = false;
        var part = uploaded.Parts[0]; var views = part.SurfaceSrvs!;
        var missing = views.ToArray(); missing[1] = default; part.SurfaceSrvs = missing; uploaded.Parts[0] = part;
        renderer.NativeWaterEnabled = false; var fallback = Draw(); renderer.NativeWaterEnabled = true; var missingMask = Draw();
        check("native water missing mask falls back without hiding geometry", !renderer.NativeWaterActive && fallback.AsSpan().SequenceEqual(missingMask));
        part.SurfaceSrvs = views; uploaded.Parts[0] = part;
        var blocker = new MeshData { Name = "opaque-water-depth-test", World = Matrix4x4.Identity,
            Positions = [new(-.5f, -.5f, 1), new(.5f, -.5f, 1), new(.5f, .5f, 1), new(-.5f, .5f, 1)],
            Normals = Enumerable.Repeat(Vector3.UnitZ, 4).ToArray(), UVs = [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY],
            Indices = [0, 1, 2, 0, 2, 3], Parts = [new MeshPart(0, 6, null, tint: new Vector4(1, 0, 0, 1))] };
        renderer.AddMesh(blocker); renderer.NativeWaterEnabled = false; var ordinary = Draw(); renderer.NativeWaterEnabled = true; var occluded = Draw();
        int red = 0, wrong = 0;
        for (int i = 0; i < ordinary.Length; i += 4)
            if (ordinary[i + 2] > ordinary[i + 1] * 2 && ordinary[i + 2] > ordinary[i] * 2)
            { red++; if (!ordinary.AsSpan(i, 3).SequenceEqual(occluded.AsSpan(i, 3))) wrong++; }
        check("native water shares the main opaque depth buffer", red > 100 && wrong == 0);
        renderer.Camera.Orthographic = true; Draw();
        check("native water orthographic view uses ordinary definition fallback", !renderer.NativeWaterActive);
        renderer.Camera.Orthographic = false; renderer.Mode = RenderMode.Wireframe; Draw();
        check("native water wireframe uses ordinary definition fallback", !renderer.NativeWaterActive);
    }
}
