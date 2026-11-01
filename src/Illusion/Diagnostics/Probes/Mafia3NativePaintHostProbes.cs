using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativePaintHost(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        var allFields = document.NativePaintFields;
        var field = allFields.FirstOrDefault(candidate => candidate.RootId == 0x17AB21AB80918124UL);
        if (field is null) return;
        var property = typeof(CompiledSceneDocument).GetProperty(nameof(CompiledSceneDocument.NativePaintFields))!;
        void Restrict() => property.SetValue(document, new[] { field });
        Restrict();
        var native = document.Graph.Resources.ToDictionary(resource => resource, resource => SHA256.HashData(resource.ToArray()));
        var files = document.Graph.Resources.Select(resource => resource.Path).Append(archive.FullName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)), StringComparer.OrdinalIgnoreCase);
        var revisions = document.Graph.Resources.Select(resource => (resource.Revision, resource.IsDirty)).ToArray();
        using var gpu = new GpuContext(); using var host = new D3DImageHost(); var renderer = new SceneRenderer(gpu) { ShowSky = false };
        typeof(ViewportControl).GetProperty("Renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, renderer);
        using var target = new SharedRenderTarget(gpu, 96, 96);
        var root = new SceneNode("FrameResource", "FrameResource", true) { Source = document }; host.Roots.Add(root);
        var leaves = new List<SceneNode>(); foreach (var row in document.Roots) root.AddChild(SceneTree.BuildSceneTree(row, leaves));
        var cache = host.Streamer.PaintPreview;
        static IEnumerable<SceneNode> Walk(SceneNode row) { yield return row; foreach (var child in row.Children) foreach (var descendant in Walk(child)) yield return descendant; }
        var owner = Walk(root).First(row => row.Kind != "Lod" && (row.GeometrySource ?? row.Source) is ICompiledMeshSource { GeometryBinding: { } binding,
            CanonicalFrame: CompiledSceneDocument.CompiledFrameNode frame } && frame.SceneIdentity == $"guid:{field.OwnerSceneId:X16}"
            && document.Graph.Field(binding.Visual, "m_Data") is { DataSize: 88 } data
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(document.Graph.Data(data)[56..]) == field.OwnerVisualHash);
        var cell = field.GenerateCell(0, -22, -1, 0, 1);
        var focus = cell.Points.Aggregate(Vector3.Zero, (sum, point) => sum + point.Position) / cell.Points.Count;
        renderer.Camera.Orthographic = true; renderer.Camera.OrthoHeight = 45; renderer.Camera.LookAt(focus + new Vector3(0, -35, 30), focus);
        byte[] Draw() { renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
        bool Pump(Func<bool> done, int milliseconds = 30000)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < milliseconds) { cache.Tick(.016f); if (done()) return true; Thread.Sleep(1); }
            return false;
        }
        var blank = Draw(); int history = host.History.UndoCount;
        try
        {
            check("native paint cache real copied field produces asynchronous cells and GPU instances", Pump(() => cache.Status.GpuMeshes >= 3 && cache.Status.ActivePoints > 0)
                && cell.Candidates == 675 && cell.FullCount == 77 && cell.Meshes.Count == 3);
            check("native paint owner keeps its actual terrain LOD membership", field.OwnerLod is { } lod && lod.Graph.RenderCoverage![lod.Group] > 0
                && renderer.Meshes.All(mesh => ReferenceEquals(mesh.NativeLod, field.OwnerLod)));
            // Wait for the bounded one-field workload to settle before comparing deterministic output.
            check("native paint single-field workload settles within its explicit budgets", Pump(() => cache.Status.PendingCells == 0) && cache.Status.Error is null);
            var first = Draw(); int points = cache.Status.ActivePoints;
            check("native paint asynchronous cache draws actual nonblank offscreen pixels", !first.SequenceEqual(blank) && renderer.DrawnInstances > 0);
            owner.IsVisible = false; cache.Tick(0);
            check("native paint respects its actual scene-tree owner eye", renderer.Meshes.All(mesh => !mesh.Visible) && Draw().SequenceEqual(blank));
            owner.IsVisible = true; cache.Tick(0);
            check("native paint restoring the owner eye restores exact pixels", Draw().SequenceEqual(first));
            var revisionReference = document.Graph.Field(document.Graph.Objects[field.RootId], "m_LayerInstance")!;
            var revisionField = revisionReference.Event == 4 ? revisionReference : revisionReference.Values.First(value => value.Event == 4);
            var revisionResource = document.Graph.Owner(revisionField); var restoreRevision = revisionResource.CaptureRevertedMutationCheckpoint();
            var oldMeshes = renderer.Meshes.ToArray(); long oldFieldRevision = field.Revision;
            var originalReference = document.Graph.Data(revisionField).ToArray(); var temporaryReference = originalReference.ToArray(); temporaryReference[0] ^= 1;
            // A completed edit followed by undo still advances the native source revision.
            revisionResource.Replace(revisionField, temporaryReference); revisionResource.Replace(revisionField, originalReference);
            cache.Tick(0);
            check("native paint source revision replaces immutable profiles and removes all obsolete cached geometry", document.NativePaintFields.Any(candidate => candidate.RootId == field.RootId && candidate.Revision != oldFieldRevision)
                && oldMeshes.All(mesh => !renderer.Meshes.Contains(mesh)));
            cache.Reset(); field = document.NativePaintFields.Single(candidate => candidate.RootId == field.RootId); restoreRevision(); Restrict();
            check("native paint source refresh regenerates exact deterministic pixels", Pump(() => cache.Status.PendingCells == 0 && cache.Status.ActivePoints > 0)
                && Draw().SequenceEqual(first));
            cache.Configure(false, null); cache.Tick(0);
            check("native paint OFF cancels pending work and removes all cached GPU rows", renderer.Meshes.Count == 0 && cache.Status.GpuMeshes == 0 && Draw().SequenceEqual(blank));
            cache.Configure(true, null); Restrict(); cache.Tick(0);
            var worker = (Task?)typeof(NativePaintPreview).GetField("_work", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache);
            cache.Configure(false, null);
            try { _ = worker?.Wait(5000); } catch (AggregateException exception) when (exception.InnerExceptions.All(error => error is OperationCanceledException)) { }
            cache.Tick(0);
            check("native paint canceled workers cannot attach after reset", renderer.Meshes.Count == 0 && cache.Status.GpuMeshes == 0 && host.History.UndoCount == history);
            cache.Configure(true, 1); Restrict();
            check("native paint fixed-seed regeneration restores exact geometry counts and pixels", Pump(() => cache.Status.PendingCells == 0 && cache.Status.ActivePoints > 0)
                && cache.Status.ActivePoints == points && Draw().SequenceEqual(first));
            cache.Reset(); Restrict(); int allocations = 0;
            cache.BeforeGpuAllocation = () => { if (++allocations >= 2) throw new InvalidOperationException("Injected paint second GPU allocation failure."); };
            check("native paint second GPU allocation failure is reported before installing its cell", Pump(() => cache.Status.Error is not null)
                && allocations >= 2 && renderer.Meshes.Count == 0 && cache.Status.GpuMeshes == 0);
            cache.BeforeGpuAllocation = null; cache.Reset(); Restrict();
            cache.Tick(0); cache.Reset(); host.Roots.Remove(root); Thread.Sleep(25); cache.Tick(0);
            check("native paint document removal cancels late attachments without editor history", renderer.Meshes.Count == 0 && cache.Status.Fields == 0 && host.History.UndoCount == history);
            check("native paint preview leaves every native resource file revision dirty state and byte unchanged", native.All(pair => SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value))
                && revisions.SequenceEqual(document.Graph.Resources.Select(resource => (resource.Revision, resource.IsDirty)))
                && files.All(pair => File.Exists(pair.Key) && SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)));
        }
        finally { cache.BeforeGpuAllocation = null; cache.Reset(); property.SetValue(document, allFields); }
    }
}
