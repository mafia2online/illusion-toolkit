using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private sealed record ProxyPinsSnapshot(Dictionary<CompiledElement, byte[]> Data,
        Dictionary<CompiledElement, CompiledElement[]> Children, Dictionary<CompiledElement, byte[]> Transforms,
        Dictionary<CompiledElement, byte[]> Bounds, Dictionary<ulong, string> Materials, Dictionary<ulong, ulong> GeometryOwners,
        Dictionary<string, string> GeometryOrigins, Dictionary<ulong, string> FineMaterials);

    private static ProxyPinsSnapshot CaptureProxyStatePins(ProxyRuntimeState state) => new(
        state.PinnedData.ToDictionary(p => p.Key, p => p.Value.ToArray()),
        state.PinnedChildren.ToDictionary(p => p.Key, p => p.Value.ToArray()),
        state.ExpectedTransforms.ToDictionary(p => p.Key, p => p.Value.ToArray()),
        state.ExpectedBounds.ToDictionary(p => p.Key, p => p.Value.ToArray()), new(state.MaterialTextures), new(state.GeometryOwners),
        new(state.GeometryOrigins, StringComparer.OrdinalIgnoreCase), new(state.FineMaterialTextures));

    // The caller owns complete preflight of its explicit write-set and restores this snapshot on
    // failure. Never use this to accept an arbitrary external edit or a merely increased revision.
    private void ResetProxyInvariants(ProxyRuntimeState state)
    {
        state.PinnedData.Clear(); state.PinnedChildren.Clear(); state.ExpectedTransforms.Clear(); state.ExpectedBounds.Clear();
        state.MaterialTextures.Clear(); state.FineMaterialTextures.Clear(); CaptureProxyInvariants(state); CaptureProxyMaterialSignatures(state); state.AcceptRevisions();
    }
    private void RestoreProxyStatePins(ProxyRuntimeState state, ProxyPinsSnapshot snapshot)
    {
        static void Restore<TKey, TValue>(Dictionary<TKey, TValue> target, Dictionary<TKey, TValue> source) where TKey : notnull
        { target.Clear(); foreach (var pair in source) target.Add(pair.Key, pair.Value); }
        Restore(state.PinnedData, snapshot.Data); Restore(state.PinnedChildren, snapshot.Children);
        Restore(state.ExpectedTransforms, snapshot.Transforms); Restore(state.ExpectedBounds, snapshot.Bounds); Restore(state.MaterialTextures, snapshot.Materials);
        Restore(state.GeometryOwners, snapshot.GeometryOwners);
        Restore(state.GeometryOrigins, snapshot.GeometryOrigins);
        Restore(state.FineMaterialTextures, snapshot.FineMaterials);
        state.AcceptRevisions(); state.StructuralRevision = _proxyStructuralRevision; state.BoundsRevision = NativeLods?.BoundsRevision ?? -1;
    }
}
