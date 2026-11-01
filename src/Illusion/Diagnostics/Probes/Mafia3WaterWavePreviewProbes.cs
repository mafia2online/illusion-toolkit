using System.IO;
using System.Numerics;
using System.Windows;
using System.Xml.Linq;
using Illusion.Assets;
using Illusion.Formats.Compiled;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Views;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledWaterWavePreview(Action<string, bool> check)
    {
        string originalConfig = Path.Combine(MafiaEnvironment.GameRoot, "edit", "config", "render_config.xml");
        string originalLookup = Path.Combine(MafiaEnvironment.GameRoot, "edit", "maps", "engine", "sincos.dds");
        if (!File.Exists(originalConfig) || !File.Exists(originalLookup)) return;
        byte[] configBytes = File.ReadAllBytes(originalConfig), lookupBytes = File.ReadAllBytes(originalLookup);
        string fixture = Path.GetFullPath(Path.Combine(".scratch", "water-preview-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(fixture);
        string config = Path.Combine(fixture, "render_config.xml"), lookupPath = Path.Combine(fixture, "sincos.dds");
        string imageA = Path.Combine(fixture, "before.png"), imageB = Path.Combine(fixture, "after.png"), imageC = Path.Combine(fixture, "rewind.png");
        File.WriteAllBytes(config, configBytes); File.WriteAllBytes(lookupPath, lookupBytes);
        var app = Application.Current; var shutdown = app?.ShutdownMode; if (app is not null) app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        WaterWavePreviewWindow? window = null;
        try
        {
            var waves = CompiledWaterWaves.LoadConfiguration(config); var lookup = CompiledWaterWaveLookup.Load(lookupPath); var packed = waves.PackConstants();
            using (var gpu = new GpuContext())
            using (var renderer = new WaterWavePreview(gpu, lookup.Mips))
            using (var target = new SharedRenderTarget(gpu, 96, 64))
            {
                byte[] Render(float time, Vector4 weights, string mode = "normals", Vector2? center = null, float scale = 1)
                { renderer.Render(target, packed, time, weights, center ?? Vector2.Zero, 16, mode, scale); return RenderTargetReadback.Read(gpu, target); }
                var flat = Render(0, Vector4.Zero);
                check("analytical water zero weights produce native flat Z-up normals", Enumerable.Range(0, flat.Length / 4).All(i => flat[i * 4] == 255 && Math.Abs(flat[i * 4 + 1] - 128) <= 1 && Math.Abs(flat[i * 4 + 2] - 128) <= 1 && flat[i * 4 + 3] == 255));
                var zero = Render(0, Vector4.Zero, "displacement");
                check("analytical water zero displacement has signed neutral colors", Enumerable.Range(0, zero.Length / 4).All(i => Math.Abs(zero[i * 4] - 128) <= 1 && Math.Abs(zero[i * 4 + 1] - 128) <= 1 && Math.Abs(zero[i * 4 + 2] - 128) <= 1));
                var native = Render(0, new(1, 0, 0, 0));
                check("analytical water actual copied lookup produces spatial normals", !native.SequenceEqual(flat) && Enumerable.Range(0, native.Length / 4).Select(i => BitConverter.ToUInt32(native, i * 4)).Distinct().Count() > 4);
                check("analytical water native seconds advance the GPU field", !Render(.37f, new(1, 0, 0, 0)).SequenceEqual(native));
                check("analytical water deterministic GPU rewind", Render(0, new(1, 0, 0, 0)).SequenceEqual(native));
                check("analytical water world XY changes the sampled native phases", !Render(0, new(1, 0, 0, 0), center: new(.37f, -.29f)).SequenceEqual(native));
                check("analytical water RGBA weights remain unnormalized", !Render(0, new(1, 1, 0, 0), "displacement", scale: 8).SequenceEqual(Render(0, new(.5f, .5f, 0, 0), "displacement", scale: 8)));
            }
            window = new(config, lookupPath);
            window.Configure(1.25f, new(1, 1, .25f, 0), new(-10, 42), 32, "displacement", 2); window.EnsureReady();
            check("analytical water window commits explicit unnormalized controls without activation", window.Time == 1.25f && window.Weights == new Vector4(1, 1, .25f, 0) && window.Center == new Vector2(-10, 42) && window.Span == 32 && window.Mode == "displacement" && window.DisplacementScale == 2 && !window.IsVisible);
            window.SaveFrame(imageA); check("analytical water window exports actual offscreen GPU PNG", File.ReadAllBytes(imageA).AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            window.Configure(2.5f, new(1, 1, .25f, 0), new(-10, 42), 32, "displacement", 2); window.SaveFrame(imageB);
            check("analytical water window time changes its GPU image", !File.ReadAllBytes(imageA).SequenceEqual(File.ReadAllBytes(imageB)));
            window.Configure(1.25f, new(1, 1, .25f, 0), new(-10, 42), 32, "displacement", 2); window.SaveFrame(imageC);
            check("analytical water window rewind restores exact PNG", File.ReadAllBytes(imageA).SequenceEqual(File.ReadAllBytes(imageC)));
            bool invalid = false; try { window.Configure(float.NaN); } catch (ArgumentException) { invalid = true; }
            check("invalid analytical water controls preserve the current valid view", invalid && window.Time == 1.25f && window.Mode == "displacement");
            var edited = XDocument.Load(config); var amplitude = edited.Descendants("m_Amplitude").First();
            amplitude.Value = (float.Parse(amplitude.Value, System.Globalization.CultureInfo.InvariantCulture) + .5f).ToString("R", System.Globalization.CultureInfo.InvariantCulture); edited.Save(config);
            byte[] editedBytes = File.ReadAllBytes(config);
            window.Configure(1.25f, new(1, 1, .25f, 0), new(-10, 42), 32, "displacement", 2); window.SaveFrame(imageB);
            check("analytical water reads current copied XML without authoring it", File.ReadAllBytes(config).SequenceEqual(editedBytes) && !File.ReadAllBytes(imageA).SequenceEqual(File.ReadAllBytes(imageB)));
            File.WriteAllText(config, "<invalid");
            typeof(WaterWavePreviewWindow).GetMethod("ApplyInputs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
            check("malformed copied water XML is a safe UI refusal preserving the valid view", window.Time == 1.25f && window.Mode == "displacement" && !string.IsNullOrWhiteSpace(window.Description));
            File.WriteAllBytes(config, configBytes);
            window.Close(); bool closed = false; try { window.EnsureReady(); } catch (InvalidOperationException) { closed = true; }
            check("closed analytical water preview refuses GPU reuse", closed);
            check("analytical water input XML and DDS remain byte-exact", File.ReadAllBytes(originalConfig).SequenceEqual(configBytes) && File.ReadAllBytes(originalLookup).SequenceEqual(lookupBytes) && File.ReadAllBytes(lookupPath).SequenceEqual(lookupBytes));
        }
        finally
        {
            window?.Close(); if (app is not null && shutdown is { } prior) app.ShutdownMode = prior;
            foreach (string path in new[] { config, lookupPath, imageA, imageB, imageC }) if (File.Exists(path)) File.Delete(path);
            if (!Directory.EnumerateFileSystemEntries(fixture).Any()) Directory.Delete(fixture);
        }
    }
}
