using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativePaintFields(Action<string, bool> check)
    {
        check("native paint UV expansion flushes half subnormals and preserves native finite exponent31", CompiledPaintMapping.NativeHalf(0x8001) == 0
            && CompiledPaintMapping.NativeHalf(0x7c00) == 65536 && CompiledPaintMapping.NativeHalf(0xfc00) == -65536);
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        var field = document.NativePaintFields.FirstOrDefault(field => field.RootId == 0x17AB21AB80918124);
        if (field is null) { check("copied Bayou native paint entry resolves its explicit attached visual and template", false); return; }
        var resources = document.Graph.Resources.ToDictionary(resource => resource, resource => resource.ToArray());
        check("copied Bayou native paint entries expose static mesh profiles and explicit effect refusals", document.NativePaintFields.Count == 67
            && document.NativePaintFields.Count(field => field.NativeBakedBins) == 54 && document.NativePaintRefusals.Count == 11
            && document.NativePaintFields.All(field => field.OwnerSceneId != 0 && field.OwnerVisualHash != 0 && field.RenderRefusal is null));
        var cell = field.GenerateCell(0, -22, -1, 0, 1);
        check("actual copied weed cell generates clipped density-filtered points and all three native mesh levels", cell.Candidates == 675
            && cell.FullCount == 77 && cell.Points.Count == 77 && cell.Meshes.Count == 3 && cell.Meshes.All(mesh => mesh.Instances?.Length == 77));
        var repeated = field.GenerateCell(0, -22, -1, 0, 1);
        check("native paint snapshot repeat is deterministic with explicitly editor-seeded shuffle", cell.Points.SequenceEqual(repeated.Points)
            && cell.Meshes.Zip(repeated.Meshes).All(pair => pair.First.Instances!.SequenceEqual(pair.Second.Instances!)));
        var changedSeed = field.GenerateCell(0, -22, -1, 0, 2);
        string PointKey(Illusion.Formats.Compiled.CompiledPaintTriangle.Point point) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(new[] { point }.AsSpan())));
        check("paint editor shuffle changes order without inventing or deleting native accepted points", !cell.Points.SequenceEqual(changedSeed.Points)
            && cell.Points.Select(PointKey).Order().SequenceEqual(changedSeed.Points.Select(PointKey).Order()));
        check("baked paint levels use one fixed full-cell native expanded query box", cell.Meshes.All(mesh => mesh.InstanceLod?.QueryBounds is not null)
            && cell.Meshes.Select(mesh => mesh.InstanceLod!.QueryBounds).Distinct().Count() == 1);
        bool budgetRefused = false, cancelled = false;
        try { _ = field.GenerateCell(0, -22, -1, 0, 1, 1); } catch (NotSupportedException) { budgetRefused = true; }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { _ = field.GenerateCell(0, -22, -1, 0, 1, cancellationToken: cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
        }
        check("native paint generation honors bounded candidates and cancellation without partial results", budgetRefused && cancelled);
        using var gpu = new GpuContext(); using var renderer = new SceneRenderer(gpu) { ShowSky = false };
        using var target = new SharedRenderTarget(gpu, 96, 96);
        foreach (var mesh in cell.Meshes) renderer.AddMesh(mesh);
        var center = cell.Points.Aggregate(Vector3.Zero, (sum, point) => sum + point.Position) / cell.FullCount;
        renderer.Camera.Orthographic = true; renderer.Camera.OrthoHeight = 40; renderer.Camera.LookAt(center + new Vector3(0, -20, 20), center);
        byte[] Draw() { renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
        var full = Draw();
        check("actual procedural foliage draws the native selected aggregate level", renderer.DrawnInstances == 77 && renderer.DrawnMeshes == 1);
        for (int i = 0; i < cell.Meshes.Count; i++) renderer.UpdateInstances(renderer.Meshes[i], cell.Meshes[i].Instances![..30]);
        var partial = Draw();
        check("native paint active prefix changes GPU instance count and pixels", renderer.DrawnInstances == 30 && !partial.SequenceEqual(full));
        for (int i = 0; i < cell.Meshes.Count; i++) renderer.UpdateInstances(renderer.Meshes[i], cell.Meshes[i].Instances!);
        check("native paint full-prefix rewind restores exact pixels", Draw().SequenceEqual(full));
        IEnumerable<CompiledSceneDocument.CompiledFrameNode> Frames(IEnumerable<Illusion.Assets.Sds.SdsFrameNode> rows)
        {
            foreach (var row in rows)
            {
                if (row.Source is CompiledSceneDocument.CompiledFrameNode frame) yield return frame;
                foreach (var child in Frames(row.Children)) yield return child;
            }
        }
        var owner = Frames(document.Roots).First(frame => frame.SceneIdentity == $"guid:{field.OwnerSceneId:X16}");
        bool ownerRefused = false;
        try { var moved = owner.LocalTransform; moved.M43 += .25f; owner.LocalTransform = moved; }
        catch (Illusion.Domain.EditRefusedException) { ownerRefused = true; }
        check("paint preview does not bypass linked native terrain authoring guards", ownerRefused);
        document.RefreshNativePaintFields();
        var refreshed = document.NativePaintFields.Single(next => next.RootId == field.RootId);
        check("paint recapture publishes a new immutable snapshot while old worker inputs remain exact", refreshed.Revision != field.Revision
            && refreshed.GenerateCell(0, -22, -1, 0, 1).Points.SequenceEqual(cell.Points)
            && field.GenerateCell(0, -22, -1, 0, 1).Points.SequenceEqual(cell.Points));
        check("paint snapshot generation and GPU prefix updates preserve every compiled resource", resources.All(pair => pair.Key.ToArray().SequenceEqual(pair.Value)
            && pair.Key.ToArray().SequenceEqual(File.ReadAllBytes(pair.Key.Path))));
    }
}
