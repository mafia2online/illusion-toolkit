using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Scene;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    internal string? InspectCompiledLinkedProxy(SceneNode node, out CompiledLinkedProxyPlan? plan)
    {
        plan = null;
        if (IsLoading || Rnd is null) return "Wait for the scene to finish loading.";
        if (node.Source is not IFrameNode frame || node.OwningDocumentNode()?.Source is not CompiledSceneDocument document)
            return "Select a compiled scene object belonging to a linked coarse/fine family.";
        plan = document.InspectLinkedProxyPreparation(frame, out var refusal);
        return plan is null ? refusal ?? "This object has no supported linked proxy family." : null;
    }

    internal void RefreshLinkedProxyMeshes(SceneNode node)
    {
        if (Rnd is null || node.Source is not IFrameNode source
            || node.OwningDocumentNode()?.Source is not CompiledSceneDocument document) return;
        var targets = document.GetLinkedProxyTransformTargets(source).ToHashSet();
        if (targets.Count == 0) return;
        foreach (var mesh in Rnd.Meshes)
            if (mesh.Owner is SceneNode { Source: IFrameNode frame })
            {
                var canonical = frame is ICompiledMeshSource visual ? visual.CanonicalFrame : frame;
                if (targets.Contains(canonical)) mesh.SetWorld(frame.WorldTransform);
            }
    }

    // Both editors and MCP use the same reviewed plan and undoable viewport transaction.
    internal string? PrepareCompiledLinkedProxy(SceneNode node, CompiledLinkedProxyPlan plan)
    {
        if (IsLoading || Rnd is null || BridgeEditedCount > 0) return "Wait for loading and end the Blender session before preparing a proxy.";
        if (node.OwningDocumentNode() is not { Source: CompiledSceneDocument document } documentNode)
            return "Select a compiled scene object.";
        var before = document.Roots.ToArray();
        var mutation = document.PrepareLinkedProxyTransforms(plan, out var refusal);
        if (mutation is null) return refusal ?? "The linked proxy could not be prepared.";
        var createdMeshes = new List<GpuMesh>();
        try
        {
            var removed = before.Except(document.Roots).Single();
            IEnumerable<SceneNode> Walk(SceneNode root)
            { yield return root; foreach (var child in root.Children) foreach (var row in Walk(child)) yield return row; }
            var oldRow = Walk(documentNode).First(row => ReferenceEquals(row.Source, removed.Source));
            var parent = oldRow.Parent ?? throw new InvalidOperationException("The coarse proxy has no scene parent.");
            var rows = new List<SceneNode>();
            foreach (var native in document.Roots.Except(before))
            {
                var leaves = new List<SceneNode>();
                var row = SceneTree.BuildSceneTree(native, leaves); rows.Add(row);
                foreach (var leaf in leaves)
                    if (leaf.Pending is { } pending)
                    {
                        var mesh = Rnd.CreateMeshGpu(pending); createdMeshes.Add(mesh);
                        mesh.Owner = leaf; leaf.Mesh = mesh; leaf.Pending = null;
                    }
            }
            var oldMeshes = Walk(oldRow).Select(row => row.Mesh).OfType<GpuMesh>().Distinct().ToArray();
            var edit = new LinkedProxyDisplayEdit(this, documentNode, parent, oldRow, rows.ToArray(), oldMeshes, createdMeshes.ToArray(), Rnd.Meshes.ToArray(), mutation);
            edit.Install(); History.Push(edit); return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            mutation.Undo(); foreach (var mesh in createdMeshes) mesh.Dispose();
            return "The prepared proxy could not be displayed: " + ex.Message;
        }
    }

    private sealed class LinkedProxyDisplayEdit(D3DImageHost host, SceneNode documentNode, SceneNode parent,
        SceneNode original, SceneNode[] prepared, GpuMesh[] before, GpuMesh[] after, GpuMesh[] originalOrder, IEditAction mutation) : INodeEdit
    {
        private bool _applied;
        private readonly int _originalIndex = parent.Children.IndexOf(original);
        private GpuMesh[]? _preparedOrder;
        public IEnumerable<SceneNode> Nodes => [documentNode];
        internal void Install() => Swap(true);
        private void Swap(bool apply)
        {
            var removed = apply ? before : after; var added = apply ? after : before;
            host.Selection.Select(null);
            host.Rnd?.DetachMeshes(removed);
            foreach (var row in prepared) parent.Children.Remove(row);
            parent.Children.Remove(original);
            if (apply) foreach (var row in prepared) parent.AddChild(row);
            else
            {
                parent.AddChild(original);
                parent.Children.Move(parent.Children.IndexOf(original), Math.Min(_originalIndex, parent.Children.Count - 1));
            }
            foreach (var mesh in added) host.Rnd?.AttachMesh(mesh);
            if (host.Rnd is { } renderer)
            {
                if (apply && _preparedOrder is null) _preparedOrder = renderer.Meshes.ToArray();
                renderer.RestoreMeshOrder(apply ? _preparedOrder! : originalOrder);
            }
            host.Tree.MeshCount += added.Length - removed.Length;
            host.Persistence.MarkFrameModified(documentNode);
            host.RaiseSceneChanged(); host.RaiseSelectionPropertiesChanged(); _applied = apply;
        }
        public void Undo() { if (!_applied) return; mutation.Undo(); Swap(false); }
        public void Redo() { if (_applied) return; mutation.Redo(); Swap(true); }
        public void Discard()
        { mutation.Discard(); foreach (var mesh in _applied ? before : after) mesh.Dispose(); }
    }
}
