using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Existing immutable rest bone. Local is the native parent-local row-vector transform;
/// this inspection never exposes a transient animation pose.</summary>
public sealed record PreparedProxyBoneInfo(int Index, string Name, int Parent, Matrix4x4 Local);

public sealed partial class CompiledSceneDocument
{
    public IReadOnlyList<PreparedProxyBoneInfo> InspectPreparedLinkedProxyBones(IFrameNode source, out string? refusal)
    {
        refusal = null;
        var canonical = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
        if (canonical is not CompiledFrameNode fine || !_linkedProxyStates.Values.Any(s => s.Pairs.ContainsKey(fine)))
        { refusal = "Choose a prepared static fine object."; return []; }
        var state = _linkedProxyStates.Values.Single(s => s.Pairs.ContainsKey(fine));
        if ((refusal = ValidateProxyMaterialSignatures(state) ?? ValidateProxyInvariants(state)) is not null) return [];
        foreach (var frame in new[] { fine, state.Pairs[fine] })
        {
            var candidate = VehicleRig(frame);
            if (frame.Children.Count != 0 || Components(frame.Element).Any(c => c.TypeId is not (0x0fa73185 or 0x2da22400 or 0x2b7b4775 or 0xe27be3b3))
                || !StaticGeometry(frame, rigidCollision: true) && !PreparedSkinContract(frame,out _)
                || candidate is null)
            { refusal = "Paired rest-bone edits require rigid model/skeleton components with verified static collision/glass; gameplay, dynamic, skin and morph dependencies need their own contract."; return []; }
            if ((refusal = (ValidatePreparedRigCollision(frame, candidate) ?? ValidatePreparedRigGlass(frame, candidate))) is not null) return [];
        }
        var rig = VehicleRig(fine)!;
        return Enumerable.Range(0, rig.Count).Select(i => new PreparedProxyBoneInfo(i, rig.Name(i), rig.Parent(i), rig.LocalRest(i))).ToArray();
    }

