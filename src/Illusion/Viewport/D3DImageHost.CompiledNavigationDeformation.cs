using System.Numerics;
using Illusion.Assets.Compiled;
using Illusion.Scene;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    internal async Task<string?> DeformCompiledNavigationAsync(SceneNode node, int section,
        IReadOnlyDictionary<int, Vector3> edits, string? copiedExecutablePath = null)
    {
        if (IsLoading || BridgeEditedCount > 0) return "wait for loading and end the Blender session before editing navigation";
        var owner = node.Source is CompiledSceneDocument ? node : node.OwningDocumentNode();
        if (owner?.Source is not CompiledSceneDocument document) return "select a Mafia III document or one of its objects";
        byte[]? before = (uint)section < document.NavigationMeshes.Count ? document.NavigationMeshes[section].ToBytes() : null;
        var result = await document.DeformNavigationInteriorAsync(section, edits, copiedExecutablePath);
        if (result.Edit is null) return result.Refusal ?? "the native navigation cook was refused";
        if (!Tree.IsInScene(owner) || BridgeEditedCount > 0)
        {
            result.Edit.Undo();
            return "the editor changed while navigation was cooking; no edit was kept";
        }
        if (before is not null && before.AsSpan().SequenceEqual(document.NavigationMeshes[section].ToBytes()))
        {
            result.Edit.Undo();
            return null;
        }
        KeepCompiledMutation(owner, result.Edit);
        return null;
    }
}
