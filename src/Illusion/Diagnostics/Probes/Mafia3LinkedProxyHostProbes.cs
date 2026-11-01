using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Formats.Hashing;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Gizmos;
using Illusion.Scene;
using Illusion.Viewport;
using Illusion.Views;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestLinkedProxyHostPreparation(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_french_east_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        using var gpu = new GpuContext();
        using var host = new D3DImageHost();
        var renderer = new SceneRenderer(gpu);
        typeof(ViewportControl).GetProperty("Renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, renderer);
        var owner = new SceneNode("FrameResource", "FrameResource", true) { Source = document };
        host.Roots.Add(owner);
        var leaves = new List<SceneNode>();
        foreach (var native in document.Roots) owner.AddChild(SceneTree.BuildSceneTree(native, leaves));
        foreach (var leaf in leaves)
            if (leaf.Pending is { } pending)
            {
                var mesh = renderer.AddMesh(pending); mesh.Owner = leaf; leaf.Mesh = mesh; leaf.Pending = null;
            }
        host.Tree.MeshCount = renderer.Meshes.Count;
        var fine = owner.Children.Single(row => row.Source is CompiledSceneDocument.CompiledFrameNode frame
            && frame.SceneIdentity == "guid:0F947BBBC7BC8574");
        var originalRows = owner.Children.ToArray(); var originalMeshes = renderer.Meshes.ToArray();
        using var target = new SharedRenderTarget(gpu, 96, 96);
        renderer.ShowSky = false;
        var group = document.NativeLods!.Groups.Single(group => group.Id == 0x5B403B2A66404E6B);
        var center = (group.WorldMin + group.WorldMax) * .5f;
        var near = center + new Vector3(0, 0, 100);
        var farEye = new Vector3(center.X, group.WorldMin.Y - 600, group.WorldMax.Z + 200);
        byte[] Render(Vector3 eye)
        { renderer.Camera.LookAt(eye, center); renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
        var originalPixels = Render(near);
        var originalFarPixels = Render(farEye);
        var hashes = document.Graph.Resources.ToDictionary(resource => resource, resource => SHA256.HashData(resource.ToArray()));
        string? refusal = host.InspectCompiledLinkedProxy(fine, out var plan);
        check("host proxy inspection returns verified native costs without history", refusal is null && plan is { FineFrames: 14, AfterVisuals: 231, AfterDraws: 235 }
            && !host.History.CanUndo);
        var application = Application.Current ?? throw new InvalidOperationException("The authoring control probe requires a WPF application.");
        var previousShutdown = application.ShutdownMode; var previousMain = application.MainWindow;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        CompiledAuthoringWindow? authoring = null;
        try
        {
            authoring = new CompiledAuthoringWindow(host);
            var panel = (StackPanel)((ScrollViewer)authoring.Content).Content;
            host.Selection.SetSelection([fine], fine);
            panel.Children.OfType<Button>().Single(button => Equals(button.Content, "Inspect linked proxy costs"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("shared editor authoring controls display reviewed proxy costs without mutation", panel.Children.OfType<TextBlock>()
                .Any(text => text.Text.Contains("Family 0x5B403B2A66404E6B", StringComparison.Ordinal) && text.Text.Contains("235", StringComparison.Ordinal))
                && !host.History.CanUndo);
            panel.Children.OfType<Button>().Single(button => Equals(button.Content, "Prepare paired proxy transforms"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!host.History.CanUndo) throw new InvalidOperationException(string.Join("; ", panel.Children.OfType<TextBlock>().Select(text => text.Text)));
        }
        finally
        {
            authoring?.Close(); application.MainWindow = previousMain; application.ShutdownMode = previousShutdown;
        }
        check($"host proxy preparation installs native roots/history (rows {owner.Children.Count}/{originalRows.Length}; meshes {renderer.Meshes.Count}/{originalMeshes.Length}; tree {host.Tree.MeshCount})", host.History.CanUndo
            && owner.Children.Contains(fine) && owner.Children.Count == originalRows.Length + 13
            && renderer.Meshes.Count == originalMeshes.Length - 1 + 231 && host.Tree.MeshCount == renderer.Meshes.Count);
        var preparedRows = owner.Children.Except(originalRows).ToArray();
        var preparedMeshes = renderer.Meshes.Except(originalMeshes).ToArray();
        ulong protectedMaterial = preparedMeshes.SelectMany(mesh => mesh.Parts).Select(part => part.MaterialHash)
            .First(hash => host.MaterialEditing.Catalog.GetMaterial(hash)?.TextureSlots.Any(slot => slot.TextureName is not null) == true);
        var protectedSlot = host.MaterialEditing.Catalog.GetMaterial(protectedMaterial)!.TextureSlots.First(slot => slot.TextureName is not null);
        string originalTexture = protectedSlot.TextureName!;
        check("prepared global texture sampler and identity edits refuse through editor controls", !host.MaterialEditing.SetTexture(protectedMaterial, protectedSlot.SlotId, originalTexture + "_not_carried")
            && !host.MaterialEditing.AddTextureSlot(protectedMaterial, "T999") && !host.MaterialEditing.RemoveTextureSlot(protectedMaterial, protectedSlot.SlotId)
            && host.MaterialEditing.RenameMaterial(protectedMaterial, "itk_protected_material_probe") is null && !host.MaterialEditing.DeleteMaterial(protectedMaterial)
            && host.MaterialEditing.Catalog.GetTexture(protectedMaterial, protectedSlot.SlotId) == originalTexture);
        using (var otherHost = new D3DImageHost())
        {
            var textureEdit = typeof(MaterialEditController).GetNestedType("TextureEdit", BindingFlags.NonPublic)!;
            var recorded = (IEditAction)Activator.CreateInstance(textureEdit, otherHost.MaterialEditing, protectedMaterial,
                protectedSlot.SlotId, originalTexture + "_not_carried", originalTexture)!;
            otherHost.History.Push(recorded); bool refused = false;
            try { otherHost.History.Undo(); } catch (EditRefusedException) { refused = true; }
            check("cross-editor material history refuses before writes and preserves its undo entry", refused && otherHost.History.CanUndo
                && host.MaterialEditing.Catalog.GetTexture(protectedMaterial, protectedSlot.SlotId) == originalTexture);
            otherHost.History.Clear();
        }
        var collection = (MaterialCollection)typeof(MafiaMaterials).GetProperty("Collection", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var nativeMaterial = (Material_v63)collection.FindByHash(protectedMaterial)!;
        var textureToken = nativeMaterial.Textures[protectedSlot.SlotId];
        var savedFiles = document.Graph.Resources.Select(resource => resource.Path)
            .Concat(Directory.GetFiles(Path.Combine(MafiaEnvironment.GameRoot, "edit", "materials"), "*.mtl"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        nativeMaterial.Textures[protectedSlot.SlotId] = new HashName(originalTexture + "_not_carried");
        try
        {
            var save = host.SaveEditsReport();
            check("editor save reports invalid proxy material dependencies before any library or scene write", !save.Complete && save.Written == 0
                && host.Persistence.HasUnsavedEdits && savedFiles.All(pair => SHA256.HashData(File.ReadAllBytes(pair.Key)).AsSpan().SequenceEqual(pair.Value)));
        }
        finally { nativeMaterial.Textures[protectedSlot.SlotId] = textureToken; }
        check("generated host proxy meshes retain native coarse membership", preparedMeshes.Length == 231
            && preparedMeshes.All(mesh => mesh.NativeLod is { } membership && membership.Graph.Groups[membership.Group].Id == 0x5B403B2A66404E6B));
        check("prepared coarse geometry preserves exact near-view pixels", originalPixels.SequenceEqual(Render(near))
            && preparedMeshes.All(mesh => !mesh.NativeLodVisible));
        _ = Render(farEye);
        check("prepared native proxies render far while fine group is hidden", preparedMeshes.All(mesh => mesh.RenderVisible)
            && originalMeshes.Where(mesh => mesh.NativeLod is { } membership && membership.Graph.Groups[membership.Group].Id == 0x5B403B2A66404E6D)
                .All(mesh => !mesh.NativeLodVisible));
        var fineFrame = (IFrameNode)fine.Source!;
        var farFrame = document.GetLinkedProxyTransformTargets(fineFrame).Single();
        var farRow = preparedRows.Single(row => ReferenceEquals(row.Source, farFrame));
        bool OwnedBy(GpuMesh mesh, IFrameNode frame) => mesh.Owner is SceneNode { Source: IFrameNode source }
            && ReferenceEquals(source is ICompiledMeshSource visual ? visual.CanonicalFrame : source, frame);
        var pairedGpu = renderer.Meshes.Where(mesh => OwnedBy(mesh, fineFrame) || OwnedBy(mesh, farFrame)).ToDictionary(mesh => mesh, mesh => mesh.World);
        var beforeFine = fineFrame.WorldTransform; var beforeFar = farFrame.WorldTransform;
        host.Selection.SetSelection([fine], fine); host.GizmoBeginDrag(GizmoMode.Move);
        refusal = host.Editing.TryGizmoApplyWorldDelta(Matrix4x4.CreateTranslation(.25f, 0, 0)); host.GizmoEndDrag();
        check("prepared fine gizmo move refreshes both fine and far native GPU worlds", refusal is null && pairedGpu.Count > 0
            && fineFrame.WorldTransform.Translation == beforeFine.Translation + new Vector3(.25f, 0, 0)
            && farFrame.WorldTransform.Translation == beforeFar.Translation + new Vector3(.25f, 0, 0)
            && pairedGpu.All(pair => pair.Key.World.Translation == pair.Value.Translation + new Vector3(.25f, 0, 0)));
        host.History.Undo();
        check("paired gizmo undo restores both canonical and GPU worlds", fineFrame.WorldTransform == beforeFine && farFrame.WorldTransform == beforeFar
            && pairedGpu.All(pair => pair.Key.World == pair.Value));
        host.History.Redo(); host.History.Undo();
        host.Selection.SetSelection([fine, farRow], farRow); host.GizmoBeginDrag(GizmoMode.Move);
        refusal = host.Editing.TryGizmoApplyWorldDelta(Matrix4x4.CreateTranslation(.25f, 0, 0)); host.GizmoEndDrag();
        check("mixed fine and generated coarse gizmo refuses before any write and preserves redo", refusal is not null
            && fineFrame.WorldTransform == beforeFine && farFrame.WorldTransform == beforeFar && host.History.CanRedo
            && pairedGpu.All(pair => pair.Key.World == pair.Value));
        host.History.Undo();
        check("host proxy undo restores original tree and GPU instances", owner.Children.Count == originalRows.Length
            && originalRows.SequenceEqual(owner.Children) && renderer.Meshes.SequenceEqual(originalMeshes)
            && host.Tree.MeshCount == renderer.Meshes.Count
            && hashes.All(pair => SHA256.HashData(pair.Key.ToArray()).AsSpan().SequenceEqual(pair.Value)));
        check("host proxy undo restores exact original near and far GPU pixels", originalPixels.SequenceEqual(Render(near))
            && originalFarPixels.SequenceEqual(Render(farEye)));
        host.History.Redo();
        check("host proxy redo reuses native rows and GPU instances", preparedRows.All(owner.Children.Contains)
            && preparedMeshes.All(renderer.Meshes.Contains) && owner.Children.Contains(fine));
        host.History.Undo(); host.History.Clear();
        check("discarding undone preparation leaves original meshes usable", originalMeshes.All(renderer.Meshes.Contains)
            && document.PreparedLinkedProxyGroups.Count == 0 && !host.History.CanUndo);
    }
}
