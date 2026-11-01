using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private sealed record ProxyTransformWrite(CompiledElement Field, byte[] Before, byte[] After);
    private sealed record ProxyTransformPlan(IReadOnlyList<ProxyTransformWrite> Writes, IEditAction? Bounds, IReadOnlyList<ProxyRuntimeState> States);

    /// <summary>Complete preflight for a transform. Prepared mappings authorize paired transforms only;
    /// all geometry, material, hierarchy and authored skeleton guards remain independent.</summary>
    public string? ValidateLinkedProxyTransform(IFrameNode source, Matrix4x4 local)
    {
        _ = PlanLinkedProxyTransform(source, local, out _, out string? refusal); return refusal;
    }

    /// <summary>Additional canonical roots whose viewer matrices must be refreshed after a fine transform.</summary>
    public IReadOnlyList<IFrameNode> GetLinkedProxyTransformTargets(IFrameNode source)
    {
        var canonical = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
        if (canonical is not CompiledFrameNode frame) return [];
        bool Affected(CompiledFrameNode fine)
        {
            var seen = new HashSet<CompiledFrameNode>();
            for (var current = fine; current is not null && seen.Add(current); current = current.ParentFrame)
                if (ReferenceEquals(current, frame)) return true;
            return false;
        }
        return _linkedProxyStates.Values.SelectMany(state => state.Pairs.Where(pair => Affected(pair.Key)).Select(pair => (IFrameNode)pair.Value)).Distinct().ToArray();
    }

    private ProxyTransformPlan? PlanLinkedProxyTransform(IFrameNode source, Matrix4x4 local, out bool linked, out string? refusal)
    {
        linked = false; refusal = null;
        try
        {
            string? original = ValidateSceneCollectionMutation(source);
            if (original is null) return null;
            linked = true;
            var canonical = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
            if (canonical is not CompiledFrameNode frame || !_frames.TryGetValue(frame.Element, out var stored) || !ReferenceEquals(frame, stored))
                throw new EditRefusedException(original);
            if (_linkedProxyStates.Values.Any(state => state.Pairs.Values.Contains(frame)))
                throw new EditRefusedException("Prepared coarse proxies follow their fine objects; edit the fine object instead.");
            var affected = new HashSet<CompiledFrameNode>();
            void Visit(CompiledFrameNode current) { if (affected.Add(current)) foreach (var child in current.Children) Visit(child); }
            Visit(frame);
            var sources = _linkedProxyStates.Values.SelectMany(state => state.Pairs.Keys).ToHashSet();
            if (affected.Any(current => ProxyVisuals(current).Any() && ValidateSceneCollectionMutation(current) is not null && !sources.Contains(current)))
                throw new EditRefusedException(original);
            var states = _linkedProxyStates.Values.Where(state => state.Pairs.Keys.Any(affected.Contains)).ToArray();
            if (states.Length == 0) throw new EditRefusedException(original);
            foreach (var state in states)
                if (ValidateProxyMaterialSignatures(state) is { } materialChanged) throw new EditRefusedException(materialChanged);
                else if (ValidateProxyInvariants(state) is { } changed) throw new EditRefusedException(changed);
            if (ValidateVehiclePlacementTransform(frame, local) is { } carRefusal) throw new EditRefusedException(carRefusal);
            if (local == frame.LocalTransform) return new([], null, states);
            var worlds = new Dictionary<CompiledFrameNode, Matrix4x4>();
            void Propose(CompiledFrameNode current, Matrix4x4 world)
            {
                if (world.M14 != 0 || world.M24 != 0 || world.M34 != 0 || world.M44 != 1 || !Matrix4x4.Invert(world, out _))
                    throw new EditRefusedException("Paired proxy transforms must be finite, affine and invertible.");
                _ = WriteMatrix(world); worlds.Add(current, world);
                foreach (var child in current.Children) Propose(child, child.LocalTransform * world);
            }
            Propose(frame, local * frame.ParentWorldTransform);
            foreach (var state in states)
                foreach (var pair in state.Pairs.Where(pair => affected.Contains(pair.Key)))
                {
                    if (pair.Value.ParentFrame is not null || pair.Value.Children.Count != 0)
                        throw new EditRefusedException("A prepared proxy is no longer a standalone render-only root.");
                    worlds.Add(pair.Value, worlds[pair.Key]);
                }
            var writes = new List<ProxyTransformWrite>();
            void Write(CompiledElement field, Matrix4x4 matrix)
            {
                byte[] before = Graph.Data(field).ToArray(), after = WriteMatrix(matrix);
                if (before.Length != 48) throw new InvalidDataException("Unknown native transform layout.");
                if (!before.AsSpan().SequenceEqual(after)) writes.Add(new(field, before, after));
            }
            if (frame.LinkTransform is { } link) Write(link, local);
            foreach (var pair in worlds) Write(Graph.Field(pair.Key.Element, "m_Transform")!, pair.Value);
            var bounds = PrepareNativeLodBounds(worlds); // Entire native output validated before the first write.
            return new(writes, bounds, states);
        }
        catch (Exception ex) when (ex is EditRefusedException or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        { refusal = ex.Message; return null; }
    }

    private bool TryApplyLinkedProxyTransform(CompiledFrameNode source, Matrix4x4 local, out string? refusal)
    {
        var plan = PlanLinkedProxyTransform(source, local, out bool linked, out refusal);
        if (!linked || plan is null) return false;
        var checkpoints = plan.Writes.Select(write => Graph.Owner(write.Field)).Concat(_nativeLodGroupFields.Values.Select(Graph.Owner))
            .Distinct().Select(resource => resource.CaptureDirtyCheckpoint()).ToArray();
        try
        {
            foreach (var write in plan.Writes) Graph.Owner(write.Field).Replace(write.Field, write.After);
            plan.Bounds?.Redo(); AcceptProxyTransformState(plan.States); return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or NotSupportedException)
        {
            plan.Bounds?.Undo();
            foreach (var write in plan.Writes.Reverse()) Graph.Owner(write.Field).Replace(write.Field, write.Before);
            foreach (var restore in checkpoints) restore();
            AcceptProxyTransformState(plan.States); refusal = ex.Message; return false;
        }
    }
}
