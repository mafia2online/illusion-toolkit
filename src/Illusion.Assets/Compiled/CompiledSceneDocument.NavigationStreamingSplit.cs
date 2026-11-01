using System.Numerics;
using System.Text.Json;
using Illusion.Domain;
using Illusion.Formats.Navigation;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    public async Task<NavigationDeformationResult> SplitNavigationStreamingEdgeAsync(int sectionIndex, int peerIndex, int edgeIndex,
        Vector3? position = null, string? copiedExecutablePath = null, CancellationToken cancellationToken = default, bool moveRepresentatives = false)
    {
        if ((uint)sectionIndex >= _navMeshes.Count || (uint)peerIndex >= _navMeshes.Count || sectionIndex == peerIndex)
            return new(null, "Select two distinct navigation sections in the same document.");
        try
        {
            var source = _navMeshes[sectionIndex]; var peer = _navMeshes[peerIndex]; byte[][] before = [source.ToBytes(), peer.ToBytes()];
            _ = source.PrepareStreamingEdgeSplit(peer, edgeIndex, position);
            byte[][] cooked = await NativeNavigationCooker.CookStreamingAsync(before, new Dictionary<int, Vector3>(), copiedExecutablePath,
                cancellationToken, moveRepresentatives: moveRepresentatives, splitEdge: edgeIndex, splitPoint: position);
            if (!source.ToBytes().AsSpan().SequenceEqual(before[0]) || !peer.ToBytes().AsSpan().SequenceEqual(before[1]))
                return new(null, "A navigation section changed during the stream split; retry the edit.");
            var edit = source.ApplyStreamingEdgeSplitCook(peer, edgeIndex, cooked[0], cooked[1], position, moveRepresentatives);
            RefreshNavigationMeshNodes(source); RefreshNavigationMeshNodes(peer);
            return new(new NavigationStreamGrowthEdit(this, source, peer, edit), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException or
            UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        { return new(null, ex.Message); }
    }
    private sealed class NavigationStreamGrowthEdit(CompiledSceneDocument owner, Mafia3NavMeshFile source, Mafia3NavMeshFile peer,
        Mafia3NavigationStreamingGrowthEdit edit) : IEditAction
    {
        private void Refresh() { owner.RefreshNavigationMeshNodes(source); owner.RefreshNavigationMeshNodes(peer); }
        public void Undo() { edit.Undo(); Refresh(); }
        public void Redo() { edit.Redo(); Refresh(); }
    }
}
