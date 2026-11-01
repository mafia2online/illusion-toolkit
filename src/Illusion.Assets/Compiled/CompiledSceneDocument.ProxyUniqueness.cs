using System.Buffers.Binary;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private IEditAction? IsolatePreparedProxyGeometry(CompiledFrameNode frame, CompiledElement visual, out string? refusal)
        => IsolatePreparedProxyGeometry(frame, visual, out refusal, out _);

    private IEditAction? IsolatePreparedProxyGeometry(CompiledFrameNode frame, CompiledElement visual, out string? refusal,
        out Action? validateMaterialCompositionUndo)
    {
        refusal = null; validateMaterialCompositionUndo = null; var actions = new List<IEditAction>(); var budgets = new List<IEditAction>();
        ProxyRuntimeState? state = null; ProxyPinsSnapshot? originalPins = null;
        var dirty = Graph.Resources.Select(resource => resource.CaptureDirtyCheckpoint()).ToArray(); long revision = _proxyStructuralRevision;
        var original = Graph.Nodes.Where(node => node.Event == 4).ToDictionary(node => node, node => Graph.Data(node).ToArray());
        var sizes = Graph.Resources.ToDictionary(resource => resource, ProxyGeometrySerializedSize);
        using var transaction = new ImportTransaction(this, null);
        try
        {
            state = _linkedProxyStates.Values.Single(candidate => candidate.Pairs.ContainsKey(frame));
            if (ValidateProxyMaterialSignatures(state) is { } material) throw new InvalidDataException(material);
            if (ValidateProxyInvariants(state) is { } invalid) throw new InvalidDataException(invalid);
            originalPins = CaptureProxyStatePins(state); var farFrame = state.Pairs[frame];
            var near = ProxyVisuals(frame).ToArray(); var far = ProxyVisuals(farFrame).ToArray(); int index = Array.IndexOf(near, visual);
            if (index < 0 || near.Length != far.Length) throw new InvalidDataException("Native proxy visual ownership no longer corresponds.");
            actions.Add(IsolateVisualGeometryCore(visual, true, out var reason) ?? throw new InvalidDataException(reason));
            actions.Add(IsolateVisualGeometryCore(far[index], true, out reason) ?? throw new InvalidDataException(reason));
            MarkOwnedProxyGeometry(state, visual); MarkOwnedProxyGeometry(state, far[index]);
            var changed = Graph.Nodes.Where(node => node.Event == 4 && (!original.TryGetValue(node, out var before) || !Graph.Data(node).SequenceEqual(before))).ToArray();
            var after = changed.ToDictionary(node => node, node => Graph.Data(node).ToArray());
            var grown = Graph.Resources.ToDictionary(resource => resource, resource => Math.Max(0, ProxyGeometrySerializedSize(resource) - sizes[resource]));
            ResetProxyInvariants(state); var newPins = CaptureProxyStatePins(state);
            foreach (var action in actions.AsEnumerable().Reverse()) action.Undo();
            RestoreProxyStatePins(state, originalPins);
            var before = changed.ToDictionary(node => node, node => Graph.Data(node).ToArray());
            foreach (var pair in grown.Where(pair => pair.Value != 0))
            {
                string folder = Path.GetDirectoryName(pair.Key.Path)!;
                if (!_vehicleResources.TryGetValue(folder, out var manager)) _vehicleResources.Add(folder, manager = new SdsResourceCarry(folder));
                budgets.Add(manager.AddGenericGrowthBudget(Path.GetFileName(pair.Key.Path), checked(pair.Value * 4 + 4096), out reason)
                    ?? throw new InvalidDataException(reason));
            }
            foreach (var action in actions) action.Redo(); RestoreProxyStatePins(state, newPins); AcceptProxyTransformState([state]);
            bool attached = true;
            var slotFields = EditableMaterialTables(frame.Element, visual).Concat(EditableMaterialTables(farFrame.Element, far[index])).ToHashSet();
            static bool OnlyMaterialGuidsChanged(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> current)
            {
                if (expected.Length != current.Length || expected.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(expected) != BinaryPrimitives.ReadUInt32LittleEndian(current)
                    || expected.Length != 4L + BinaryPrimitives.ReadUInt32LittleEndian(expected) * 40L) return false;
                for (int offset = 0; offset < expected.Length; offset++)
                    if ((offset < 12 || (offset - 12) % 40 >= 8) && expected[offset] != current[offset]) return false;
                return true;
            }
            ProxyRuntimeState Validate(bool forward, bool materialComposition = false)
            {
                if (!_linkedProxyStates.TryGetValue(state.Parent, out var current) || current.Child != state.Child
                    || !current.Pairs.TryGetValue(frame, out var owner) || !ReferenceEquals(owner, farFrame))
                    throw new EditRefusedException("The original paired geometry owners are no longer active.");
                if (ValidateProxyMaterialSignatures(current) is { } changedMaterial) throw new EditRefusedException(changedMaterial);
                if (ValidateProxyInvariants(current) is { } stale) throw new EditRefusedException(stale);
                if ((forward ? before : after).Any(pair => !Graph.Data(pair.Key).SequenceEqual(pair.Value)
                    && !(materialComposition && !forward && slotFields.Contains(pair.Key) && OnlyMaterialGuidsChanged(pair.Value, Graph.Data(pair.Key)))))
                    throw new EditRefusedException("Undo later native geometry edits before changing paired asset ownership.");
                return current;
            }
            var result = new ProxyGeometryAction(
                () =>
                {
                    if (attached) return; var current = Validate(true);
                    foreach (var budget in budgets) budget.Redo(); foreach (var action in actions) action.Redo();
                    RestoreProxyStatePins(current, newPins); AcceptProxyTransformState([current]); attached = true;
                },
                () =>
                {
                    if (!attached) return; var current = Validate(false);
                    foreach (var action in actions.AsEnumerable().Reverse()) action.Undo(); foreach (var budget in budgets.AsEnumerable().Reverse()) budget.Undo();
                    RestoreProxyStatePins(current, originalPins); AcceptProxyTransformState([current]); attached = false;
                }, () => Validate(true), () => Validate(false));
            validateMaterialCompositionUndo = () => Validate(false, materialComposition: true);
            transaction.Commit(); return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException or IOException or OverflowException)
        {
            foreach (var action in actions.AsEnumerable().Reverse()) action.Undo(); foreach (var budget in budgets.AsEnumerable().Reverse()) budget.Undo();
            foreach (var restore in dirty) restore(); _proxyStructuralRevision = revision;
            if (state is not null && originalPins is not null) RestoreProxyStatePins(state, originalPins);
            refusal = ex.Message; return null;
        }
    }
}
