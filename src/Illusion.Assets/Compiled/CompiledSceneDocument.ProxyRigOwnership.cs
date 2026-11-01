using System.Buffers.Binary;
using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Edits an existing rigid rest bone on independently identified native immutable
    /// skeleton copies. Original skeleton GUIDs remain unchanged for unloaded native users.</summary>
    public IEditAction? SetPreparedLinkedProxyBoneTransform(IFrameNode source, int bone, Matrix4x4 local, out string? refusal)
        => EditPreparedLinkedProxyRig(source, () =>
        {
            var action = SetPreparedLinkedProxyBoneTransformCore(source, bone, local, out var reason);
            return (action, reason);
        }, out refusal);

    private IEditAction? EditPreparedLinkedProxyRig(IFrameNode source,
        Func<(IEditAction? Action, string? Refusal)> mutate, out string? refusal)
    {
        refusal = null;
        if (InspectPreparedLinkedProxyBones(source, out refusal).Count == 0) return null;
        var fine = (CompiledFrameNode)(source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source);
        var state = _linkedProxyStates.Values.Single(s => s.Pairs.ContainsKey(fine));
        var far = state.Pairs[fine]; var originalPins = CaptureProxyStatePins(state);
        var originalCache = CapturePairRigCache();
        var checkpoints = Graph.Resources.Select(r => r.CaptureRevertedMutationCheckpoint()).ToArray();
        var edits = new List<IEditAction>(); var copies = new List<GeometryCopy>();
        var pointers = new List<(CompiledElement Field, byte[] Before, byte[] After)>();
        var ownedBefore = new Dictionary<CompiledElement, byte[]>();
        var originals = new Dictionary<CompiledElement, byte[]>(); IEditAction? rest = null;
        try
        {
            foreach (var frame in new[] { fine, far })
            {
                var component = Components(frame.Element).Single(c => c.TypeId == 0x0fa73185);
                var reference = Graph.Field(component, "m_ImmutableData")!;
                var immutable = Graph.Resolve(reference)!;
                foreach (var element in Descendants(immutable).Where(e => e.Event == 4))
                    originals.TryAdd(element, Graph.Data(element).ToArray());
                if (reference.Event == 2 && reference.Values.Count() > 1) edits.Add(ExternalizeProxyInlineGeometry([reference]));
                var pointer = reference.Event == 2 ? reference.Values.First() : reference;
                var before = Graph.Data(pointer).ToArray(); var copy = new GeometryCopy(this); copies.Add(copy);
                var clone = copy.CloneDefinition(immutable, false); copy.Finish();
                foreach (var element in Descendants(clone).Where(e => e.Event == 4))
                    ownedBefore.Add(element, Graph.Data(element).ToArray());
                var after = before.ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(after, clone.ObjectId);
                Graph.Owner(pointer).Replace(pointer, after); pointers.Add((pointer, before, after));
            }
            foreach (var resource in pointers.Select(p => Graph.Owner(p.Field)).Distinct())
            {
                string folder = Path.GetDirectoryName(resource.Path)!;
                if (!_vehicleResources.TryGetValue(folder, out var manager)) _vehicleResources.Add(folder, manager = new SdsResourceCarry(folder));
                edits.Add(manager.AddGenericBudget(Path.GetFileName(resource.Path), [folder], out refusal) ?? throw new NotSupportedException(refusal));
            }
            ResetProxyInvariants(state);
            var mutation = mutate(); rest = mutation.Action ?? throw new NotSupportedException(mutation.Refusal);
            var ownedCache = CapturePairRigCache(); bool applied = true;
            void Validate(bool forward)
            {
                if (!_linkedProxyStates.TryGetValue(state.Parent, out var active) || active.Child != state.Child
                    || !active.Pairs.TryGetValue(fine, out var paired) || !ReferenceEquals(paired, far))
                    throw new EditRefusedException("The prepared rigid skeleton owners changed.");
                if (ValidateProxyMaterialSignatures(active) is { } material) throw new EditRefusedException(material);
                if (ValidateProxyInvariants(active) is { } invariant) throw new EditRefusedException(invariant);
                if (pointers.Any(p => !Graph.Data(p.Field).SequenceEqual(forward ? p.Before : p.After))
                    || originals.Any(p => !Graph.Data(p.Key).SequenceEqual(p.Value))
                    || forward && ownedBefore.Any(p => !Graph.Data(p.Key).SequenceEqual(p.Value)))
                    throw new EditRefusedException("An original or copied native skeleton reference changed.");
                if (!forward) rest.ValidateUndo();
            }
            return new ProxyGeometryAction(
                () =>
                {
                    if (applied) return; Validate(true);
                    var currentState = _linkedProxyStates[state.Parent]; var previousPins = CaptureProxyStatePins(currentState);
                    var restoreResources = Graph.Resources.Select(resource => resource.CaptureRevertedMutationCheckpoint()).ToArray();
                    try
                    {
                        foreach (var edit in edits) edit.Redo(); foreach (var copy in copies) copy.Reattach();
                        foreach (var p in pointers) Graph.Owner(p.Field).Replace(p.Field, p.After);
                        ownedCache(); ResetProxyInvariants(currentState);
                        rest.Redo(); applied = true;
                    }
                    catch
                    {
                        foreach (var p in pointers) Graph.Owner(p.Field).Replace(p.Field, p.Before);
                        foreach (var copy in copies.AsEnumerable().Reverse()) copy.Detach();
                        foreach (var edit in edits.AsEnumerable().Reverse()) edit.Undo(); originalCache();
                        foreach (var restore in restoreResources) restore(); RestoreProxyStatePins(currentState, previousPins); throw;
                    }
                },
                () =>
                {
                    if (!applied) return; Validate(false); rest.Undo();
                    foreach (var p in pointers) Graph.Owner(p.Field).Replace(p.Field, p.Before);
                    foreach (var copy in copies.AsEnumerable().Reverse()) copy.Detach();
                    foreach (var edit in edits.AsEnumerable().Reverse()) edit.Undo();
                    originalCache(); ResetProxyInvariants(_linkedProxyStates[state.Parent]); applied = false;
                }, () => { if (!applied) Validate(true); }, () => { if (applied) Validate(false); });
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
        {
            rest?.Undo();
            foreach (var p in pointers) Graph.Owner(p.Field).Replace(p.Field, p.Before);
            foreach (var copy in copies.AsEnumerable().Reverse()) copy.Detach();
            foreach (var edit in edits.AsEnumerable().Reverse()) edit.Undo();
            originalCache(); foreach (var checkpoint in checkpoints) checkpoint(); RestoreProxyStatePins(state, originalPins);
            refusal = ex.Message; return null;
        }

        Action CapturePairRigCache()
        {
            var snapshot = new[] { fine.Element, far.Element }.Select(element =>
                (Element: element, Found: _vehicleRigs.TryGetValue(element, out var rig), Rig: rig)).ToArray();
            return () =>
            {
                foreach (var entry in snapshot)
                    if (entry.Found) _vehicleRigs[entry.Element] = entry.Rig;
                    else _vehicleRigs.Remove(entry.Element);
            };
        }
    }
}