    /// <summary>Changes one existing rigid bind bone in a uniquely owned prepared static pair.
    /// Rest matrices are paired; bone indices, inverse binds, palettes and raw geometry stay intact.
    /// The returned action is already applied; refresh both frames' decoded static geometry.</summary>
    private IEditAction? SetPreparedLinkedProxyBoneTransformCore(IFrameNode source, int bone, Matrix4x4 local, out string? refusal)
    {
        refusal = null;
        var canonical = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
        if (canonical is not CompiledFrameNode fine || !_linkedProxyStates.Values.Any(s => s.Pairs.ContainsKey(fine)))
        { refusal = "Choose a prepared static fine object."; return null; }
        var state = _linkedProxyStates.Values.Single(s => s.Pairs.ContainsKey(fine)); var far = state.Pairs[fine];
        if ((refusal = ValidateProxyMaterialSignatures(state) ?? ValidateProxyInvariants(state)) is not null) return null;
        var before = new Dictionary<CompiledElement, byte[]>();
        var after = new Dictionary<CompiledElement, byte[]>();
        var checkpoints = Graph.Resources.Select(r => r.CaptureRevertedMutationCheckpoint()).ToArray();
        var pins = CaptureProxyStatePins(state); IEditAction? bounds = null;
        try
        {
            if (!CompiledMeshBinding.IsRigidBind(local)) throw new NotSupportedException("A static bind bone must remain a finite proper rigid transform.");
            var rigs = new[] { fine, far }.Select(frame =>
            {
                if (frame.Children.Count != 0 || Components(frame.Element).Any(c => c.TypeId is not (0x0fa73185 or 0x2da22400 or 0x2b7b4775 or 0xe27be3b3))
                    || !StaticGeometry(frame, rigidCollision: true) && !PreparedSkinContract(frame,out _))
                    throw new NotSupportedException("Paired bind edits require rigid static models with verified static collision/glass dependencies.");
                var rig = VehicleRig(frame) ?? throw new NotSupportedException("The object has no immutable native skeleton.");
                if (ValidatePreparedRigGlass(frame, rig) is { } glassRefusal) throw new NotSupportedException(glassRefusal);
                if ((uint)bone >= rig.Count || Graph.Nodes.Count(n => ReferenceEquals(Graph.Resolve(Graph.Field(n, "m_ImmutableData")), rig.Immutable)) != 1)
                    throw new NotSupportedException("The native immutable skeleton must be uniquely owned and the bone index valid.");
                return rig;
            }).ToArray();
            foreach (string name in new[] { "m_OrigBoneMatrices", "m_BoneInvMatrices", "m_ParentIndices" })
                if (!Graph.Data(Graph.Field(rigs[0].Immutable, name)!).SequenceEqual(Graph.Data(Graph.Field(rigs[1].Immutable, name)!)))
                    throw new InvalidDataException("Fine and far native bind tables do not correspond.");
            var names = rigs.Select(rig => Graph.Items(Graph.Field(rig.Immutable, "m_BoneNames")).Select(Graph.Integer).ToArray()).ToArray();
            if (!names[0].SequenceEqual(names[1])) throw new InvalidDataException("Fine and far bone identities differ.");
            var modelsBefore = rigs.Select(rig => Enumerable.Range(0, rig.Count).Select(rig.ModelRest).ToArray()).ToArray();
            foreach (var rig in rigs)
            {
                var field = Graph.Field(rig.Immutable, "m_OrigBoneMatrices")!; var bytes = Graph.Data(field).ToArray();
                before.Add(field, bytes.ToArray()); WriteMatrix(local).CopyTo(bytes, 4 + bone * 48); after.Add(field, bytes);
                Graph.Owner(field).Replace(field, bytes);
            }
            foreach (var frame in new[] { fine, far })
            {
                int rigIndex = ReferenceEquals(frame, fine) ? 0 : 1;
                UpdatePreparedRigCollision(frame, rigs[rigIndex], modelsBefore[rigIndex], before, after);
                bool skin=HasNativeSkin(frame);
                NativeLodBounds? aggregate = skin?PreparedSkinLocalBounds(frame):null;
                foreach (var visual in skin?Enumerable.Empty<CompiledElement>():ProxyVisuals(frame))
                {
                    if (!CompiledMeshBinding.TryImmutableRigidBind(Graph, frame.Element, visual, out var bind)
                        || Graph.Field(visual, "m_Data") is not { DataSize: 88 } data)
                        throw new NotSupportedException("Every native visual must retain a verified rigid bind and local bounds.");
                    var bytes = Graph.Data(data).Slice(16, 24);
                    var box = TransformNativeBounds(new(CompiledMeshDecoder.Vector(bytes), CompiledMeshDecoder.Vector(bytes[12..])), bind);
                    aggregate = aggregate is { } old ? new(Vector3.Min(old.Min, box.Min), Vector3.Max(old.Max, box.Max)) : box;
                }
                if (aggregate is null) throw new InvalidDataException("The paired rig has no visual geometry.");
                if(skin)
                    foreach(var skeleton in Components(frame.Element).Where(component=>component.TypeId==0x0fa73185))
                        foreach(string name in new[]{"m_LocalAABB","m_WorldAABB"})
                        {
                            var field=Graph.Field(skeleton,name);
                            if(field is not{DataSize:24})throw new NotSupportedException("The skinned native skeleton lacks a verified envelope field.");
                            before.TryAdd(field,Graph.Data(field).ToArray());
                            after[field]=NativeBoundsBytes(name=="m_LocalAABB"?aggregate.Value:TransformNativeBounds(aggregate.Value,frame.WorldTransform));
                            Graph.Owner(field).Replace(field,after[field]);
                        }
                foreach (var model in Components(frame.Element).Where(c => c.TypeId == 0x2da22400))
                    if (Graph.Field(model, "m_LocalAABB") is { } field)
                    {
                        if (Graph.Data(field).Length != 24) throw new InvalidDataException("Unknown model bounds layout.");
                        before.TryAdd(field, Graph.Data(field).ToArray()); after[field] = NativeBoundsBytes(aggregate.Value);
                        Graph.Owner(field).Replace(field, after[field]);
                    }
            }
            if (_linkedProxyStates.Values.Any(other => !ReferenceEquals(other, state) && after.Keys.Any(other.PinnedData.ContainsKey)))
                throw new InvalidDataException("A bind field is protected by another native family.");
            bounds = PrepareNativeLodBounds();
            foreach (var pair in before) Graph.Owner(pair.Key).Replace(pair.Key, pair.Value);
            foreach (var checkpoint in checkpoints) checkpoint(); RestoreProxyStatePins(state, pins);
            var action = new PreparedProxyRigEdit(this, state.Parent, state.Child, fine, far, before, after, bounds, rigs);
            action.Redo(); return action;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
        {
            foreach (var pair in before) Graph.Owner(pair.Key).Replace(pair.Key, pair.Value);
            foreach (var checkpoint in checkpoints) checkpoint(); RestoreProxyStatePins(state, pins); refusal = ex.Message; return null;
        }
    }

    private sealed class PreparedProxyRigEdit(CompiledSceneDocument document, ulong parent, ulong child,
        CompiledFrameNode fine, CompiledFrameNode far, Dictionary<CompiledElement, byte[]> before,
        Dictionary<CompiledElement, byte[]> after, IEditAction bounds, CompiledVehicleRig[] rigs) : IEditAction
    {
        private bool _applied;
        private ProxyRuntimeState Validate(bool forward)
        {
            if (!document._linkedProxyStates.TryGetValue(parent, out var state) || state.Child != child
                || !state.Pairs.TryGetValue(fine, out var target) || !ReferenceEquals(target, far))
                throw new EditRefusedException("The bind edit's prepared fine/far owners changed.");
            if (ValidateProxyMaterialSignatures(state) is { } material) throw new EditRefusedException(material);
            if (document.ValidateProxyInvariants(state) is { } invariant) throw new EditRefusedException(invariant);
            var expected = forward ? before : after;
            if (expected.Any(p => !document.Graph.Data(p.Key).SequenceEqual(p.Value))
                || rigs.Any(rig => document.Graph.Nodes.Count(n => ReferenceEquals(document.Graph.Resolve(document.Graph.Field(n, "m_ImmutableData")), rig.Immutable)) != 1))
                throw new EditRefusedException("The paired native bind changed or became shared.");
            return state;
        }
        public void ValidateUndo() { if (_applied) _ = Validate(false); }
        public void ValidateRedo() { if (!_applied) _ = Validate(true); }
        private void Apply(bool forward)
        {
            if (_applied == forward) return; var state = Validate(forward); var pins = CaptureProxyStatePins(state);
            var expected = forward ? before : after; var values = forward ? after : before;
            var checkpoints = values.Keys.Select(document.Graph.Owner).Distinct().Select(r => r.CaptureDirtyCheckpoint()).ToArray(); bool boundsApplied = false;
            try
            {
                foreach (var pair in values) document.Graph.Owner(pair.Key).Replace(pair.Key, pair.Value);
                if (forward) bounds.Redo(); else bounds.Undo(); boundsApplied = true;
                document.ResetProxyInvariants(state); foreach (var rig in rigs) rig.NotifyAuthoredMutation(); _applied = forward;
            }
            catch
            {
                if (boundsApplied) { if (forward) bounds.Undo(); else bounds.Redo(); }
                foreach (var pair in expected) document.Graph.Owner(pair.Key).Replace(pair.Key, pair.Value);
                foreach (var checkpoint in checkpoints) checkpoint(); document.RestoreProxyStatePins(state, pins); throw;
            }
        }
        public void Undo() => Apply(false);
        public void Redo() => Apply(true);
    }
}
