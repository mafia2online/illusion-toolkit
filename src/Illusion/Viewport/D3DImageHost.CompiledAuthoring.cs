using Illusion.Domain;
using Illusion.Assets.Compiled;
using System.Numerics;
using Illusion.Scene;
using Illusion.Rendering.Gpu;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    internal string? AddCompiledCollision(SceneNode target, string kind, Vector3 size, Vector3 position,
        IReadOnlyList<Vector3>? points, ushort material, out SceneNode? created, ushort parentBone = ushort.MaxValue, IReadOnlyList<uint>? triangleIndices = null)
    {
        created = null;
        if (IsLoading || Rnd is null) return "wait for the scene to finish loading";
        if (target.Source is not IFrameNode frame || target.OwningDocumentNode()?.Source is not CompiledSceneDocument document)
            return "select a compiled scene object";
        var mutation = document.AddCollision(frame, kind, size, Matrix4x4.CreateTranslation(position), points, material, out var refusal, parentBone, triangleIndices);
        if (mutation is null) return refusal ?? "the collision could not be created";
        var parent = target;
        while (parent.Parent is { } next && ReferenceEquals(next.Source, frame)) parent = next;
        var row = SceneTree.BuildSceneTree(mutation.Root, []);
        var edit = new CreatedGroupEdit(this, parent, row, mutation);
        edit.Redo(); History.Push(edit); created = row; CarCollisionEditing.RefreshOverlay();
        return null;
    }

    internal string? MakeCompiledMeshUnique(SceneNode node)
    {
        if (IsLoading || Rnd is null) return "wait for the scene to finish loading";
        if ((node.GeometrySource ?? node.Source) is not ICompiledMeshSource { GeometryBinding: { } binding } source
            || node.OwningDocumentNode()?.Source is not CompiledSceneDocument document) return "select one compiled visual mesh";
        var edit = document.IsolateVisualGeometry(binding.Visual, out var refusal);
        if (edit is null) return refusal ?? "the visual could not be isolated";
        var pairs = new List<(SceneNode Row, GpuMesh Before, GpuMesh After)>();
        try
        {
            foreach (var mesh in Rnd.Meshes.ToArray())
                if (mesh.Owner is SceneNode row && ReferenceEquals(row.GeometrySource ?? row.Source, source))
                {
                    var data = binding.DecodeLod(row.Lod) ?? throw new InvalidOperationException("The isolated LOD could not be decoded.");
                    var replacement = Rnd.CreateMeshGpu(data); replacement.Owner = row;
                    pairs.Add((row,mesh,replacement));
                }
            var shown = new VisualIsolationEdit(this, edit, pairs);
            shown.Install(); KeepCompiledMutation(node, shown); return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            edit.Undo(); foreach (var pair in pairs) pair.After.Dispose();
            return "the isolated visual could not be displayed: " + ex.Message;
        }
    }

    private sealed class VisualIsolationEdit(D3DImageHost host, IEditAction mutation,
        List<(SceneNode Row, GpuMesh Before, GpuMesh After)> pairs) : IEditAction
    {
        private bool _applied = true;
        public void Install() { foreach (var pair in pairs) Swap(pair.Row,pair.Before,pair.After); }
        private void Swap(SceneNode row, GpuMesh before, GpuMesh after)
        {
            host.Rnd?.DetachMeshes([before]); host.Rnd?.AttachMesh(after);
            after.Ghost = before.Ghost; after.Visible = before.Visible; row.Mesh = after;
            if (row.Source is IFrameNode frame) after.SetWorld(frame.WorldTransform);
        }
        public void Undo()
        { if (!_applied) return; mutation.Undo(); foreach (var pair in pairs) Swap(pair.Row,pair.After,pair.Before); _applied=false; }
        public void Redo()
        { if (_applied) return; mutation.Redo(); Install(); _applied=true; }
        public void Discard()
        { mutation.Discard(); foreach (var pair in pairs) { if (_applied) pair.Before.Dispose(); else pair.After.Dispose(); } }
    }
    internal string? CreateCompiledGroup(string name, SceneNode? parent, out SceneNode? created)
    {
        created = null;
        if (IsLoading || BridgeEditedCount > 0) return "wait for loading and end the Blender session before creating a group";
        SceneNode? documentNode = parent?.OwningDocumentNode();
        if (documentNode is null)
        {
            IEnumerable<SceneNode> Walk(IEnumerable<SceneNode> roots)
            { foreach (var row in roots) { yield return row; foreach (var child in Walk(row.Children)) yield return child; } }
            var candidates = Walk(Tree.Roots).Where(row => row.Source is IFrameGroupProvider).ToArray();
            if (candidates.Length != 1) return "choose a parent in one destination document before creating a group";
            documentNode = candidates[0];
        }
        if (documentNode.Source is not IFrameGroupProvider provider) return "this document does not support compiled groups";
        if (parent is not null && parent.Source is not IFrameNode) return "the parent must be a scene group";
        var mutation = provider.CreateGroup(name, parent?.Source as IFrameNode, out var refusal);
        if (mutation is null) return refusal ?? "the group could not be created";
        var row = SceneTree.BuildSceneTree(mutation.Root, []);
        var edit = new CreatedGroupEdit(this, parent ?? documentNode, row, mutation);
        edit.Redo(); History.Push(edit); created = row;
        return null;
    }

    internal string? CreateLinkedProxyGroup(SceneNode owner, string name, out SceneNode? created)
    {
        created = null;
        if (!Tree.IsInScene(owner)) return "The fine object is no longer in this scene.";
        if (IsLoading || BridgeEditedCount > 0) return "Wait for loading and end the Blender session before creating a group.";
        if (owner.Source is not IFrameNode source || owner.OwningDocumentNode() is not { Source: CompiledSceneDocument document } documentNode)
            return "Select a prepared fine object.";
        var mutation = document.CreatePreparedLinkedProxyGroup(source, name, out var refusal);
        if (mutation is null) return refusal ?? "The native group could not be created.";
        var row = SceneTree.BuildSceneTree(mutation.Root, []);
        var edit = new CreatedGroupEdit(this, documentNode, row, mutation);
        edit.Redo(); History.Push(edit); created = row; return null;
    }

    internal string? TranslateCompiledNavigation(SceneNode node, int section, Vector3 offset)
    {
        if (IsLoading || BridgeEditedCount > 0) return "wait for loading and end the Blender session before editing navigation";
        var owner = node.Source is CompiledSceneDocument ? node : node.OwningDocumentNode();
        if (owner?.Source is not CompiledSceneDocument document) return "select a Mafia III document or one of its objects";
        var edit = document.TranslateNavigationSection(section, offset, out var refusal);
        if (edit is null) return refusal ?? "the navigation section could not be translated";
        KeepCompiledMutation(owner, edit);
        return null;
    }

    internal void KeepCompiledMutation(SceneNode node, IEditAction mutation)
    {
        ClearMorphPreview();
        History.Push(new CompiledMutationEdit(this, node, mutation));
        Persistence.MarkFrameModified(node); CarCollisionEditing.RefreshOverlay();
        RaiseSelectionPropertiesChanged(); RaiseSceneChanged();
    }

    private sealed class CreatedGroupEdit(D3DImageHost host, SceneNode parent, SceneNode row, IFrameDuplication mutation) : INodeEdit, IPreflightEdit
    {
        private bool _attached = true;
        public IEnumerable<SceneNode> Nodes => [row];
        public void ValidateUndo() => mutation.ValidateDetach();
        public void ValidateRedo() { if (!_attached) mutation.ValidateReattach(); }
        public void Redo()
        {
            ValidateRedo();
            if (!_attached) mutation.Reattach();
            parent.AddChild(row); host.Persistence.MarkFrameModified(row);
            host.Selection.SetSelection([row], row); host.CarCollisionEditing.RefreshOverlay(); host.RaiseSceneChanged(); _attached = true;
        }
        public void Undo()
        {
            ValidateUndo(); mutation.Detach(); host.Selection.Select(null); parent.Children.Remove(row);
            host.Persistence.MarkFrameModified(parent); host.CarCollisionEditing.RefreshOverlay(); host.RaiseSceneChanged(); _attached = false;
        }
    }

    private sealed class CompiledMutationEdit(D3DImageHost host, SceneNode node, IEditAction mutation) : INodeEdit
    {
        public IEnumerable<SceneNode> Nodes => [node];
        public void Undo() => Apply(undo: true);
        public void Redo() => Apply(undo: false);
        private void Apply(bool undo)
        {
            if (!host.Tree.IsInScene(node)) return;
            if (undo) mutation.Undo(); else mutation.Redo();
            host.Persistence.MarkFrameModified(node); host.CarCollisionEditing.RefreshOverlay();
            host.Selection.SetSelection([node], node); host.RaiseSelectionPropertiesChanged(); host.RaiseSceneChanged();
        }
        public void Discard() => mutation.Discard();
    }
}
