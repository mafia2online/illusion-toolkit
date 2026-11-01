using Illusion.Domain;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Captures removal of fine static roots together with their generated far owners.
    /// The final pair cannot be removed: an empty native family needs a separate group teardown.</summary>
    public IFrameDetachment? CapturePreparedLinkedProxyDetachment(IReadOnlyList<IFrameNode> frames)
    {
        var requested = frames.OfType<CompiledFrameNode>().Distinct().ToArray();
        if (requested.Length == 0 || requested.Length != frames.Distinct().Count()) return null;
        var fine = requested.Where(f => _linkedProxyStates.Values.Any(s => s.Pairs.ContainsKey(f))).ToArray();
        if (fine.Length == 0 || fine.Any(f => f.Children.Count != 0 || !CanDuplicatePreparedLinkedProxy(f))
            || requested.Except(fine).Any(f => Components(f.Element).Count() != 1 || Components(f.Element).Single().TypeId != 0x360b668d)
            || requested.Any(f => f.Children.Any(child => !requested.Contains(child)))) return null;
        var states = _linkedProxyStates.Values.Where(s => fine.Any(s.Pairs.ContainsKey)).ToArray();
        if (states.Length != 1 || fine.Any(f => !states[0].Pairs.ContainsKey(f))) return null;
        var state = states[0];
        if (state.Pairs.Count <= fine.Length || ValidateProxyMaterialSignatures(state) is not null || ValidateProxyInvariants(state) is not null) return null;
        var pairs = fine.ToDictionary(f => f, f => state.Pairs[f]);
        var all = requested.Concat(pairs.Values).ToHashSet();
        try
        {
            var plans = BuildDeletionPlans(_deleted.Concat(all).ToHashSet());
            foreach (var (resource, plan) in plans) resource.Rewrite(plan);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException) { return null; }
        return new PreparedProxyDetachment(this, state.Parent, state.Child, pairs, all);
    }

    private sealed class PreparedProxyDetachment(CompiledSceneDocument document, ulong parent, ulong child,
        Dictionary<CompiledFrameNode, CompiledFrameNode> pairs, HashSet<CompiledFrameNode> frames) : IFrameDetachment
    {
        private bool _detached;
        private Dictionary<ulong, string>? _dependencies;
        private ProxyPinsSnapshot? _removedPins;
        private readonly List<(CompiledFrameNode Parent, CompiledFrameNode Child, int Index)> _boundary = [];
        public IReadOnlyList<IFrameNode> AdditionalFrames => pairs.Values.Cast<IFrameNode>().ToArray();
        private ProxyRuntimeState Active(bool attached)
        {
            if (!document._linkedProxyStates.TryGetValue(parent, out var state) || state.Child != child
                || pairs.Any(pair => state.Pairs.ContainsKey(pair.Key) != attached
                    || attached && !ReferenceEquals(state.Pairs[pair.Key], pair.Value)))
                throw new EditRefusedException("The removed proxy pair's prepared family changed.");
            if (ValidateProxyMaterialSignatures(state) is { } material) throw new EditRefusedException(material);
            if (document.ValidateProxyInvariants(state) is { } invariant) throw new EditRefusedException(invariant);
            return state;
        }
        public void ValidateDetach()
        {
            if (_detached) return;
            var state = Active(true);
            if (state.Pairs.Count <= pairs.Count) throw new EditRefusedException("Keep at least one native fine/coarse pair in this family.");
        }
        public void ValidateReattach()
        {
            if (!_detached) return; var state = Active(false);
            if (_dependencies!.Any(p => CompiledProxyMaterialGuard.TextureSignature(p.Key) != p.Value)
                || _removedPins!.Data.Any(p => !state.PinnedData.ContainsKey(p.Key) && !document.Graph.Data(p.Key).SequenceEqual(p.Value)))
                throw new EditRefusedException("The detached proxy's native fields or material dependencies changed.");
        }
        private void RestoreBoundary()
        {
            foreach (var (owner, frame, index) in _boundary.OrderBy(entry => entry.Index))
                if (!owner.Children.Contains(frame)) owner.Children.Insert(Math.Clamp(index, 0, owner.Children.Count), frame);
            _boundary.Clear();
        }
        public void Detach()
        {
            if (_detached) return; ValidateDetach(); var state = Active(true);
            _dependencies ??= new(state.MaterialTextures);
            var pins = CaptureProxyStatePins(state); _removedPins ??= pins;
            try
            {
                foreach (var frame in frames)
                    if (frame.ParentFrame is { } owner && !frames.Contains(owner))
                    { _boundary.Add((owner, frame, owner.Children.IndexOf(frame))); owner.Children.Remove(frame); }
                document._deleted.UnionWith(frames); foreach (var pair in pairs) state.Pairs.Remove(pair.Key);
                document._effectivePlans = null; document._proxyStructuralRevision++;
                document.ResetProxyInvariants(state); _detached = true;
            }
            catch
            {
                document._deleted.ExceptWith(frames); foreach (var pair in pairs) state.Pairs[pair.Key] = pair.Value;
                RestoreBoundary();
                document._effectivePlans = null; document.RestoreProxyStatePins(state, pins); throw;
            }
        }
        public void Reattach()
        {
            if (!_detached) return; ValidateReattach(); var state = Active(false);
            var pins = CaptureProxyStatePins(state);
            try
            {
                document._deleted.ExceptWith(frames); foreach (var pair in pairs) state.Pairs.Add(pair.Key, pair.Value);
                foreach (var (owner, frame, index) in _boundary.OrderBy(entry => entry.Index))
                    if (!owner.Children.Contains(frame)) owner.Children.Insert(Math.Clamp(index, 0, owner.Children.Count), frame);
                document._effectivePlans = null; document._proxyStructuralRevision++;
                document.ResetProxyInvariants(state); _boundary.Clear(); _detached = false;
            }
            catch
            {
                document._deleted.UnionWith(frames); foreach (var pair in pairs) state.Pairs.Remove(pair.Key);
                foreach (var (owner, frame, _) in _boundary) owner.Children.Remove(frame);
                document._effectivePlans = null; document.RestoreProxyStatePins(state, pins); throw;
            }
        }
    }
}
