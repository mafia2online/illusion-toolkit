using System.Security.Cryptography;
using Illusion.Assets.Bridge;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed record CompiledLinkedGeometryRequest(CompiledMeshBinding Binding, MeshObjectPayload Payload, int Lod);
public sealed record CompiledLinkedGeometryBatch(IReadOnlyList<BridgeMeshApplier.ApplyResult> Results, IEditAction Edit);

public sealed partial class CompiledSceneDocument
{
    public bool CanPlanLinkedProxyGeometry(CompiledMeshBinding binding) => _linkedProxyStates.Values.Any(state =>
        state.Pairs.Keys.Any(frame => ProxyVisuals(frame).Any(visual => ReferenceEquals(visual, binding.Visual))));
    /// <summary>Stages ordered native replacements as one atomic transaction. Individual result
    /// actions are owned by Edit; consumers use Results only for their precomputed GPU geometry.</summary>
    public CompiledLinkedGeometryBatch? PlanLinkedProxyGeometryBatch(IReadOnlyList<CompiledLinkedGeometryRequest> requests,
        out string? refusal)
    {
        refusal = null;
        if (requests.Count == 0) { refusal = "A native geometry batch needs at least one replacement."; return null; }
        var results = new List<BridgeMeshApplier.ApplyResult>(); int staged = 0;
        var revisions = Graph.Resources.Select(resource => resource.CaptureRevertedMutationCheckpoint()).ToArray();
        var bounds = NativeLods?.CaptureRevertedBoundsCheckpoint(); long structure = _proxyStructuralRevision;
        var rigBefore = _vehicleRigs.ToDictionary(pair => pair.Key, pair => pair.Value);
        var allowedTransformFields = _linkedProxyStates.Values.Where(state => requests.Any(request => state.Pairs.Keys
                .Any(frame => ProxyVisuals(frame).Any(visual => ReferenceEquals(visual, request.Binding.Visual)))))
            .SelectMany(state => state.ExpectedTransforms.Keys.Concat(state.ExpectedBounds.Keys)).Distinct().ToHashSet();
        byte[] Fingerprint(CompiledResource resource, IReadOnlyDictionary<CompiledElement, byte[]>? overrides = null)
        {
            var fields = allowedTransformFields.Where(field => ReferenceEquals(Graph.Owner(field), resource)).ToArray();
            if (fields.Length == 0) return SHA256.HashData(resource.ToArray());
            var rewrite = new CompiledRewrite { AllowNativeAllocatorFallback = true };
            if (overrides is not null) foreach (var field in fields) rewrite.Values[field] = overrides[field];
            return SHA256.HashData(resource.Rewrite(rewrite).ToArray());
        }
        var before = Graph.Resources.ToDictionary(resource => resource, resource => Fingerprint(resource));
        try
        {
            if (requests.Select(request => (request.Binding.Visual, request.Lod)).Distinct().Count() != requests.Count)
                throw new InvalidDataException("A native geometry batch cannot replace the same visual LOD twice.");
            foreach (var request in requests)
            {
                var owner = _frames.Values.SingleOrDefault(frame => _linkedProxyStates.Values.Any(state => state.Pairs.ContainsKey(frame))
                    && ProxyVisuals(frame).Any(visual => ReferenceEquals(visual, request.Binding.Visual)))
                    ?? throw new InvalidDataException("Every batch binding must belong to a prepared fine owner in this document.");
                _ = owner;
                var result = request.Binding.TryApply(request.Payload, request.Lod, out var reason)
                    ?? throw new InvalidDataException(reason);
                results.Add(result); result.ApplyNew(); staged++;
            }
            var after = Graph.Resources.ToDictionary(resource => resource, resource => Fingerprint(resource));
            var afterTransformFields = allowedTransformFields.ToDictionary(field => field, field => Graph.Data(field).ToArray());
            var rigAfter = _vehicleRigs.ToDictionary(pair => pair.Key, pair => pair.Value);
            var finalPrimary = requests.Select(request => request.Binding.DecodeLod(request.Lod)
                ?? throw new InvalidDataException("Missing final batch geometry.")).ToArray();
            var finalAdditional = results.SelectMany(result => result.AdditionalCompiledLodMeshes)
                .DistinctBy(output => (output.Binding.Visual, output.Lod))
                .Select(output => new BridgeMeshApplier.ApplyResult.CompiledLodMesh(output.Binding, output.Lod,
                    output.Binding.DecodeLod(output.Lod) ?? throw new InvalidDataException("Missing final counterpart geometry."))).ToArray();
            for (int index = staged - 1; index >= 0; index--) results[index].RestoreOriginal(); staged = 0;
            foreach (var restore in revisions) restore(); bounds?.Invoke(); _proxyStructuralRevision = structure;
            RestoreCache(rigBefore);
            var operations = results.ToArray(); bool applied = false;
            void Validate(bool forward, bool executing = false)
            {
                var expected = forward ? before : after;
                // Composite Undo preflights before its transform children restore their matrices.
                // Normalize only this batch's validated transform/bounds fields in an independent
                // rewrite; the executing native action still requires the exact original snapshot.
                var normalized = !forward && !executing ? afterTransformFields : null;
                if (Graph.Resources.Count() != expected.Count || expected.Any(pair => !Fingerprint(pair.Key, normalized).SequenceEqual(pair.Value)))
                    throw new EditRefusedException("Native geometry batch storage changed; restore intervening edits before replay.");
                foreach (var state in _linkedProxyStates.Values)
                {
                    if (ValidateProxyMaterialSignatures(state) is { } material) throw new EditRefusedException(material);
                    if (ValidateProxyInvariants(state) is { } invariant) throw new EditRefusedException(invariant);
                }
                if (forward) operations[0].ValidateNew(); else operations[^1].ValidateOriginal();
            }
            void Apply(bool forward)
            {
                if (applied == forward) return;
                Validate(forward, true);
                var rollback = Graph.Resources.Select(resource => resource.CaptureRevertedMutationCheckpoint()).ToArray();
                var rollbackBounds = NativeLods?.CaptureRevertedBoundsCheckpoint(); long revision = _proxyStructuralRevision;
                var cache = _vehicleRigs.ToDictionary(pair => pair.Key, pair => pair.Value);
                int completed = 0;
                try
                {
                    if (forward) foreach (var operation in operations) { operation.ApplyNew(); completed++; }
                    else foreach (var operation in operations.Reverse()) { operation.RestoreOriginal(); completed++; }
                    RestoreCache(forward ? rigAfter : rigBefore); applied = forward;
                }
                catch
                {
                    if (forward) foreach (var operation in operations.Take(completed).Reverse()) operation.RestoreOriginal();
                    else foreach (var operation in operations.Reverse().Take(completed).Reverse()) operation.ApplyNew();
                    foreach (var restore in rollback) restore(); rollbackBounds?.Invoke(); _proxyStructuralRevision = revision;
                    RestoreCache(cache); throw;
                }
            }
            var action = new ProxyGeometryAction(() => Apply(true), () => Apply(false),
                () => { if (!applied) Validate(true); }, () => { if (applied) Validate(false); });
            var display = operations.Select((operation, index) => new BridgeMeshApplier.ApplyResult
            {
                NewMesh = finalPrimary[index], Lod = requests[index].Lod, TouchedVertices = operation.TouchedVertices,
                CompiledBatchMember = true,
                CompiledTopologyRebuilt = operation.TopologyRebuilt, Requantized = operation.Requantized,
                AdditionalCompiledLodMeshes = index == 0 ? finalAdditional : [],
                CompiledWriteFields = index == 0 ? operations.SelectMany(item => item.CompiledWriteFields).Distinct().ToArray() : [],
                ApplyCompiled = index == 0 ? action.Redo : () => { }, RestoreCompiled = index == 0 ? action.Undo : () => { },
                ValidateCompiledNew = index == 0 ? action.ValidateRedo : null,
                ValidateCompiledOriginal = index == 0 ? action.ValidateUndo : null
            }).ToArray();
            return new(display, action);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
        { refusal = ex.Message; return null; }
        finally
        {
            for (int index = staged - 1; index >= 0; index--) results[index].RestoreOriginal();
            foreach (var restore in revisions) restore(); bounds?.Invoke(); _proxyStructuralRevision = structure;
            RestoreCache(rigBefore);
        }

        void RestoreCache(Dictionary<Illusion.Formats.Compiled.CompiledElement, CompiledVehicleRig?> values)
        {
            _vehicleRigs.Clear(); foreach (var pair in values) _vehicleRigs.Add(pair.Key, pair.Value);
        }
    }
}
