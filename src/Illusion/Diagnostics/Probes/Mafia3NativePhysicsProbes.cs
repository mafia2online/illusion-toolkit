using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestNativePhysicsOracle(Action<string, bool> check)
    {
        string? executable = Environment.GetEnvironmentVariable("ILLUSION_MAFIA3_COOK_EXE");
        string? fixtures = Environment.GetEnvironmentVariable("ILLUSION_MAFIA3_PHYSICS_FIXTURES");
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(fixtures) || !File.Exists(executable) || !Directory.Exists(fixtures)) return;
        Task.Run(async () =>
        {
            async Task Validate(byte[] packet, string label, bool strict, int? variant = null)
            {
                string helper = Path.Combine(AppContext.BaseDirectory, "Illusion.exe");
                if (!File.Exists(helper)) helper = Path.Combine(AppContext.BaseDirectory, "Illusion.NativeCookHost.exe");
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo(helper)
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    },
                };
                if (Path.GetFileName(helper).Equals("Illusion.exe", StringComparison.OrdinalIgnoreCase)) process.StartInfo.ArgumentList.Add("--native-nav-cook");
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
                { ExecutablePath = executable, Resource = packet, Operation = "physics_validate", Variant = variant, StrictBounds = strict }));
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(true); throw new InvalidOperationException("Native physics oracle timed out."); }
                using var response = JsonDocument.Parse(await output);
                if (process.ExitCode != 0 || !response.RootElement.GetProperty("Success").GetBoolean())
                    throw new InvalidDataException(label + ": " + response.RootElement.GetProperty("Error").GetString() + await error);
                var report = response.RootElement.GetProperty("Physics");
                check(label + " original PC binary triangle/Codec3Axis exact", report.GetProperty("MaximumVertexError").GetSingle() == 0 &&
                    report.GetProperty("MaximumBoundsError").GetSingle() == 0 &&
                    report.GetProperty("ExactVertexComponents").GetInt32() == report.GetProperty("Triangles").GetInt32() * 9);
                if (strict) check(label + " emitted native BVHs strictly contain native triangles", report.GetProperty("UncontainedNodes").GetInt32() == 0 && report.GetProperty("NonzeroBounds").GetInt32() > 0);
            }
            Vector3[] outline = [new(0, 0, 0), new(2, 0, 0), new(2, 1, 0), new(1, 1, 0), new(1, 2, 0), new(0, 2, 0)];
            uint[] indices = [0, 1, 3, 1, 2, 3, 0, 3, 5, 3, 4, 5];
            byte[]? template = null;
            foreach (string path in Directory.GetFiles(fixtures, "mesh-*.hkx").Order())
            {
                byte[] original = File.ReadAllBytes(path); template ??= original;
                await Validate(original, Path.GetFileName(path) + " stock", false);
                var meshes = HavokMeshData.Read(original);
                for (int variant = 0; variant < meshes.Count; variant++)
                {
                    if (meshes[variant] is null) continue;
                    byte[] cooked = HavokMeshTopologyWriter.Replace(original, variant, outline, indices);
                    await Validate(cooked, Path.GetFileName(path) + " new concave " + variant, true, variant);
                    byte[] refitted = HavokMeshWriter.Transform(original, variant,
                        Matrix4x4.CreateScale(1.3f, .7f, 1.2f) * Matrix4x4.CreateRotationZ(.7f) * Matrix4x4.CreateTranslation(314, -200, 77));
                    await Validate(refitted, Path.GetFileName(path) + " fixed refit " + variant, true, variant);
                }
                check(Path.GetFileName(path) + " native oracle preserves source", File.ReadAllBytes(path).SequenceEqual(original));
            }
            if (template is null) return;
            var grid = Enumerable.Range(0, 101 * 101).Select(i => new Vector3(i % 101, i / 101, MathF.Sin(i % 101 * .1f) * MathF.Cos(i / 101 * .2f))).ToArray();
            var order = new List<uint>();
            for (int y = 0; y < 100; y++) for (int x = 0; x < 100; x++)
                { uint a = (uint)(y * 101 + x), b = a + 1, c = a + 101, d = c + 1; order.AddRange([a, b, d, a, d, c]); }
            await Validate(HavokMeshTopologyWriter.Replace(template, 0, grid, order), "20000 connected native triangles", true, 0);
            var bankPositions = Enumerable.Range(0, 33333).SelectMany(i => new[] { new Vector3(i * 2, 0, 0), new Vector3(i * 2 + 1, 0, 0), new Vector3(i * 2, 1, 0) }).ToArray();
            var bankIndices = Enumerable.Range(0, 3).SelectMany(_ => Enumerable.Range(0, 99999).Select(i => (uint)i)).ToArray();
            await Validate(HavokMeshTopologyWriter.Replace(template, 0, bankPositions, bankIndices), "99999 native triangles/shared banks", true, 0);
        }).GetAwaiter().GetResult();
    }
}
