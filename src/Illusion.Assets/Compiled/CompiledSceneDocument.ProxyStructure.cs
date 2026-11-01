using Illusion.Assets.Sds;
using Illusion.Domain;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private interface ICompiledDuplicationMap { IReadOnlyDictionary<ulong, ulong> SceneIds { get; } }
    // Only entered after complete prepared-family validation, never exposed as an editing mode.
    private readonly HashSet<CompiledFrameNode> _proxyStructuralAuthorization = [];

    private (CompiledFrameNode Root, ProxyRuntimeState State, CompiledFrameNode[] Leaves, CompiledFrameNode[] Nodes)? PreparedDuplicateSource(IFrameNode source)
    {
        var canonical = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
        if (canonical is not CompiledFrameNode root || !_frames.ContainsValue(root) || !ProxyFineAncestrySupported(root)) return null;
        var nodes = new HashSet<CompiledFrameNode>(); var leaves = new List<CompiledFrameNode>();
        bool Visit(CompiledFrameNode frame)
        {
            if (!nodes.Add(frame) || _deleted.Contains(frame) || !ReferenceEquals(Graph.Owner(root.Element), Graph.Owner(frame.Element))) return false;
            var components = Components(frame.Element).ToArray();
            if (components.Length == 1 && components[0].TypeId == 0x360b668d) return frame.Children.All(Visit);
            if (frame.Children.Count != 0 || components.Any(c => c.TypeId is not (0x0fa73185 or 0x2da22400)) || !StaticGeometry(frame)) return false;
            leaves.Add(frame); return true;
        }
        if (!Visit(root) || leaves.Count == 0) return null;
        var states = _linkedProxyStates.Values.Where(state => leaves.Any(state.Pairs.ContainsKey)).ToArray();
        return states.Length == 1 && leaves.All(states[0].Pairs.ContainsKey) ? (root, states[0], leaves.ToArray(), nodes.ToArray()) : null;
    }
    public bool CanDuplicatePreparedLinkedProxy(IFrameNode source) => PreparedDuplicateSource(source) is not null;

    /// <summary>Copies a prepared static fine subtree and each generated coarse root with independent
    /// native geometry, IDs and owning registrations. All roots belong to the already-applied token.</summary>
    public IFrameDuplication? DuplicatePreparedLinkedProxy(IFrameNode source, out string? refusal)
    {
        refusal = null;
        if (PreparedDuplicateSource(source) is not { } selection)
        { refusal = "Choose a prepared render-only static fine root or pure Kit subtree."; return null; }
        var state = selection.State;
        if ((refusal = ValidateProxyMaterialSignatures(state) ?? ValidateProxyInvariants(state)) is not null) return null;
        IFrameDuplication? nearCopy = null; var farCopies = new List<IFrameDuplication>(); var budgets = new List<IEditAction>();
        var pairs = new Dictionary<CompiledFrameNode, CompiledFrameNode>(); var pins = CaptureProxyStatePins(state);
        using var transaction = new ImportTransaction(this, selection.Root.ParentFrame);
        try
        {
            _proxyStructuralAuthorization.UnionWith(selection.Nodes); _proxyStructuralAuthorization.UnionWith(selection.Leaves.Select(f => state.Pairs[f]));
            nearCopy = DuplicateCore(selection.Root, out refusal) ?? throw new NotSupportedException(refusal);
            var ids = ((ICompiledDuplicationMap)nearCopy).SceneIds;
            foreach (var original in selection.Leaves)
            {
                var farCopy = DuplicateCore(state.Pairs[original], out refusal) ?? throw new NotSupportedException(refusal);
                farCopies.Add(farCopy);
                pairs.Add(_frames[Graph.Objects[ids[original.Element.ObjectId]]], (CompiledFrameNode)farCopy.Root.Source!);
            }
            foreach (var resource in pairs.Keys.Concat(pairs.Values).Select(f => Graph.Owner(f.Element)).Distinct())
            {
                string folder = Path.GetDirectoryName(resource.Path)!;
                if (!_vehicleResources.TryGetValue(folder, out var manager)) _vehicleResources.Add(folder, manager = new SdsResourceCarry(folder));
                budgets.Add(manager.AddGenericBudget(Path.GetFileName(resource.Path), new[] { folder }, out refusal) ?? throw new NotSupportedException(refusal));
            }
            var result = new PreparedProxyDuplication(this, state.Parent, state.Child, nearCopy, farCopies, pairs, budgets);
            result.AdoptApplied(); transaction.Commit(); return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException or IOException or OverflowException)
        {
            foreach (var budget in budgets.AsEnumerable().Reverse()) budget.Undo();
            foreach (var copy in farCopies.AsEnumerable().Reverse()) copy.Detach(); nearCopy?.Detach();
            foreach (var pair in pairs) { state.Pairs.Remove(pair.Key); RemoveProxyCopiedMembership(pair.Key); RemoveProxyCopiedMembership(pair.Value); }
            RestoreProxyStatePins(state, pins); refusal = ex.Message; return null;
        }
        finally { _proxyStructuralAuthorization.Clear(); }
    }

    private void RemoveProxyCopiedMembership(CompiledFrameNode frame)
    {
        foreach (var key in _nativeLodVisuals.Keys.Where(key => ReferenceEquals(key.Frame, frame.Element)).ToArray()) _nativeLodVisuals.Remove(key);
    }
    private void AttachProxyCopiedMembership(CompiledFrameNode frame, ulong group)
    {
        var membership = _nativeLodVisuals.Values.FirstOrDefault(m => m.Graph.Groups[m.Group].Id == group)
            ?? throw new InvalidDataException("The copied native LOD membership is unavailable.");
        foreach (var visual in ProxyVisuals(frame)) _nativeLodVisuals[(frame.Element, visual)] = membership;
        IEnumerable<ISceneHierarchyNode> Walk(ISceneHierarchyNode row)
        { yield return row; foreach (var child in row.Children) foreach (var descendant in Walk(child)) yield return descendant; }
        foreach (var row in Roots.SelectMany(Walk).Where(row => (row.GeometrySource ?? row.Source) is ICompiledMeshSource source && ReferenceEquals(source.CanonicalFrame, frame)))
        {
            if (row.Mesh is { } single) single.NativeLod = membership;
            foreach (var lod in row.LodMeshes) lod.NativeLod = membership;
        }
    }

    private sealed class PreparedProxyDuplication(CompiledSceneDocument document, ulong parent, ulong child,
        IFrameDuplication near, IReadOnlyList<IFrameDuplication> far, Dictionary<CompiledFrameNode, CompiledFrameNode> pairs,
        IReadOnlyList<IEditAction> budgets) : IFrameDuplication
    {
        private bool _attached;
        public ISceneHierarchyNode Root => near.Root;
        public IReadOnlyList<ISceneHierarchyNode> AdditionalRoots => far.Select(copy => copy.Root).ToArray();
        private ProxyRuntimeState Active(bool expectedAttached)
        {
            if (!document._linkedProxyStates.TryGetValue(parent, out var state) || state.Child != child
                || pairs.Any(pair => state.Pairs.ContainsKey(pair.Key) != expectedAttached
                    || expectedAttached && !ReferenceEquals(state.Pairs[pair.Key], pair.Value)))
                throw new EditRefusedException("The paired duplicate's prepared family changed.");
            if (ValidateProxyMaterialSignatures(state) is { } material) throw new EditRefusedException(material);
            if (document.ValidateProxyInvariants(state) is { } invariant) throw new EditRefusedException(invariant);
            return state;
        }
        private void Register(ProxyRuntimeState state)
        {
            foreach (var pair in pairs)
            {
                state.Pairs.Add(pair.Key, pair.Value);
                document.AttachProxyCopiedMembership(pair.Key, child); document.AttachProxyCopiedMembership(pair.Value, parent);
            }
            document.ResetProxyInvariants(state); document.RefreshNativeLodRenderCoverage(); _attached = true;
        }
        internal void AdoptApplied() => Register(document._linkedProxyStates[parent]);
        public void ValidateDetach()
        {
            if (!_attached) return; _ = Active(true); near.ValidateDetach(); foreach (var copy in far) copy.ValidateDetach();
        }
        public void ValidateReattach()
        {
            if (_attached) return; _ = Active(false); near.ValidateReattach(); foreach (var copy in far) copy.ValidateReattach();
        }
        public void Reattach()
        {
            if (_attached) return; ValidateReattach(); var state = Active(false); var pins = CaptureProxyStatePins(state);
            try
            {
                near.Reattach(); foreach (var copy in far) copy.Reattach(); foreach (var budget in budgets) budget.Redo(); Register(state);
            }
            catch
            {
                foreach (var budget in budgets.Reverse()) budget.Undo(); foreach (var copy in far.Reverse()) copy.Detach(); near.Detach();
                foreach (var pair in pairs) { state.Pairs.Remove(pair.Key); document.RemoveProxyCopiedMembership(pair.Key); document.RemoveProxyCopiedMembership(pair.Value); }
                document.RestoreProxyStatePins(state, pins); throw;
            }
        }
        public void Detach()
        {
            if (!_attached) return; ValidateDetach(); var state = Active(true); var pins = CaptureProxyStatePins(state);
            try
            {
                foreach (var copy in far.Reverse()) copy.Detach(); near.Detach(); foreach (var budget in budgets.Reverse()) budget.Undo();
                foreach (var pair in pairs) { state.Pairs.Remove(pair.Key); document.RemoveProxyCopiedMembership(pair.Key); document.RemoveProxyCopiedMembership(pair.Value); }
                document.ResetProxyInvariants(state); document.RefreshNativeLodRenderCoverage(); _attached = false;
            }
            catch
            {
                near.Reattach(); foreach (var copy in far) copy.Reattach(); foreach (var budget in budgets) budget.Redo();
                foreach (var pair in pairs) { state.Pairs[pair.Key] = pair.Value; document.AttachProxyCopiedMembership(pair.Key, child); document.AttachProxyCopiedMembership(pair.Value, parent); }
                document.RestoreProxyStatePins(state, pins); throw;
            }
        }
    }
}
