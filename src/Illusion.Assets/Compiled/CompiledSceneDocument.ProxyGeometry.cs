using Illusion.Assets.Bridge;
using Illusion.Bridge.Payload;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    public sealed partial class CompiledFrameNode
    {
        internal bool HasPreparedLinkedGeometryOwner => _document._linkedProxyStates.Values.Any(state => state.Pairs.ContainsKey(this));
        internal BridgeMeshApplier.ApplyResult? ApplyLinkedGeometry(CompiledMeshBinding binding, MeshObjectPayload payload,
            int lod, out string? refusal) => _document.TryApplyLinkedProxyGeometry(binding, this, payload, lod, out refusal);
    }

    private BridgeMeshApplier.ApplyResult? TryApplyLinkedProxyGeometry(CompiledMeshBinding binding, CompiledFrameNode frame,
        MeshObjectPayload payload, int lod, out string? refusal)
    {
        refusal = null;
        try
        {
            var state = _linkedProxyStates.Values.SingleOrDefault(s => s.Pairs.ContainsKey(frame))
                ?? throw new InvalidDataException("Prepare this linked proxy family before editing its static attributes.");
            if (ValidateProxyMaterialSignatures(state) is { } material) throw new InvalidDataException(material);
            if (ValidateProxyInvariants(state) is { } stale) throw new InvalidDataException(stale);
            var fineVisuals = ProxyVisuals(frame).ToArray(); var farFrame = state.Pairs[frame];
            var farVisuals = ProxyVisuals(farFrame).ToArray();
            int ordinal = Array.IndexOf(fineVisuals, binding.Visual);
            if (ordinal < 0 || fineVisuals.Length != farVisuals.Length) throw new InvalidDataException("Prepared visual correspondence changed.");
            var farVisual = farVisuals[ordinal];
            foreach (var visual in new[] { binding.Visual, farVisual })
                if (Graph.Nodes.Sum(node => Graph.Items(Graph.Field(node, "m_Visuals")).Count(item => ReferenceEquals(Graph.Resolve(item), visual))) != 1)
                    throw new InvalidDataException("Prepared attribute editing requires a visual owned by exactly one render component.");
            var counterpart = new CompiledMeshBinding(Graph, farVisual, farFrame, farFrame.Name,
                level => DecodeVisual(farFrame, farVisual, new CompiledMeshDecoder(Graph, Library.StageCompanions.For(SourceArchive)), level),
                () => GeometryBind(farFrame, farVisual));
            int lowest = binding.LodCount - 1;
            var near = binding.RestrictedProxyAttributes(payload, lod);
            BridgeMeshApplier.ApplyResult? far = null;
            if (lod != lowest)
            {
                var protectedStorage = binding.ProxyLodStorage(lowest).Concat(counterpart.ProxyLodStorage(0)).ToHashSet();
                if (near.CompiledWriteFields.Any(protectedStorage.Contains))
                    throw new InvalidDataException("This higher LOD shares storage with the native lowest proxy LOD.");
            }
            else
            {
                var baseline = binding.Export(this, lod, out var error) ?? throw new InvalidDataException(error);
                var target = counterpart.Export(this, 0, out error) ?? throw new InvalidDataException(error);
                if (!baseline.Positions.SequenceEqual(target.Positions) || !baseline.LoopVertexIndices.SequenceEqual(target.LoopVertexIndices)
                    || !baseline.LoopOrigIndex.SequenceEqual(target.LoopOrigIndex) || !baseline.LoopNormals.SequenceEqual(target.LoopNormals)
                    || !baseline.LoopUvs.SequenceEqual(target.LoopUvs) || !baseline.FaceMaterials.SequenceEqual(target.FaceMaterials)
                    || !baseline.LoopColors.SequenceEqual(target.LoopColors)
                    || baseline.ExtraLoopUvs.Count != target.ExtraLoopUvs.Count
                    || baseline.ExtraLoopUvs.Any(p => !target.ExtraLoopUvs.TryGetValue(p.Key, out var values) || !p.Value.SequenceEqual(values)))
                    throw new InvalidDataException("The fine and far native corner attributes no longer correspond exactly.");
                target.LoopNormals = payload.LoopNormals; target.LoopUvs = payload.LoopUvs;
                target.LoopColors = payload.LoopColors; target.ExtraLoopUvs = payload.ExtraLoopUvs;
                far = counterpart.RestrictedProxyAttributes(target, 0);
                if (near.ConflictsWithCompiledWrites(far)) throw new InvalidDataException("Fine and far attribute storage aliases.");
            }
            var fields = near.CompiledWriteFields.Concat(far?.CompiledWriteFields ?? []).Distinct().Where(f => f.Event == 4).ToArray();
            if (_linkedProxyStates.Values.Any(other => !ReferenceEquals(other, state) && fields.Any(other.PinnedData.ContainsKey)))
                throw new InvalidDataException("Attribute storage is protected by another prepared proxy family.");
            var before = fields.ToDictionary(f => f, f => Graph.Data(f).ToArray());
            Dictionary<CompiledElement, byte[]>? after = null;
            var applyNear = near.ApplyCompiled!; var restoreNear = near.RestoreCompiled!;
            (Dictionary<CompiledElement, byte[]> Expected, ProxyRuntimeState Active) Validate(bool forward)
            {
                // Preparation undo/redo deliberately replaces runtime state while retaining canonical
                // native frames. Never validate or accept pins on a detached state captured by history.
                if (!_linkedProxyStates.TryGetValue(state.Parent, out var active) || active.Child != state.Child
                    || active.Pairs.Count != state.Pairs.Count
                    || state.Pairs.Any(pair => !active.Pairs.TryGetValue(pair.Key, out var target) || !ReferenceEquals(target, pair.Value)))
                    throw new InvalidDataException("The paired attribute preparation is no longer active with the same native owners.");
                if (ValidateProxyMaterialSignatures(active) is { } materialChanged) throw new InvalidDataException(materialChanged);
                if (ValidateProxyInvariants(active) is { } changed) throw new InvalidDataException(changed);
                var expected = forward ? before : after ?? throw new InvalidDataException("The paired attribute action has not been applied.");
                if (expected.Any(p => !Graph.Data(p.Key).SequenceEqual(p.Value))) throw new InvalidDataException("The paired attribute action is stale.");
                return (expected, active);
            }
            void Apply(bool forward)
            {
                var (expected, active) = Validate(forward);
                var checkpoints = fields.Select(Graph.Owner).Distinct().Select(r => r.CaptureDirtyCheckpoint()).ToArray();
                try
                {
                    if (forward) { applyNear(); far?.ApplyNew(); }
                    else { far?.RestoreOriginal(); restoreNear(); }
                    if (forward) after = fields.ToDictionary(f => f, f => Graph.Data(f).ToArray());
                    // Only storage fields validated by the restricted writer may become new invariant values.
                    foreach (var field in fields) if (active.PinnedData.ContainsKey(field)) active.PinnedData[field] = ProxyCurrentData(field);
                    active.AcceptRevisions();
                }
                catch
                {
                    foreach (var pair in expected) Graph.Owner(pair.Key).Replace(pair.Key, pair.Value);
                    foreach (var checkpoint in checkpoints) checkpoint();
                    throw;
                }
            }
            near.ApplyCompiled = () => Apply(true); near.RestoreCompiled = () => Apply(false);
            near.ValidateCompiledNew = () => Validate(true);
            near.ValidateCompiledOriginal = () => Validate(false);
            near.AdditionalCompiledBindings = far is null ? [] : [counterpart];
            if (far?.NewMesh is { } farMesh) near.AdditionalCompiledMeshes = new Dictionary<CompiledMeshBinding, Illusion.Domain.MeshData> { [counterpart] = farMesh };
            // Both write sets participate in bridge multi-selection conflict rejection.
            near.CompiledWriteFields = near.CompiledWriteFields.Concat(far?.CompiledWriteFields ?? []).Distinct().ToArray();
            return near;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        { refusal = ex.Message; return null; }
    }
}
