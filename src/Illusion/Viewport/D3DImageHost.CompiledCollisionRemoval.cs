using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Scene;

namespace Illusion.Viewport;

public partial class D3DImageHost
{
    internal string? RemoveCompiledCollision(SceneNode node) => RemoveCompiledCollisions([node]);

    // Stage the native mutations together: a refused volume must not leave an earlier selection deleted.
    private string? RemoveCompiledCollisions(IReadOnlyList<SceneNode> nodes)
    {
        if (IsLoading || BridgeEditedCount > 0) return "wait for loading and end the Blender session before removing collision";
        var staged = new List<(SceneNode Parent, SceneNode Row, int Index, IEditAction Mutation)>();
        string? failure = null;
        try
        {
            foreach (var node in nodes.Distinct())
            {
                if (node.Source is not IFrameNode source || node.OwningDocumentNode()?.Source is not CompiledSceneDocument document
                    || node.Parent is not { } parent) { failure = "select a compiled collision volume"; break; }
                var mutation = document.RemoveCollision(source, out var refusal);
                if (mutation is null) { failure = refusal ?? "the collision volume could not be removed"; break; }
                staged.Add((parent,node,parent.Children.IndexOf(node),mutation));
            }
        }
        catch
        {
            foreach (var item in staged.AsEnumerable().Reverse()) { item.Mutation.Undo(); item.Mutation.Discard(); }
            throw;
        }
        if (failure is not null)
        {
            foreach (var item in staged.AsEnumerable().Reverse()) { item.Mutation.Undo(); item.Mutation.Discard(); }
            return failure;
        }
        foreach (var item in staged)
        {
            item.Parent.Children.Remove(item.Row);
            History.Push(new RemovedCompiledCollisionEdit(this,item.Parent,item.Row,item.Index,item.Mutation));
            Persistence.MarkFrameModified(item.Parent);
        }
        if (staged.Count > 0) { Selection.Select(null); CarCollisionEditing.RefreshOverlay(); RaiseSceneChanged(); }
        return null;
    }

    private sealed class RemovedCompiledCollisionEdit(D3DImageHost host, SceneNode parent, SceneNode row,
        int index, IEditAction mutation) : INodeEdit
    {
        public IEnumerable<SceneNode> Nodes => [parent];
        public void Undo()
        {
            mutation.Undo(); parent.Children.Insert(Math.Min(index, parent.Children.Count), row);
            host.Selection.SetSelection([row], row); Changed();
        }
        public void Redo()
        {
            mutation.Redo(); parent.Children.Remove(row); host.Selection.Select(null); Changed();
        }
        private void Changed()
        {
            host.Persistence.MarkFrameModified(parent); host.CarCollisionEditing.RefreshOverlay(); host.RaiseSceneChanged();
        }
        public void Discard() => mutation.Discard();
    }
}
