using Illusion.Assets.Bridge;
using Illusion.Assets.Sds;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private BridgeMeshApplier.ApplyResult? TryApplyLinkedProxyReplacement(CompiledMeshBinding binding, CompiledFrameNode frame,
        MeshObjectPayload payload, int lod, out string? refusal)
    {
        refusal = null;
        var isolations = new List<IEditAction>(); var native = new List<BridgeMeshApplier.ApplyResult>(); var budgets = new List<IEditAction>();
        IEditAction? bounds = null; bool boundsApplied = false;
        ProxyRuntimeState? state = null; ProxyPinsSnapshot? pinsBefore = null;
        var checkpoints = Graph.Resources.Select(resource => resource.CaptureRevertedMutationCheckpoint()).ToArray();
        long structuralBefore = _proxyStructuralRevision;
        var restoreStagedBounds = NativeLods?.CaptureRevertedBoundsCheckpoint();
        Dictionary<CompiledElement, byte[]> beforeData = [];
        HashSet<CompiledElement> beforeDefinitions = [];
        Dictionary<CompiledResource, long> resourceSizes = [];
        BridgeMeshApplier.ApplyResult Compute(CompiledMeshBinding source, CompiledFrameNode owner, MeshObjectPayload input, int level,
            System.Numerics.Vector3? origin = null, float scale = 0)
        {
            try { return source.ProxyStaticGeometry(input, level, origin, scale); }
            catch (InvalidDataException ex) when (ex.Message.Contains("shared", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("dedicated", StringComparison.OrdinalIgnoreCase))
            {
                var isolation = IsolateVisualGeometryCore(source.Visual, true, out var reason) ?? throw new InvalidDataException(reason);
                isolations.Add(isolation);
                return source.ProxyStaticGeometry(input, level, origin, scale);
            }
        }
        try
        {
            state = _linkedProxyStates.Values.SingleOrDefault(candidate => candidate.Pairs.ContainsKey(frame))
                ?? throw new InvalidDataException("A matching prepared native proxy family is required.");
            if (ValidateProxyMaterialSignatures(state) is { } material) throw new InvalidDataException(material);
            if (ValidateProxyInvariants(state) is { } stale) throw new InvalidDataException(stale);
            pinsBefore = CaptureProxyStatePins(state);
            beforeData = Graph.Nodes.Where(node => node.Event == 4).ToDictionary(node => node, node => Graph.Data(node).ToArray());
            beforeDefinitions = Graph.Nodes.Where(node => node.Event == 2 && node.ObjectId != 0).ToHashSet();
            resourceSizes = Graph.Resources.ToDictionary(resource => resource, ProxyGeometrySerializedSize);
            var farFrame = state.Pairs[frame]; var sourceVisuals = ProxyVisuals(frame).ToArray(); var farVisuals = ProxyVisuals(farFrame).ToArray();
            int ordinal = Array.IndexOf(sourceVisuals, binding.Visual);
            if (ordinal < 0 || sourceVisuals.Length != farVisuals.Length) throw new InvalidDataException("Native proxy visual correspondence changed.");
            var farVisual = farVisuals[ordinal];
            foreach (var visual in new[] { binding.Visual, farVisual })
                if (Graph.Nodes.Sum(node => Graph.Items(Graph.Field(node, "m_Visuals")).Count(item => ReferenceEquals(Graph.Resolve(item), visual))) != 1)
                    throw new InvalidDataException("A shared visual instance requires independent scene ownership before replacement.");
            _ = SelectProxyLowestGeometry([frame, farFrame]); // Strict static/morph/dependency layout validation, without allocation.
            var set = Graph.Resolve(Graph.Field(binding.Visual, "m_MeshSet"))!;
            foreach (var level in Graph.Items(Graph.Field(set, "m_LODs")))
            {
                var mesh = Graph.Resolve(Graph.Field(level, "m_Mesh")) ?? throw new InvalidDataException("Unresolved native proxy LOD.");
                if (ProxyReferencePresent(Graph.Field(mesh, "m_InternalMorphMapTextureData")) || ProxyReferencePresent(Graph.Field(mesh, "m_InternalMorphTextureData")))
                    throw new InvalidDataException("All proxy geometry LODs must be free of internal morph dependencies.");
                var flags = Graph.Field(mesh, "m_VFFlags");
                if (flags?.DataSize != 12) throw new InvalidDataException("Unknown native proxy vertex declaration table.");
                for (int stream = 0; stream < 3; stream++)
                {
                    var layout = CompiledMeshDecoder.Layout(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Graph.Data(flags)[(stream * 4)..]));
                    if (layout.Skin >= 0 || layout.Morph >= 0) throw new InvalidDataException("Skinned and morphing proxy LODs require independent authored envelopes.");
                }
            }
            var counterpart = new CompiledMeshBinding(Graph, farVisual, farFrame, farFrame.Name,
                level => DecodeVisual(farFrame, farVisual, new CompiledMeshDecoder(Graph, Library.StageCompanions.For(SourceArchive)), level),
                () => GeometryBind(farFrame, farVisual));
            int lowest = binding.LodCount - 1;
            var originalLowest = binding.DecodeLod(lowest) ?? throw new InvalidDataException("Missing native lowest geometry.");
            var originalFar = counterpart.DecodeLod(0) ?? throw new InvalidDataException("Missing native far geometry.");
            var originalSelected = binding.DecodeLod(lod) ?? throw new InvalidDataException("Missing selected native geometry.");
            MeshObjectPayload? farOriginalInput = lod == lowest ? RebaseProxyGeometryDonors(payload, originalLowest, originalFar) : null;
            var sourceInput = payload;
            if (!HasOwnedProxyGeometry(state, binding.Visual))
            {
                isolations.Add(IsolateVisualGeometryCore(binding.Visual, true, out var reason) ?? throw new InvalidDataException(reason));
                sourceInput = RebaseProxyGeometryDonors(payload, originalSelected, binding.DecodeLod(lod)!);
                MarkOwnedProxyGeometry(state, binding.Visual);
            }
            var primary = Compute(binding, frame, sourceInput, lod); native.Add(primary); primary.ApplyNew();
            MeshObjectPayload? farInput = null;
            if (lod == lowest) farInput = farOriginalInput;
            else if (primary.Requantized)
            {
                var exportedLowest = binding.DecodeLod(lowest)!;
                farInput = binding.Export(this, lowest, out var reason) ?? throw new InvalidDataException(reason);
                var rebuiltLowest = Compute(binding, frame, farInput, lowest, primary.NewDecompressionOffset, primary.NewDecompressionFactor);
                native.Add(rebuiltLowest); rebuiltLowest.ApplyNew();
                farInput = RebaseProxyGeometryDonors(farInput, exportedLowest, originalFar, true);
            }
            BridgeMeshApplier.ApplyResult? far = null;
            if (farInput is not null)
            {
                if (!HasOwnedProxyGeometry(state, farVisual))
                {
                    isolations.Add(IsolateVisualGeometryCore(farVisual, true, out var reason) ?? throw new InvalidDataException(reason));
                    farInput = RebaseProxyGeometryDonors(farInput, originalFar, counterpart.DecodeLod(0)!);
                    MarkOwnedProxyGeometry(state, farVisual);
                }
                far = Compute(counterpart, farFrame, farInput, 0, primary.NewDecompressionOffset, primary.NewDecompressionFactor);
                native.Add(far); far.ApplyNew();
                VerifyProxyRegeneratedCorners(binding.DecodeLod(lowest)!, counterpart.DecodeLod(0)!);
            }
            if (ExpandProxyRigidSkeletonEnvelope(frame, binding) is { } fineEnvelope) isolations.Add(fineEnvelope);
            if (far is not null && ExpandProxyRigidSkeletonEnvelope(farFrame, counterpart) is { } farEnvelope) isolations.Add(farEnvelope);
            bounds = PrepareNativeLodBounds(); bounds.Redo(); boundsApplied = true;
            var fields = native.SelectMany(result => result.CompiledWriteFields).Where(field => field.Event == 4)
                .Concat(Graph.Nodes.Where(field => field.Event == 4 && (!beforeData.TryGetValue(field, out var bytes) || !Graph.Data(field).SequenceEqual(bytes))))
                .Distinct().ToArray();
            if (_linkedProxyStates.Values.Any(other => !ReferenceEquals(other, state) && fields.Any(other.PinnedData.ContainsKey)))
                throw new InvalidDataException("The proposed native replacement writes storage owned by another prepared family.");
            var after = fields.ToDictionary(field => field, field => Graph.Data(field).ToArray());
            var newDefinitions = Graph.Nodes.Where(node => node.Event == 2 && node.ObjectId != 0 && !beforeDefinitions.Contains(node)).ToArray();
            var additional = new List<BridgeMeshApplier.ApplyResult.CompiledLodMesh>();
            if (primary.Requantized)
                for (int level = 0; level < binding.LodCount; level++) if (level != lod && binding.DecodeLod(level) is { } mesh)
                    additional.Add(new(binding, level, mesh));
            if (far?.NewMesh is { } farMesh) additional.Add(new(counterpart, 0, farMesh));
            ResetProxyInvariants(state); var pinsAfter = CaptureProxyStatePins(state);
            var sizesAfter = Graph.Resources.ToDictionary(resource => resource, ProxyGeometrySerializedSize);
            var stagedNative = native.ToArray();
            bounds.Undo(); boundsApplied = false;
            foreach (var result in native.AsEnumerable().Reverse()) result.RestoreOriginal(); native.Clear();
            foreach (var isolation in isolations.AsEnumerable().Reverse()) isolation.Undo();
            foreach (var restore in checkpoints) restore(); restoreStagedBounds?.Invoke(); _proxyStructuralRevision = structuralBefore;
            RestoreProxyStatePins(state, pinsBefore);
            var before = fields.ToDictionary(field => field, field => Graph.Data(field).ToArray());
            foreach (var pair in sizesAfter)
            {
                long growth = Math.Max(0, pair.Value - resourceSizes[pair.Key]); if (growth == 0) continue;
                string folder = Path.GetDirectoryName(pair.Key.Path)!;
                if (!_vehicleResources.TryGetValue(folder, out var manager)) _vehicleResources.Add(folder, manager = new SdsResourceCarry(folder));
                var reserve = manager.AddGenericGrowthBudget(Path.GetFileName(pair.Key.Path), checked(growth * 4 + 4096), out var reason)
                    ?? throw new InvalidDataException(reason);
                reserve.Undo(); budgets.Add(reserve);
            }
            var applyPrimary = primary.ApplyCompiled!; var undoPrimary = primary.RestoreCompiled!;
            var pairIdentity = state.Pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
            ProxyRuntimeState Validate(bool forward, bool executing = false)
            {
                if (!_linkedProxyStates.TryGetValue(state.Parent, out var active) || active.Child != state.Child
                    || !active.Pairs.TryGetValue(frame, out var currentFar) || !ReferenceEquals(currentFar, farFrame)
                    || active.Pairs.Count != pairIdentity.Count || pairIdentity.Any(pair => !active.Pairs.TryGetValue(pair.Key, out var target) || !ReferenceEquals(target, pair.Value)))
                    throw new InvalidDataException("The native geometry preparation no longer owns the original fine/far pair.");
                if (ValidateProxyMaterialSignatures(active) is { } materialChanged) throw new InvalidDataException(materialChanged);
                if (ValidateProxyInvariants(active) is { } invalid) throw new InvalidDataException(invalid);
                if (newDefinitions.Any(definition => forward ? Graph.Objects.ContainsKey(definition.ObjectId)
                    : !ReferenceEquals(Graph.Objects.GetValueOrDefault(definition.ObjectId), definition)))
                    throw new InvalidDataException("A retained native geometry identity is unavailable or no longer owned by this action.");
                var expected = forward ? before : after;
                var boxes = _nativeLodGroupFields.Values.ToHashSet();
                if (expected.Any(pair => !boxes.Contains(pair.Key) && !Graph.Data(pair.Key).SequenceEqual(pair.Value)))
                    throw new InvalidDataException("Native replacement storage changed after preflight.");
                if ((forward || executing) && (forward ? pinsBefore : pinsAfter).Transforms.Any(pair => !Graph.Data(pair.Key).SequenceEqual(pair.Value)))
                    throw new InvalidDataException("Restore the original object transforms before applying this geometry action.");
                return active;
            }
            void Apply(bool forward)
            {
                var active = Validate(forward, true);
                var undoDirty = Graph.Resources.Select(resource => resource.CaptureRevertedMutationCheckpoint()).ToArray();
                var rollbackBounds = NativeLods?.CaptureRevertedBoundsCheckpoint();
                long revision = _proxyStructuralRevision;
                var pins = CaptureProxyStatePins(active); int isolationCount = 0, operationCount = 0, budgetCount = 0; bool boundChanged = false;
                try
                {
                    if (forward)
                    {
                        foreach (var budget in budgets) { budget.Redo(); budgetCount++; }
                        foreach (var isolation in isolations) { isolation.Redo(); isolationCount++; }
                        applyPrimary(); operationCount++;
                        foreach (var operation in stagedNative.Skip(1)) { operation.ApplyNew(); operationCount++; }
                        bounds.Redo(); boundChanged = true; RestoreProxyStatePins(active, pinsAfter);
                    }
                    else
                    {
                        bounds.Undo(); boundChanged = true;
                        foreach (var operation in stagedNative.Skip(1).Reverse()) { operation.RestoreOriginal(); operationCount++; }
                        undoPrimary(); operationCount++;
                        foreach (var isolation in isolations.AsEnumerable().Reverse()) { isolation.Undo(); isolationCount++; }
                        foreach (var budget in budgets.AsEnumerable().Reverse()) { budget.Undo(); budgetCount++; }
                        RestoreProxyStatePins(active, pinsBefore);
                    }
                    AcceptProxyTransformState([active]); RefreshNativeLodRenderCoverage();
                }
                catch
                {
                    if (forward)
                    {
                        if (boundChanged) bounds.Undo();
                        foreach (var operation in stagedNative.Take(operationCount).Skip(1).Reverse()) operation.RestoreOriginal();
                        if (operationCount > 0) undoPrimary();
                        foreach (var isolation in isolations.Take(isolationCount).Reverse()) isolation.Undo();
                        foreach (var budget in budgets.Take(budgetCount).Reverse()) budget.Undo();
                    }
                    else
                    {
                        foreach (var budget in budgets.AsEnumerable().Reverse().Take(budgetCount).Reverse()) budget.Redo();
                        foreach (var isolation in isolations.AsEnumerable().Reverse().Take(isolationCount).Reverse()) isolation.Redo();
                        var reversed = stagedNative.Reverse().Take(operationCount).Reverse().ToArray();
                        foreach (var operation in reversed) { if (ReferenceEquals(operation, primary)) applyPrimary(); else operation.ApplyNew(); }
                        if (boundChanged) bounds.Redo();
                    }
                    foreach (var restore in undoDirty) restore(); rollbackBounds?.Invoke(); _proxyStructuralRevision = revision; RestoreProxyStatePins(active, pins); throw;
                }
            }
            primary.ApplyCompiled = () => Apply(true); primary.RestoreCompiled = () => Apply(false);
            primary.ValidateCompiledNew = () => Validate(true); primary.ValidateCompiledOriginal = () => Validate(false);
            primary.AdditionalCompiledLodMeshes = additional;
            primary.AdditionalCompiledBindings = far is null ? [] : [counterpart];
            // Native bounds hold one graph snapshot; competing bounds writers must be one explicit
            // aggregate transaction rather than partially applying two independently prepared results.
            primary.CompiledWriteFields = stagedNative.SelectMany(operation => operation.CompiledWriteFields)
                .Concat(_nativeLodGroupFields.Values).Concat(new[] { binding.Visual, farVisual }).Distinct().ToArray();
            return primary;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException or IOException)
        { refusal = ex.Message; return null; }
        finally
        {
            if (boundsApplied) bounds?.Undo();
            foreach (var result in native.AsEnumerable().Reverse()) result.RestoreOriginal();
            foreach (var isolation in isolations.AsEnumerable().Reverse()) isolation.Undo();
            foreach (var budget in budgets.AsEnumerable().Reverse()) budget.Undo();
            foreach (var restore in checkpoints) restore(); restoreStagedBounds?.Invoke(); _proxyStructuralRevision = structuralBefore;
            if (state is not null && pinsBefore is not null) RestoreProxyStatePins(state, pinsBefore);
        }
    }

    private static void VerifyProxyRegeneratedCorners(MeshData fine, MeshData far)
    {
        if (fine.Indices.Length != far.Indices.Length || fine.Parts.Length != far.Parts.Length
            || fine.Parts.Where((part, index) => part.MaterialHash != far.Parts[index].MaterialHash || part.IndexCount != far.Parts[index].IndexCount).Any())
            throw new InvalidDataException("Regenerated far topology or material ranges differ from native fine LOWEST.");
        for (int corner = 0; corner < fine.Indices.Length; corner++)
        {
            int a = checked((int)fine.Indices[corner]), b = checked((int)far.Indices[corner]);
            if (fine.Positions[a] != far.Positions[b] || fine.Normals[a] != far.Normals[b]
                || fine.UVs?[a] != far.UVs?[b] || fine.Colors?[a] != far.Colors?[b]
                || fine.Tangents?[a] != far.Tangents?[b] || fine.Binormals?[a] != far.Binormals?[b] || fine.DecalUVs?[a] != far.DecalUVs?[b])
                throw new InvalidDataException("Regenerated native far corner channels differ from fine LOWEST.");
        }
    }
}
