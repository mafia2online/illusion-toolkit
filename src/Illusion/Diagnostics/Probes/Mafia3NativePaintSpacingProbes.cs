using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Domain.Properties;
using Illusion.Formats.Compiled;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativePaintSpacing(Action<string, bool> check)
    {
        using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var random = new Random(8673); var bytes = new byte[48];
            for (int trial = 0; trial < 1000; trial++)
            {
                var p = new Vector3[3]; var uv = new Vector3[3];
                for (int i = 0; i < 3; i++)
                {
                    p[i] = new((float)random.NextDouble() * 5, (float)random.NextDouble() * 5, (float)random.NextDouble() * 3);
                    uv[i] = new((float)random.NextDouble(), (float)random.NextDouble(), 0);
                }
                var basis = CompiledPaintGridBasis.Create(p[0], p[1], p[2], CompiledPaintTriangle.FlatNormal(p[0], p[1], p[2]), uv[0], uv[1], uv[2]);
                float[] values = [basis.M11, basis.M12, basis.M13, basis.M14, basis.M21, basis.M22, basis.M23, basis.M24, basis.M31, basis.M32, basis.M33, basis.M34];
                for (int i = 0; i < 12; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
                digest.AppendData(bytes);
            }
            check("fixed-spacing quantized UV frame matches original1000-case native digest", Convert.ToHexString(digest.GetHashAndReset()) == "2E1C58419404A0F7B043C454AF02AEF3A3B1F41157D09C34865504BFB2DAB1FD");
        }
        using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var random = new Random(11871); var bytes = new byte[36]; int points = 0;
            for (int trial = 0; trial < 5000; trial++)
            {
                int level = trial % 3;
                var offset = new Vector3((float)random.NextDouble() * 200 - 100, (float)random.NextDouble() * 200 - 100, (float)random.NextDouble() * 20 - 10);
                var p = new Vector3[3]; var uv = new Vector3[3];
                for (int i = 0; i < 3; i++)
                {
                    p[i] = offset + new Vector3((float)random.NextDouble() * 4, (float)random.NextDouble() * 4, (float)random.NextDouble() * 2);
                    uv[i] = new((float)random.NextDouble(), (float)random.NextDouble(), 0);
                }
                var normal = CompiledPaintTriangle.FlatNormal(p[0], p[1], p[2]);
                float sx = .2f + (float)random.NextDouble() * .5f, sy = .2f + (float)random.NextDouble() * .5f;
                float dx = (float)random.NextDouble() * .3f, dy = (float)random.NextDouble() * .3f;
                uint seed = (uint)random.NextInt64(0, 0x100000000);
                var generated = CompiledPaintSpacing.Generate(p, normal, uv, sx, sy, dx, dy, seed, 3, level, _ => 255);
                BinaryPrimitives.WriteInt32LittleEndian(bytes, generated.Count); digest.AppendData(bytes.AsSpan(0, 4)); points += generated.Count;
                foreach (var point in generated)
                {
                    float[] values = [point.Position.X, point.Position.Y, point.Position.Z, point.Normal.X, point.Normal.Y, point.Normal.Z,
                        point.ImagePosition.X, point.ImagePosition.Y, point.ImagePosition.Z];
                    for (int i = 0; i < 9; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
                    digest.AppendData(bytes);
                }
            }
            check("fixed-spacing grid RNG/deviations/projection/UV match original5000-triangle17264-point native digest", points == 17264
                && Convert.ToHexString(digest.GetHashAndReset()) == "C7BB6A6215F50B677CFC9A4B69723EBFB506871929F32D7F3F68BE020C9E3D0C");
        }
        IReadOnlyList<CompiledPaintTriangle.Point> Generate(int level, byte density, int budget = 65535, CancellationToken token = default) =>
            CompiledPaintSpacing.Generate([Vector3.Zero, new(5, 0, 0), new(0, 2, 0)], Vector3.UnitZ,
                [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], .2f, .2f, 0, 0, 1, 3, level, _ => density, budget, token);
        check("native fixed-spacing additive tiers preserve original staggered point counts", new[] { Generate(0, 255).Count, Generate(1, 255).Count, Generate(2, 255).Count }.SequenceEqual(new[] { 47, 51, 48 }));
        check("fixed spacing consumes nonzero density rather than random-area byte probability", Generate(0, 1).SequenceEqual(Generate(0, 255)) && Generate(0, 0).Count == 0);
        bool bounded = false, cancelled = false;
        try { _ = Generate(0, 255, 1); } catch (NotSupportedException) { bounded = true; }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); try { _ = Generate(0, 255, token: cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        }
        check("fixed-spacing work honors candidate budget and cancellation before publication", bounded && cancelled);
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive); var snapshots = document.Graph.Resources.ToDictionary(resource => resource, resource => resource.ToArray());
        var originalField = document.NativePaintFields.Single(field => field.RootId == 0x17AB21AB80918124);
        var originalCell = originalField.GenerateCell(0, -22, -1, 0, 1);
        IEnumerable<SdsFrameNode> Walk(IEnumerable<SdsFrameNode> rows)
        { foreach (var row in rows) { yield return row; foreach (var child in Walk(row.Children)) yield return child; } }
        var sourceRow = Walk(document.Roots).Single(row => row.Name.EndsWith("[17AB21AB80918124]", StringComparison.Ordinal));
        var properties = ((IPropertySource)sourceRow.Source!).GetPropertyGroups().SelectMany(group => group.Properties).ToDictionary(property => property.Id);
        check("paint profile rows expose only native editable values and readonly shared ownership metadata", properties.Where(pair => pair.Key.StartsWith("Paint.", StringComparison.Ordinal)).All(pair => pair.Value.IsReadOnly && pair.Value.Set is null)
            && properties["m_SpacingEnabled"].Set is not null && (long)properties["Paint.Users"].Get()! > 1);
        var saved = properties.Values.Where(property => property.Set is not null).ToDictionary(property => property, property => property.Get());
        using var gpu = new GpuContext(); using var renderer = new SceneRenderer(gpu) { ShowSky = false };
        using var target = new SharedRenderTarget(gpu, 96, 96);
        var center = originalCell.Points.Aggregate(Vector3.Zero, (sum, point) => sum + point.Position) / originalCell.FullCount;
        renderer.Camera.Orthographic = true; renderer.Camera.OrthoHeight = 40; renderer.Camera.LookAt(center + new Vector3(0, -20, 20), center);
        byte[] Draw(CompiledNativePaintCell cell)
        { renderer.Clear(); foreach (var mesh in cell.Meshes) renderer.AddMesh(mesh); renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
        var originalImage = Draw(originalCell);
        try
        {
            properties["m_SpacingX"].Set!(.5f); properties["m_SpacingY"].Set!(.5f);
            properties["m_SpacingDeviationX"].Set!(0f); properties["m_SpacingDeviationY"].Set!(0f); properties["m_SpacingEnabled"].Set!(true);
            document.RefreshNativePaintFields();
            var field = document.NativePaintFields.Single(field => field.RootId == originalField.RootId); var cell = field.GenerateCell(0, -22, -1, 0, 1);
            check("copied authored fixed-spacing profile generates original97-point clipped native cell", field.Spacing is not null && cell.Candidates == 3268 && cell.FullCount == 97 && cell.Meshes.Count == 3);
            check("actual fixed-spacing profile changes GPU positions/counts while retaining source LOD", !Draw(cell).SequenceEqual(originalImage) && renderer.DrawnInstances == 97 && renderer.DrawnMeshes == 1);
            properties["m_SpacingX"].Set!(0f); document.RefreshNativePaintFields();
            check("invalid authored spacing remains explicitly refused and its existing component row remains repairable", document.NativePaintRefusals.ContainsKey(originalField.RootId)
                && Walk(document.Roots).Any(row => ReferenceEquals(row, sourceRow)));
            properties["m_SpacingX"].Set!(.5f); document.RefreshNativePaintFields();
            check("repairing fixed spacing restores the same immutable cell point set", document.NativePaintFields.Single(next => next.RootId == originalField.RootId).GenerateCell(0, -22, -1, 0, 1).Points.SequenceEqual(cell.Points));
        }
        finally { foreach (var (property, value) in saved) property.Set!(value); document.RefreshNativePaintFields(); }
        var restored = document.NativePaintFields.Single(field => field.RootId == originalField.RootId).GenerateCell(0, -22, -1, 0, 1);
        check("paint spacing property undo restores exact source bytes and exact GPU image", snapshots.All(pair => pair.Value.SequenceEqual(pair.Key.ToArray())
            && pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key.Path))) && Draw(restored).SequenceEqual(originalImage));
    }
}
