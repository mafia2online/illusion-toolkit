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
    internal static void TestPreparedProxyMorphHost(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_french_east_block_b_lod0.sds"));
        string source = Path.Combine(MafiaEnvironment.ResourcesFolder!, "sds_retail", "city_crash", "bc_city_crash.sds", "Generic_3379_00689871E06C2C3D.ires.compiled");
        if (!archive.Exists || !File.Exists(source)) return;
        var document = new CompiledSceneDocument(archive); if (document.PreparedLinkedProxyGroups.Count != 0) return;
        var files = document.Graph.Resources.Select(resource => Path.GetDirectoryName(resource.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(Directory.GetFiles)
            .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)), StringComparer.OrdinalIgnoreCase);
        using var gpu = new GpuContext(); using var host = new D3DImageHost();
        var renderer = new SceneRenderer(gpu) { ShowSky = false, Mode = RenderMode.Solid };
        typeof(ViewportControl).GetProperty("Renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, renderer);
        using var target = new SharedRenderTarget(gpu, 192, 192);
        var root = new SceneNode("FrameResource", "FrameResource", true) { Source = document }; host.Roots.Add(root);
        var leaves = new List<SceneNode>(); foreach (var native in document.Roots) root.AddChild(SceneTree.BuildSceneTree(native, leaves));
        foreach (var leaf in leaves) if (leaf.Pending is { } data) { var mesh = renderer.AddMesh(data); mesh.Owner = leaf; leaf.Mesh = mesh; leaf.Pending = null; }
        host.Tree.MeshCount = renderer.Meshes.Count;
        static IEnumerable<SceneNode> Walk(SceneNode row) { yield return row; foreach (var child in row.Children) foreach (var descendant in Walk(child)) yield return descendant; }
        var owner = Walk(root).First(row => row.Source is CompiledSceneDocument.CompiledFrameNode frame && frame.SceneIdentity == "guid:0F947BBBC7BC8574");
        var refusal = host.InspectCompiledLinkedProxy(owner, out var plan); if (refusal is not null || plan is null) throw new InvalidDataException(refusal);
        if (host.PrepareCompiledLinkedProxy(owner, plan) is { } prepare) throw new InvalidDataException(prepare);
        try
        {
            refusal = host.CreateLinkedProxyGroup(owner, "native_morph_host", out var parent); if (refusal is not null || parent is null) throw new InvalidDataException(refusal);
            foreach (var mesh in renderer.Meshes) mesh.Visible = false;
            var originalMeshes = renderer.Meshes.ToArray(); int count = document.ObjectCount, history = host.History.UndoCount;
            var native = document.Graph.Resources.ToDictionary(resource => resource, resource => (SHA: SHA256.HashData(resource.ToArray()), resource.Revision, resource.IsDirty));
            long bounds = document.NativeLods!.BoundsRevision; host.Selection.SetSelection([parent], parent);
            host.BeforePreparedMorphGpuAllocation = index => { if (index == 2) throw new InvalidOperationException("Injected second morph GPU allocation failure."); };
            try { refusal = host.ImportLinkedProxyMorph(owner, parent, source, 0x155D0F95DB818728, out _); }
            finally { host.BeforePreparedMorphGpuAllocation = null; }
            check("prepared morph second GPU failure restores native state history selection bounds and GPU order", refusal is not null
                && document.ObjectCount == count && host.History.UndoCount == history && ReferenceEquals(host.Selection.Active, parent)
                && renderer.Meshes.SequenceEqual(originalMeshes) && document.NativeLods.BoundsRevision == bounds
                && native.All(pair => pair.Key.Revision == pair.Value.Revision && pair.Key.IsDirty == pair.Value.IsDirty && SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value.SHA)));
            refusal = host.ImportLinkedProxyMorph(owner, parent, source, 0x155D0F95DB818728, out var imported); if (refusal is not null || imported is null) throw new InvalidDataException(refusal);
            var row = Walk(imported).First(row => (row.GeometrySource ?? row.Source) is ICompiledMeshSource { GeometryBinding: { } binding }
                && document.Graph.Resolve(document.Graph.Field(document.Graph.Resolve(document.Graph.Field(binding.Visual, "m_MeshSet")), "m_MorphTextureData")) is not null);
            var sourceBinding = ((ICompiledMeshSource)(row.GeometrySource ?? row.Source)!).GeometryBinding!;
            var bindings = document.GetPreparedMorphBindings(sourceBinding, out refusal); if (refusal is not null) throw new InvalidDataException(refusal);
            var bodies = renderer.Meshes.Where(mesh => mesh.Owner is SceneNode node && (node.GeometrySource ?? node.Source) is ICompiledMeshSource { GeometryBinding: { } binding } && bindings.Contains(binding)).ToArray();
            check("prepared morph host installs both two-visual GPU roots and one seven-bone tree each", renderer.Meshes.Count == originalMeshes.Length + 4 && bodies.Length == 2
                && Walk(imported).Count(node => node.Kind == "Bone") == 7);
            var bytes = document.Graph.Resources.ToDictionary(resource => resource, resource => (SHA: SHA256.HashData(resource.ToArray()), resource.Revision));
            var center = (bodies[0].BoundsMin + bodies[0].BoundsMax) * .5f; var direction = Vector3.Normalize(new Vector3(3, -4, 2));
            renderer.Camera.Orthographic = true; renderer.Camera.OrthoHeight = 2.2f; host.Selection.Select(null);
            byte[] Draw(float distance) { renderer.Camera.LookAt(center + direction * distance, center); renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
            var nearOriginal = Draw(6); var farOriginal = Draw(1500);
            if (host.PreviewCompiledMorph(row, new Dictionary<int, float> { [0] = 1, [1] = .7f, [2] = .3f, [3] = .5f }) is { } morph) throw new InvalidDataException(morph);
            check("prepared manual morph preview updates actual near and far GPU pixels together", !Draw(6).SequenceEqual(nearOriginal) && !Draw(1500).SequenceEqual(farOriginal));
            host.ClearMorphPreview();
            check("prepared manual morph clear restores both exact GPU images without native writes", Draw(6).SequenceEqual(nearOriginal) && Draw(1500).SequenceEqual(farOriginal)
                && bytes.All(pair => pair.Key.Revision == pair.Value.Revision && SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value.SHA)));
            if (host.PreviewCompiledMorph(row, new Dictionary<int, float> { [0] = 1 }) is { } again) throw new InvalidDataException(again);
            if (host.TryUndo() is { } undo) throw new InvalidDataException(undo);
            check("public morph import undo clears previews before removing both GPU roots", !host.Tree.IsInScene(imported) && renderer.Meshes.SequenceEqual(originalMeshes));
            if (host.TryRedo() is { } redo) throw new InvalidDataException(redo);
            check("public morph import redo restores authored near far pixels and native tree", host.Tree.IsInScene(imported) && Walk(imported).Count(node => node.Kind == "Bone") == 7
                && Draw(6).SequenceEqual(nearOriginal) && Draw(1500).SequenceEqual(farOriginal));
            document.SaveWorkingCopy(); var reopened = new CompiledSceneDocument(archive);
            check("host morph import independently reopens a valid prepared mapping", reopened.LinkedProxyMappingWarnings.Count == 0);
        }
        finally
        {
            host.BeforePreparedMorphGpuAllocation = null; host.ClearMorphPreview();
            while (host.History.UndoCount > 0) if (host.TryUndo() is { } undo) throw new InvalidDataException(undo);
            document.SaveWorkingCopy(); host.History.Clear();
        }
        check("host morph import undo preserves every prior native working file and manifest", files.All(pair => File.Exists(pair.Key) && SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)));
    }
}
