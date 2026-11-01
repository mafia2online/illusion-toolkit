using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestLinkedProxyGeometryBatchHost(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_french_east_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive); if (document.PreparedLinkedProxyGroups.Count != 0) return;
        using var gpu = new GpuContext(); using var host = new D3DImageHost(); var renderer = new SceneRenderer(gpu);
        typeof(ViewportControl).GetProperty("Renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, renderer);
        var owner = new SceneNode("FrameResource", "FrameResource", true) { Source = document }; host.Roots.Add(owner);
        var leaves = new List<SceneNode>();
        foreach (var native in document.Roots) owner.AddChild(SceneTree.BuildSceneTree(native, leaves));
        foreach (var leaf in leaves) if (leaf.Pending is { } pending)
        { var mesh = renderer.AddMesh(pending); mesh.Owner = leaf; leaf.Mesh = mesh; leaf.Pending = null; }
        host.Tree.MeshCount = renderer.Meshes.Count;
        var fine = owner.Children.Single(row => row.Source is CompiledSceneDocument.CompiledFrameNode frame && frame.SceneIdentity == "guid:0F947BBBC7BC8574");
        var refusal = host.InspectCompiledLinkedProxy(fine, out var plan);
        if (refusal is not null || plan is null) throw new InvalidDataException(refusal);
        if (host.PrepareCompiledLinkedProxy(fine, plan) is { } rejected) throw new InvalidDataException(rejected);
        var bindings = EntityCreationRows(document.Roots).Select(row => (row.GeometrySource ?? row.Source) as ICompiledMeshSource)
            .Where(source => source?.GeometryBinding is not null && ReferenceEquals(source.CanonicalFrame, fine.Source))
            .OrderBy(source => source!.SceneIdentity, StringComparer.Ordinal).Select(source => source!.GeometryBinding!).Take(2).ToArray();
        var rows = bindings.Select(binding => renderer.Meshes.Select(mesh => mesh.Owner).OfType<SceneNode>().Single(row =>
            row.Lod == binding.LodCount - 1 && (row.GeometrySource ?? row.Source) is ICompiledMeshSource { GeometryBinding: { } source }
            && ReferenceEquals(source.Visual, binding.Visual))).ToArray();
        var requests = bindings.Select((binding, index) =>
        {
            int lod = binding.LodCount - 1;
            var payload = binding.Export(document, lod, out var error) ?? throw new InvalidDataException(error);
            for (int vertex = 0; vertex < payload.Positions.Length; vertex++) payload.Positions[vertex] += new Vector3(index + 12, 0, 0);
            PokeProxyTriangle(payload); return new CompiledLinkedGeometryRequest(binding, payload, lod);
        }).ToArray();
        var before = document.Graph.Resources.ToDictionary(resource => resource, resource => (resource.Revision, Bytes: SHA256.HashData(resource.ToArray())));
        var meshes = renderer.Meshes.ToArray(); var primary = rows.Select(row => row.Mesh).ToArray();
        primary[0]!.Visible = false; primary[0]!.Ghost = true;
        bool Exact() => before.All(pair => SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value.Bytes));
        var batch = document.PlanLinkedProxyGeometryBatch(requests, out refusal) ?? throw new InvalidDataException(refusal);
        var geometry = rows.Select((row, index) => new GeometryEditController.GeometryItem(row, batch.Results[index])).ToArray();
        int allocations = 0; bool failed = false;
        host.GeometryEditing.BeforeGeometryGpuAllocation = () => { if (++allocations == 3) throw new InvalidOperationException("Injected aggregate GPU failure."); };
        try { host.GeometryEditing.ApplyPushBatch(geometry, [], [], null); }
        catch (InvalidOperationException) { failed = true; }
        finally { host.GeometryEditing.BeforeGeometryGpuAllocation = null; }
        check("aggregate paired GPU allocation failure preserves native revisions meshes order and history", failed && Exact()
            && before.All(pair => pair.Key.Revision == pair.Value.Revision) && meshes.SequenceEqual(renderer.Meshes) && !host.History.CanRedo);
        var local = ((IFrameNode)fine.Source!).LocalTransform;
        var moved = local * Matrix4x4.CreateTranslation(.25f, 0, 0);
        host.GeometryEditing.ApplyPushBatch(geometry, [new(fine, local, moved)], [], null);
        var changed = renderer.Meshes.ToArray();
        check("aggregate paired push replaces both primary GPU meshes and all affected LOD outputs", rows.Select((row, index) => !ReferenceEquals(row.Mesh, primary[index])).All(value => value)
            && changed.Length == meshes.Length && document.ValidateLinkedProxyTransform((IFrameNode)fine.Source!, ((IFrameNode)fine.Source!).LocalTransform) is null);
        check("aggregate paired geometry preserves manually hidden ghost GPU state through combined transform", !rows[0].Mesh!.Visible && rows[0].Mesh!.Ghost
            && ((IFrameNode)fine.Source!).LocalTransform == moved);
        host.History.Undo();
        check("aggregate paired host Undo restores native bytes GPU identities and draw order", Exact() && meshes.SequenceEqual(renderer.Meshes)
            && !primary[0]!.Visible && primary[0]!.Ghost && ((IFrameNode)fine.Source!).LocalTransform == local);
        host.History.Redo();
        check("aggregate paired host Redo restores final GPU identities", changed.SequenceEqual(renderer.Meshes));
        host.History.Undo(); host.History.Undo(); host.History.Clear();
    }
}
