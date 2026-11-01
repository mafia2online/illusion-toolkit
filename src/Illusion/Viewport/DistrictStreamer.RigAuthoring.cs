using Illusion.Assets.Compiled;
using Illusion.Assets.Frames;
using Illusion.Domain;
using Illusion.Scene;

namespace Illusion.Viewport;

internal sealed partial class DistrictStreamer
{
    private sealed record RigTreeSwap(SceneNode Frame, SceneNode? Before, SceneNode? After, int Index);

    /// <summary>Stages native skeleton rows, current adapter inventory and overlay GPU together.
    /// Mesh rows remain canonical. The returned action is not installed until Redo.</summary>
    internal IEditAction PrepareCompiledRigAuthoringRefresh(SceneNode owner, IReadOnlySet<IFrameNode> targets,
        IReadOnlyList<SceneNode>? stagedRoots = null)
    {
        if (owner.Source is not CompiledSceneDocument document || _host.Rnd is null)
            throw new InvalidOperationException("A live compiled document is required for native skeleton refresh.");
        static IEnumerable<SceneNode> Walk(SceneNode row)
        { yield return row; foreach (var child in row.Children) foreach (var nested in Walk(child)) yield return nested; }
        var key = owner;
        for (var ancestor = owner; ancestor is not null; ancestor = ancestor.Parent)
            if (_rigs.ContainsKey(ancestor)) { key = ancestor; break; }
        var beforeRigs = _rigs.GetValueOrDefault(key); var beforePicks = _helperPicks.GetValueOrDefault(key);
        var detached=(stagedRoots??[]).SelectMany(Walk).ToHashSet();
        var rows = Walk(key).Concat((stagedRoots??[]).SelectMany(Walk)).Distinct()
            .Where(row=>row.Source is CompiledSceneDocument.CompiledFrameNode).GroupBy(row=>row.Source)
            .Select(group=>group.OrderBy(row=>row.Kind is "Mesh" or "Lod"?1:0).First()).ToArray();
        var nativeFrames = rows.Select(row => (CompiledSceneDocument.CompiledFrameNode)row.Source!).Distinct().ToArray();
        var afterRigs = (beforeRigs ?? []).Where(rig => rig.OwnerFrame is not CompiledSceneDocument.CompiledFrameNode frame || !nativeFrames.Contains(frame))
            .Concat(nativeFrames.Select(document.VehicleSkeleton).OfType<SkeletonData>()).ToList();
        var swaps = new List<RigTreeSwap>();
        foreach (var row in rows.Where(row => targets.Contains((IFrameNode)row.Source!)))
        {
            var old = row.Children.SingleOrDefault(child => child.Kind == "Skeleton");
            var skeleton = document.VehicleSkeleton((CompiledSceneDocument.CompiledFrameNode)row.Source!);
            var next = skeleton is null ? null : SceneTree.BuildSkeletonTree(skeleton);
            if (next is not null && old is not null)
            {
                next.IsExpanded = old.IsExpanded; next.IsVisible = old.IsVisible;
                var expanded = Walk(old).Where(bone => bone.Kind == "Bone").ToDictionary(bone => CompiledVehicleRig.BoneIndex(bone.Source), bone => bone.IsExpanded);
                foreach (var bone in Walk(next).Where(bone => bone.Kind == "Bone"))
                    if (expanded.TryGetValue(CompiledVehicleRig.BoneIndex(bone.Source), out bool value)) bone.IsExpanded = value;
            }
            swaps.Add(new(row, old, next, old is null ? row.Children.Count : row.Children.IndexOf(old)));
        }
        var overlay = _host.Rnd.PrepareSkeletonDistrict(key, HelperGlyphBuilder.BuildRig(afterRigs));
        return new RigAuthoringRefresh(this, key, beforeRigs, afterRigs, beforePicks, swaps, overlay, detached);
    }

    private sealed class RigAuthoringRefresh(DistrictStreamer streamer, SceneNode key, List<SkeletonData>? beforeRigs, List<SkeletonData> afterRigs,
        List<(SceneNode Node, System.Numerics.Vector3 At, float Radius)>? beforePicks, IReadOnlyList<RigTreeSwap> swaps, IEditAction overlay,
        IReadOnlySet<SceneNode> staged) : IEditAction
    {
        private bool _applied;
        private void Validate(bool forward)
        {
            foreach (var swap in swaps)
            {
                var expected = forward ? swap.Before : swap.After;
                if (!streamer._host.Tree.IsInScene(swap.Frame) && !(forward && staged.Contains(swap.Frame)) || expected is not null && !swap.Frame.Children.Contains(expected)
                    || expected is null && swap.Frame.Children.Any(child => child.Kind == "Skeleton"))
                    throw new EditRefusedException("The native skeleton tree changed outside this recorded rig edit.");
            }
            if (forward) overlay.ValidateRedo(); else overlay.ValidateUndo();
        }
        public void ValidateUndo() { if (_applied) Validate(false); }
        public void ValidateRedo() { if (!_applied) Validate(true); }
        private void Swap(bool forward)
        {
            if (_applied == forward) return; Validate(forward);
            var host = streamer._host;
            SceneNode Remap(SceneNode node)
            {
                foreach (var swap in swaps)
                {
                    var from = forward ? swap.Before : swap.After; var to = forward ? swap.After : swap.Before;
                    if (from is null || !SceneTree.IsSelfOrDescendantOf(node, from)) continue;
                    if (ReferenceEquals(node, from)) return to ?? swap.Frame;
                    int index = CompiledVehicleRig.BoneIndex(node.Source);
                    if (to is null || index < 0) return swap.Frame;
                    var pending = new Stack<SceneNode>(); pending.Push(to);
                    while (pending.TryPop(out var candidate))
                    {
                        if (candidate.Kind == "Bone" && CompiledVehicleRig.BoneIndex(candidate.Source) == index) return candidate;
                        foreach (var child in candidate.Children) pending.Push(child);
                    }
                    return swap.Frame;
                }
                return node;
            }
            var selected = host.Selection.Selected.Select(Remap).Distinct().ToArray(); var active = host.Selection.Active is { } current ? Remap(current) : null;
            if (forward) overlay.Redo(); else overlay.Undo();
            foreach (var swap in swaps)
            {
                var from = forward ? swap.Before : swap.After; var to = forward ? swap.After : swap.Before;
                if (from is not null) swap.Frame.Children.Remove(from);
                if (to is not null)
                {
                    if (to.Parent is null) { swap.Frame.AddChild(to); swap.Frame.Children.Move(swap.Frame.Children.Count - 1, Math.Min(swap.Index, swap.Frame.Children.Count - 1)); }
                    else swap.Frame.InsertChild(swap.Index, to);
                }
            }
            if (forward) streamer._rigs[key] = afterRigs;
            else if (beforeRigs is not null) streamer._rigs[key] = beforeRigs; else streamer._rigs.Remove(key);
            if (forward) streamer._helperPicks[key] = CollectGlyphPicks(key);
            else if (beforePicks is not null) streamer._helperPicks[key] = beforePicks; else streamer._helperPicks.Remove(key);
            host.Selection.SetSelection(selected, active); _applied = forward;
        }
        public void Redo() => Swap(true);
        public void Undo() => Swap(false);
        public void Discard() => overlay.Discard();
    }
}
