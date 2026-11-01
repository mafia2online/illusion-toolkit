using System.Numerics;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Scene;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    internal Task<string?> DeformCompiledNavigationAsync(SceneNode node, int section,
        IReadOnlyDictionary<int, Vector3> edits, string? copiedExecutablePath = null)
        => CookCompiledNavigationAsync(node, section, document => document.DeformNavigationInteriorAsync(section, edits, copiedExecutablePath));

    internal Task<string?> DeformCompiledNavigationStreamingAsync(SceneNode node, int section, int peer,
        IReadOnlyDictionary<int, Vector3> edits, string? copiedExecutablePath = null, bool moveRepresentatives = false)
        => CookCompiledNavigationAsync(node, section, document => document.DeformNavigationStreamingAsync(section, peer, edits, copiedExecutablePath, moveRepresentatives: moveRepresentatives));

    internal Task<string?> SplitCompiledNavigationStreamingAsync(SceneNode node, int section, int peer, int edge,
        Vector3? position = null, string? copiedExecutablePath = null, bool moveRepresentatives = false)
        => CookCompiledNavigationAsync(node, section, document => document.SplitNavigationStreamingEdgeAsync(section, peer, edge, position, copiedExecutablePath, moveRepresentatives: moveRepresentatives));

    internal Task<string?> FlipCompiledNavigationEdgeAsync(SceneNode node, int section, int edge, string? copiedExecutablePath = null)
        => CookCompiledNavigationAsync(node, section, document => document.FlipNavigationEdgeAsync(section, edge, copiedExecutablePath));
    internal Task<string?> SubdivideCompiledNavigationFaceAsync(SceneNode node, int section, int face, Vector3? position, string? copiedExecutablePath = null)
        => CookCompiledNavigationAsync(node, section, document => document.SubdivideNavigationFaceAsync(section, face, position, copiedExecutablePath));
    internal Task<string?> ExtrudeCompiledNavigationBoundaryAsync(SceneNode node, int section, int edge, Vector3? position, string? copiedExecutablePath = null)
        => CookCompiledNavigationAsync(node, section, document => document.ExtrudeNavigationBoundaryAsync(section, edge, position, copiedExecutablePath));

    private async Task<string?> CookCompiledNavigationAsync(SceneNode node, int section,
        Func<CompiledSceneDocument, Task<CompiledSceneDocument.NavigationDeformationResult>> cook)
    {
        if (IsLoading || BridgeEditedCount > 0) return "wait for loading and end the Blender session before editing navigation";
        var owner = node.Source is CompiledSceneDocument ? node : node.OwningDocumentNode();
        if (owner?.Source is not CompiledSceneDocument document) return "select a Mafia III document or one of its objects";
        byte[]? before = (uint)section < document.NavigationMeshes.Count ? document.NavigationMeshes[section].ToBytes() : null;
        var counts = (uint)section < document.NavigationMeshes.Count
            ? document.NavigationMeshes[section].Meshes.Select(m => (m.Vertices.Count, m.FaceCount, m.EdgeCount)).ToArray() : [];
        var result = await cook(document);
        if (result.Edit is null) return result.Refusal ?? "the native navigation cook was refused";
        if (IsLoading || !Tree.IsInScene(owner) || BridgeEditedCount > 0)
        {
            result.Edit.Undo();
            return "the editor changed while navigation was cooking; no edit was kept";
        }
        if (before is not null && before.AsSpan().SequenceEqual(document.NavigationMeshes[section].ToBytes()))
        {
            result.Edit.Undo();
            return null;
        }
        bool resized = (uint)section < document.NavigationMeshes.Count && !counts.SequenceEqual(document.NavigationMeshes[section].Meshes.Select(m => (m.Vertices.Count, m.FaceCount, m.EdgeCount)));
        IEditAction action = result.Edit;
        if (resized)
        {
            RefreshNavigationRows(owner, document);
            action = new NavigationMetadataEdit(this, owner, document, action);
        }
        KeepCompiledMutation(owner, action);
        return null;
    }
    private void RefreshNavigationRows(SceneNode owner, CompiledSceneDocument document)
    {
        var source = document.Roots.FirstOrDefault(n => n.Kind == "Navigation" && n.Name == "AI navigation");
        var previous = owner.Children.FirstOrDefault(n => n.Kind == "Navigation" && n.Name == "AI navigation");
        if (source is null || previous is null) return;
        int index = owner.Children.IndexOf(previous);
        bool selected = Selection.Selected.Any(n => SceneTree.IsSelfOrDescendantOf(n, previous));
        owner.Children.RemoveAt(index);
        var row = SceneTree.BuildSceneTree(source, []); row.IsExpanded = previous.IsExpanded;
        owner.AddChild(row); owner.Children.Move(owner.Children.Count - 1, index);
        if (selected) Selection.SetSelection([owner], owner);
    }
    private sealed class NavigationMetadataEdit(D3DImageHost host, SceneNode owner, CompiledSceneDocument document, IEditAction inner) : IEditAction
    {
        public void Undo() { inner.Undo(); host.RefreshNavigationRows(owner, document); }
        public void Redo() { inner.Redo(); host.RefreshNavigationRows(owner, document); }
        public void Discard() => inner.Discard();
    }
}
