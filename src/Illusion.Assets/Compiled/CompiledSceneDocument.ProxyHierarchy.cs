using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private bool ProxyFineAncestrySupported(CompiledFrameNode fine)
    {
        var seen = new HashSet<CompiledFrameNode>();
        for (var parent = fine.ParentFrame; parent is not null; parent = parent.ParentFrame)
            if (!seen.Add(parent) || !ReferenceEquals(Graph.Owner(parent.Element), Graph.Owner(fine.Element))
                || Components(parent.Element).Count() != 1 || Components(parent.Element).Single().TypeId != 0x360b668d) return false;
        return true;
    }

    private bool ReparentPreparedLinkedProxy(CompiledFrameNode fine, ISceneSource? parentSource)
    {
        var state = _linkedProxyStates.Values.Single(s => s.Pairs.ContainsKey(fine));
        if (fine.Children.Count != 0 || !ProxyFineAncestrySupported(fine)
            || ValidateProxyMaterialSignatures(state) is not null || ValidateProxyInvariants(state) is not null) return false;
        CompiledFrameNode? parent = null;
        if (parentSource is not null && !ReferenceEquals(parentSource, this))
        {
            if (parentSource is not CompiledFrameNode p || !_frames.ContainsValue(p) || _deleted.Contains(p)
                || !ReferenceEquals(Graph.Owner(fine.Element), Graph.Owner(p.Element)) || !ProxyFineAncestrySupported(p)
                || Components(p.Element).Count() != 1 || Components(p.Element).Single().TypeId != 0x360b668d) return false;
            parent = p;
        }
        if (ReferenceEquals(parent, fine.ParentFrame)) return true;
        var far = state.Pairs[fine];
        Matrix4x4 world = fine.LocalTransform * (parent?.WorldTransform ?? Matrix4x4.Identity);
        IEditAction bounds;
        try { bounds = PrepareNativeLodBounds(new Dictionary<CompiledFrameNode, Matrix4x4> { [fine] = world, [far] = world }); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException) { return false; }
        var before = new Dictionary<CompiledElement, byte[]>();
        foreach (var field in new[] { Graph.Field(fine.Element, "m_Transform"), Graph.Field(far.Element, "m_Transform"), fine.LinkTransform,
            _links.TryGetValue(fine, out var saved) ? Graph.Field(saved.Entry, "First") : null }.OfType<CompiledElement>()) before.TryAdd(field, Graph.Data(field).ToArray());
        var oldParent = fine.ParentFrame; var oldLink = fine.LinkTransform;
        var oldChildren = oldParent?.Children.ToArray(); var newChildren = parent?.Children.ToArray();
        var transfers = new Dictionary<CompiledFrameNode, (CompiledElement Container, CompiledElement Registration)>(_rootTransfers);
        var pins = CaptureProxyStatePins(state); bool boundsApplied = false, committed = false;
        using var transaction = new ImportTransaction(this, parent);
        try
        {
            _proxyStructuralAuthorization.Add(fine); if (parent is not null) _proxyStructuralAuthorization.Add(parent);
            if (!ReparentCompiled(fine, parent ?? (ISceneSource)this)) return false;
            Graph.Owner(Graph.Field(far.Element, "m_Transform")!).Replace(Graph.Field(far.Element, "m_Transform")!, WriteMatrix(world));
            bounds.Redo(); boundsApplied = true;
            ResetProxyInvariants(state); transaction.Commit(); committed = true; return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
        { return false; }
        finally
        {
            _proxyStructuralAuthorization.Clear();
            if (!committed)
            {
                if (boundsApplied) bounds.Undo();
                foreach (var pair in before) Graph.Owner(pair.Key).Replace(pair.Key, pair.Value);
                fine.ParentFrame = oldParent; fine.LinkTransform = oldLink;
                if (oldParent is not null) { oldParent.Children.Clear(); oldParent.Children.AddRange(oldChildren!); }
                if (parent is not null) { parent.Children.Clear(); parent.Children.AddRange(newChildren!); }
                _rootTransfers.Clear(); foreach (var pair in transfers) _rootTransfers.Add(pair.Key, pair.Value);
                RestoreProxyStatePins(state, pins);
            }
        }
    }
}
