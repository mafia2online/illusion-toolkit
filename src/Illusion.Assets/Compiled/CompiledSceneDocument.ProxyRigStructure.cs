using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Appends one unused native bone without renumbering any existing palette or attachment.</summary>
    public IEditAction? AddPreparedLinkedProxyBone(IFrameNode source, string name, int parent, Matrix4x4 local, float reach, out string? refusal)
        => EditPreparedLinkedProxyRig(source, () =>
        {
            var action = EditPreparedRigTables(source, rig =>
            {
                if (rig.Count >= 255 || parent < 0 || parent >= rig.Count) throw new NotSupportedException("The appended bone needs an existing parent and the native skeleton must remain below 256 bones.");
                if (string.IsNullOrWhiteSpace(name) || name.Contains('\0') || rig.Find(name) >= 0 || !CompiledMeshBinding.IsRigidBind(local)
                    || !float.IsFinite(reach) || reach <= 0) throw new ArgumentException("Bone name, rigid local transform or positive reach is invalid.");
                var values = new Dictionary<CompiledElement, byte[]>();
                void Append(string fieldName, byte[] tail)
                {
                    var field = Graph.Field(rig.Immutable, fieldName)!; var bytes = Graph.Data(field).ToArray();
                    var next = new byte[checked(bytes.Length + tail.Length)]; bytes.CopyTo(next, 0); tail.CopyTo(next, bytes.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(next, checked((uint)rig.Count + 1)); values.Add(field, next);
                }
                if (!Matrix4x4.Invert(local * rig.ModelRest(parent), out var inverse)) throw new InvalidDataException("Singular appended model bind.");
                Append("m_OrigBoneMatrices", WriteMatrix(local)); Append("m_BoneInvMatrices", WriteMatrix(inverse));
                byte[] parentBytes = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(parentBytes, checked((ushort)parent)); Append("m_ParentIndices", parentBytes);
                byte[] box = new byte[24]; for (int i = 0; i < 6; i++) BinaryPrimitives.WriteSingleLittleEndian(box.AsSpan(i * 4), i < 3 ? -reach : reach);
                Append("m_BoneAABBs", box);
                var names = Graph.Field(rig.Immutable, "m_BoneNames")!; var items = Graph.Items(names);
                var resource = Graph.Owner(names); var item = resource.CloneSubtree(items[0], new Dictionary<ulong, ulong>());
                byte[] hash = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(hash, Fnv64.Hash(name, Encoding.UTF8)); resource.SetClonedValue(item, hash);
                CompiledVehicleRig.RememberAuthoredName(name);
                Graph.RegisterSubtree(resource, item); Graph.SetSubtreeActive(resource, item, false);
                var count = names.Values.First(); byte[] countBytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(countBytes, checked((uint)rig.Count + 1)); values.Add(count, countBytes);
                return (values, new Dictionary<CompiledElement, CompiledElement[]> { [names] = [.. names.Children, item] });
            }, out var reason);
            return (action, reason);
        }, out refusal);

    /// <summary>Reparents one existing native bone while preserving its model-space bind matrix.
    /// Parent-first native iteration requires an earlier bone or the root sentinel (-1).</summary>
    public IEditAction? ReparentPreparedLinkedProxyBone(IFrameNode source, int bone, int parent, out string? refusal)
        => EditPreparedLinkedProxyRig(source, () =>
        {
            var action = EditPreparedRigTables(source, rig =>
            {
                if ((uint)bone >= rig.Count || parent < -1 || parent >= bone) throw new NotSupportedException("Native bone parenting requires an earlier parent index or -1 for a root.");
                var local = rig.ModelRest(bone);
                if (parent >= 0)
                {
                    if (!Matrix4x4.Invert(rig.ModelRest(parent), out var inverse)) throw new InvalidDataException("Singular native parent bind.");
                    local *= inverse;
                }
                if (!CompiledMeshBinding.IsRigidBind(local)) throw new NotSupportedException("Reparenting would require an unsupported non-rigid local bind.");
                var parents = Graph.Field(rig.Immutable, "m_ParentIndices")!; var rest = Graph.Field(rig.Immutable, "m_OrigBoneMatrices")!;
                var parentBytes = Graph.Data(parents).ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(parentBytes.AsSpan(4 + bone * 2), parent < 0 ? (ushort)255 : checked((ushort)parent));
                var restBytes = Graph.Data(rest).ToArray(); WriteMatrix(local).CopyTo(restBytes, 4 + bone * 48);
                return (new Dictionary<CompiledElement, byte[]> { [parents] = parentBytes, [rest] = restBytes }, new Dictionary<CompiledElement, CompiledElement[]>());
            }, out var reason);
            return (action, reason);
        }, out refusal);

    private IEditAction? EditPreparedRigTables(IFrameNode source,
        Func<CompiledVehicleRig, (Dictionary<CompiledElement, byte[]> Values, Dictionary<CompiledElement, CompiledElement[]> Children)> prepare,
        out string? refusal)
    {
        refusal = null;
        var fine = (CompiledFrameNode)(source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source);
        var state = _linkedProxyStates.Values.Single(s => s.Pairs.ContainsKey(fine)); var far = state.Pairs[fine];
        var frames = new[] { fine, far }; var rigs = frames.Select(frame => VehicleRig(frame)!).ToArray();
        var before = new Dictionary<CompiledElement, byte[]>(); var after = new Dictionary<CompiledElement, byte[]>();
        var beforeChildren = new Dictionary<CompiledElement, CompiledElement[]>(); var afterChildren = new Dictionary<CompiledElement, CompiledElement[]>();
        try
        {
            foreach (string name in new[] { "m_OrigBoneMatrices", "m_BoneInvMatrices", "m_ParentIndices", "m_BoneAABBs" })
                if (!Graph.Data(Graph.Field(rigs[0].Immutable, name)!).SequenceEqual(Graph.Data(Graph.Field(rigs[1].Immutable, name)!)))
                    throw new InvalidDataException("Fine and far skeleton tables do not correspond.");
            if (!Enumerable.Range(0, rigs[0].Count).Select(rigs[0].NameHash).SequenceEqual(Enumerable.Range(0, rigs[1].Count).Select(rigs[1].NameHash)))
                throw new InvalidDataException("Fine and far skeleton names differ.");
            foreach (var rig in rigs)
            {
                var changes = prepare(rig);
                foreach (var pair in changes.Values) { before.Add(pair.Key, Graph.Data(pair.Key).ToArray()); after.Add(pair.Key, pair.Value); }
                foreach (var pair in changes.Children) { beforeChildren.Add(pair.Key, pair.Key.Children.ToArray()); afterChildren.Add(pair.Key, pair.Value); }
            }
            var action = new PreparedRigTableEdit(this, state.Parent, state.Child, fine, far, before, after, beforeChildren, afterChildren);
            action.Redo();
            IEditAction? bounds;
            try
            {
                bounds = SetPreparedLinkedProxyBoneTransformCore(source, 0, VehicleRig(fine)!.LocalRest(0), out refusal)
                    ?? throw new NotSupportedException(refusal);
            }
            catch { action.Undo(); throw; }
            bool applied = true;
            return new ProxyGeometryAction(
                () =>
                {
                    if (applied) return; action.ValidateRedo(); action.Redo();
                    try { bounds.Redo(); applied = true; } catch { action.Undo(); throw; }
                },
                () =>
                {
                    if (!applied) return; action.ValidateUndo(); bounds.ValidateUndo(); bounds.Undo();
                    try { action.Undo(); applied = false; } catch { bounds.Redo(); throw; }
                }, () => { if (!applied) action.ValidateRedo(); }, () => { if (applied) { action.ValidateUndo(); bounds.ValidateUndo(); } });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException or OverflowException)
        { refusal = exception.Message; return null; }
    }

    private sealed class PreparedRigTableEdit(CompiledSceneDocument document, ulong parent, ulong child, CompiledFrameNode fine, CompiledFrameNode far,
        Dictionary<CompiledElement, byte[]> before, Dictionary<CompiledElement, byte[]> after,
        Dictionary<CompiledElement, CompiledElement[]> beforeChildren, Dictionary<CompiledElement, CompiledElement[]> afterChildren) : IEditAction
    {
        private bool _applied;
        private readonly Dictionary<CompiledElement, CompiledVehicleRig?> _beforeRigs = new[] { fine, far }.ToDictionary(frame => frame.Element, document.VehicleRig);
        private Dictionary<CompiledElement, CompiledVehicleRig?>? _afterRigs;
        private ProxyRuntimeState Validate(bool forward)
        {
            if (!document._linkedProxyStates.TryGetValue(parent, out var state) || state.Child != child || !state.Pairs.TryGetValue(fine, out var target) || !ReferenceEquals(target, far))
                throw new EditRefusedException("The prepared skeleton owners changed.");
            if (ValidateProxyMaterialSignatures(state) is { } material) throw new EditRefusedException(material);
            if (document.ValidateProxyInvariants(state) is { } invariant) throw new EditRefusedException(invariant);
            if ((forward ? before : after).Any(pair => !document.Graph.Data(pair.Key).SequenceEqual(pair.Value))
                || (forward ? beforeChildren : afterChildren).Any(pair => !pair.Key.Children.SequenceEqual(pair.Value)))
                throw new EditRefusedException("The native skeleton table or bone-name ownership changed.");
            return state;
        }
        public void ValidateUndo() { if (_applied) _ = Validate(false); }
        public void ValidateRedo() { if (!_applied) _ = Validate(true); }
        private void Write(Dictionary<CompiledElement, byte[]> values, Dictionary<CompiledElement, CompiledElement[]> children, bool forward)
        {
            foreach (var pair in children)
            {
                var resource = document.Graph.Owner(pair.Key);
                foreach (var old in pair.Key.Children.Except(pair.Value)) document.Graph.SetSubtreeActive(resource, old, false);
                pair.Key.Children.Clear(); pair.Key.Children.AddRange(pair.Value);
                foreach (var item in pair.Value) document.Graph.RegisterSubtree(resource, item);
            }
            foreach (var pair in values) document.Graph.Owner(pair.Key).ReplaceVariable(pair.Key, pair.Value);
            var cache = forward ? _afterRigs : _beforeRigs;
            foreach (var frame in new[] { fine, far })
            {
                document.VehicleRig(frame)?.NotifyAuthoredMutation();
                if (cache is null) document._vehicleRigs.Remove(frame.Element);
                else document._vehicleRigs[frame.Element] = cache[frame.Element];
                document.VehicleRig(frame)?.NotifyAuthoredMutation();
            }
            if (forward && _afterRigs is null) _afterRigs = new[] { fine, far }.ToDictionary(frame => frame.Element, document.VehicleRig);
        }
        private void Apply(bool forward)
        {
            if (_applied == forward) return; var state = Validate(forward); var pins = CaptureProxyStatePins(state);
            var checkpoints = document.Graph.Resources.Select(resource => resource.CaptureRevertedMutationCheckpoint()).ToArray();
            try { Write(forward ? after : before, forward ? afterChildren : beforeChildren, forward); document.ResetProxyInvariants(state); _applied = forward; }
            catch { Write(forward ? before : after, forward ? beforeChildren : afterChildren, !forward); foreach (var checkpoint in checkpoints) checkpoint(); document.RestoreProxyStatePins(state, pins); throw; }
        }
        public void Undo() => Apply(false);
        public void Redo() => Apply(true);
    }
}
