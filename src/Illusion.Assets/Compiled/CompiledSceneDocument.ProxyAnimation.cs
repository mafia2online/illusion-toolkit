using Illusion.Domain;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Cheap live topology identity check for an already validated playback lease.
    /// Authored rig revisions and canonical rig adapters must be checked separately by the caller.</summary>
    public bool AreAnimationTargetsCurrent(IFrameNode source, IReadOnlyList<CompiledFrameNode> targets)
    {
        var canonical = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
        if (canonical is not CompiledFrameNode frame) return false;
        var state = _linkedProxyStates.Values.SingleOrDefault(candidate => candidate.Pairs.ContainsKey(frame) || candidate.Pairs.ContainsValue(frame));
        if (state is null) return targets.Count == 1 && ReferenceEquals(targets[0], frame);
        var fine = state.Pairs.ContainsKey(frame) ? frame : state.Pairs.Single(pair => ReferenceEquals(pair.Value, frame)).Key;
        return targets.Count == 2 && ReferenceEquals(targets[0], fine) && ReferenceEquals(targets[1], state.Pairs[fine]);
    }
    /// <summary>Verified prepared pair playback targets, fine first. No authored resource is changed.</summary>
    public IReadOnlyList<CompiledFrameNode> GetPreparedAnimationTargets(IFrameNode source, out string? refusal)
    {
        refusal = null;
        var canonical = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
        if (canonical is not CompiledFrameNode fine) return [];
        var state = _linkedProxyStates.Values.SingleOrDefault(candidate => candidate.Pairs.ContainsKey(fine) || candidate.Pairs.ContainsValue(fine));
        if (state is null) return [fine];
        if (!state.Pairs.ContainsKey(fine)) fine = state.Pairs.Single(pair => ReferenceEquals(pair.Value, fine)).Key;
        refusal = ValidateProxyMaterialSignatures(state) ?? ValidateProxyInvariants(state);
        if (refusal is not null) return [];
        var far = state.Pairs[fine];
        var rigs = new[] { fine, far }.Select(VehicleRig).ToArray();
        if (rigs.Any(rig => rig is null)) { refusal = "Both prepared playback targets need a render skeleton."; return []; }
        foreach (string field in new[] { "m_OrigBoneMatrices", "m_BoneInvMatrices", "m_ParentIndices" })
            if (!Graph.Data(Graph.Field(rigs[0]!.Immutable, field)!).SequenceEqual(Graph.Data(Graph.Field(rigs[1]!.Immutable, field)!)))
            { refusal = "Fine and far skeleton bind tables do not correspond."; return []; }
        if (!Graph.Items(Graph.Field(rigs[0]!.Immutable, "m_BoneNames")).Select(Graph.Integer)
            .SequenceEqual(Graph.Items(Graph.Field(rigs[1]!.Immutable, "m_BoneNames")).Select(Graph.Integer)))
        { refusal = "Fine and far skeleton bone identities do not correspond."; return []; }
        return [fine, far];
    }
}
