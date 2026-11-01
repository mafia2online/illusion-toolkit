using System.Buffers.Binary;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    // Retain the original dependency authorization across slot undo/redo. Only the currently used
    // subset is persisted; a reopened document has no history requiring removed material identities.
    private readonly Dictionary<ulong, Dictionary<ulong, string>> _proxySlotDependencies = [];
    private readonly Dictionary<ulong, HashSet<ulong>> _proxySlotHighDependencies = [];

    public string? ValidateLinkedProxyMaterialSlots(IFrameNode source)
    {
        var canonical = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
        if (canonical is not CompiledFrameNode frame) return ValidateSceneCollectionMutation(source);
        var state = _linkedProxyStates.Values.SingleOrDefault(s => s.Pairs.ContainsKey(frame));
        if (state is null) return ValidateSceneCollectionMutation(source);
        return ValidateProxyMaterialSignatures(state) ?? ValidateProxyInvariants(state);
    }

    public string? LinkedProxyMaterialRefusal { get; private set; }

    /// <summary>Makes native cache identities independent before material edits. Null with no
    /// refusal means already owned. Compose the returned applied action BEFORE the slot edit so
    /// undo restores the slot table first, then the original native ownership and byte layout.</summary>
    public IEditAction? EnsurePreparedLinkedProxyMaterialOwnership(IFrameNode source, CompiledElement visual, out string? refusal)
    {
        refusal = null;
        var canonical = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
        if (canonical is not CompiledFrameNode frame) { refusal = "Choose a canonical compiled visual."; return null; }
        var state = _linkedProxyStates.Values.SingleOrDefault(s => s.Pairs.ContainsKey(frame));
        if (state is null || !ProxyVisuals(frame).Contains(visual)) { refusal = "Choose a prepared fine visual."; return null; }
        if ((refusal = ValidateProxyMaterialSignatures(state) ?? ValidateProxyInvariants(state)) is not null || HasOwnedProxyGeometry(state, visual)) return null;
        var action = IsolatePreparedProxyGeometry(frame, visual, out refusal, out var validateMaterialUndo);
        if (action is null) return null;
        try { MarkOwnedProxyGeometry(state, visual); }
        catch { action.Undo(); throw; }
        return new ProxyGeometryAction(() =>
        {
            action.Redo();
            if (!_linkedProxyStates.TryGetValue(state.Parent, out var current)) throw new EditRefusedException("The prepared material owner is no longer active.");
            MarkOwnedProxyGeometry(current, visual);
        }, action.Undo, action.ValidateRedo, validateMaterialUndo);
    }

    public IReadOnlyList<CompiledElement> GetLinkedProxyMaterialVisualTargets(IFrameNode source, CompiledElement visual)
    {
        var canonical = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
        if (canonical is not CompiledFrameNode frame) return [];
        var state = _linkedProxyStates.Values.SingleOrDefault(s => s.Pairs.ContainsKey(frame));
        if (state is null) return [];
        int ordinal = Array.IndexOf(ProxyVisuals(frame).ToArray(), visual);
        var targets = ProxyVisuals(state.Pairs[frame]).ToArray();
        return ordinal >= 0 && ordinal < targets.Length ? [targets[ordinal]] : [];
    }

    private bool WriteLinkedProxyMaterials(CompiledFrameNode frame, IReadOnlyList<ulong[]> requested, CompiledElement? visual)
    {
        LinkedProxyMaterialRefusal = null;
        bool Refuse(string reason) { LinkedProxyMaterialRefusal = reason; return false; }
        var state = _linkedProxyStates.Values.SingleOrDefault(s => s.Pairs.ContainsKey(frame));
        if (state is null) return Refuse("Prepare the fine object first.");
        if (ValidateProxyMaterialSignatures(state) is { } materialReason) return Refuse(materialReason);
        if (ValidateProxyInvariants(state) is { } invariantReason) return Refuse(invariantReason);
        var visuals = ProxyVisuals(frame).ToArray();
        if (visual is null && visuals.Length != 1) return false;
        int ordinal = Array.IndexOf(visuals, visual ?? visuals[0]);
        var farVisuals = ProxyVisuals(state.Pairs[frame]).ToArray();
        if (ordinal < 0 || farVisuals.Length != visuals.Length) return false;
        if (!HasOwnedProxyGeometry(state, visuals[ordinal])) return Refuse("Make this prepared visual's native geometry independent before assigning material slots.");
        var near = EditableMaterialTables(frame.Element, visuals[ordinal]);
        var far = EditableMaterialTables(state.Pairs[frame].Element, farVisuals[ordinal]);
        if (near.Count == 0 || requested.Count != near.Count || far.Count != 1) return Refuse("The independent native material tables are unavailable.");
        // A replacement may only use dependencies already carried by this same prepared family.
        var allowed = new Dictionary<ulong, string>(_proxySlotDependencies.TryGetValue(state.Parent, out var prior) ? prior
            : state.MaterialTextures.Concat(state.FineMaterialTextures).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.First().Value));
        foreach (ulong hash in requested.SelectMany(row => row).Distinct())
        {
            var signature = CompiledProxyMaterialGuard.TextureSignature(hash);
            if (signature is null || allowed.TryGetValue(hash, out var expected) && signature != expected)
                return Refuse("The native material is missing or its carried texture identity changed.");
            allowed.TryAdd(hash, signature);
        }
        var originalFineMaterials = _proxySlotHighDependencies.TryGetValue(state.Parent, out var originalHigh) ? new HashSet<ulong>(originalHigh)
            : ProxyMaterialHashes(state.Pairs.Keys, lowest: false, allLevels: true);
        var writes = new Dictionary<CompiledElement, byte[]>();
        bool Stage(CompiledElement field, ulong[] materials)
        {
            byte[] bytes = Graph.Data(field).ToArray();
            if (bytes.Length != 4L + materials.Length * 40L || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != materials.Length) return false;
            for (int i = 0; i < materials.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(12 + i * 40), materials[i]);
            return writes.TryAdd(field, bytes);
        }
        for (int lod = 0; lod < near.Count; lod++) if (!Stage(near[lod], requested[lod])) return false;
        if (!Stage(far[0], requested[^1])) return false;
        if (_linkedProxyStates.Values.Any(other => !ReferenceEquals(other, state) && writes.Keys.Any(other.PinnedData.ContainsKey))) return false;
        var before = writes.Keys.ToDictionary(field => field, field => Graph.Data(field).ToArray());
        var checkpoints = writes.Keys.Select(Graph.Owner).Distinct().Select(resource => resource.CaptureRevertedMutationCheckpoint()).ToArray();
        var pins = CaptureProxyStatePins(state);
        var filesBefore = state.Files; var hashesBefore = new Dictionary<string, byte[]>(state.FileHashes, StringComparer.OrdinalIgnoreCase);
        var managerKeys = _vehicleResources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var carryCheckpoints = _vehicleResources.Values.Select(manager => manager.CaptureFlushCheckpoint()).ToArray();
        var changedCarries = new List<(ProxySlotTextureCarry Carry, bool Before)>();
        var newCarries = new Dictionary<(ulong Parent, ulong Material, string Destination), ProxySlotTextureCarry>();
        try
        {
            string fineFolder = Path.GetDirectoryName(Graph.Owner(frame.Element).Path)!;
            string farFolder = Path.GetDirectoryName(Graph.Owner(state.Pairs[frame].Element).Path)!;
            foreach (ulong hash in requested.SelectMany(row => row).Distinct())
            {
                var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!originalFineMaterials.Contains(hash)) destinations.Add(fineFolder);
                if (requested[^1].Contains(hash) && (!state.MaterialTextures.ContainsKey(hash)
                    || _proxySlotCarries.ContainsKey((state.Parent, hash, farFolder)))) destinations.Add(farFolder);
                foreach (string destination in destinations)
                {
                    var key = (state.Parent, hash, destination);
                    bool existed = _proxySlotCarries.TryGetValue(key, out var carry);
                    carry ??= CarryProxySlotMaterial(state, hash, destination);
                    if (!existed) { newCarries.Add(key, carry); changedCarries.Add((carry, false)); }
                    else if (!carry.Applied) { changedCarries.Add((carry, false)); carry.Edit.Redo(); carry.Applied = true; }
                }
            }
            foreach (var write in writes) Graph.Owner(write.Key).Replace(write.Key, write.Value);
            var farUsed = ProxyMaterialHashes(state.Pairs.Values, lowest: false);
            var fineByFolder = state.Pairs.Keys.GroupBy(f => Path.GetDirectoryName(Graph.Owner(f.Element).Path)!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => ProxyMaterialHashes(group, lowest: false, allLevels: true), StringComparer.OrdinalIgnoreCase);
            var carries = _proxySlotCarries.Where(pair => pair.Key.Parent == state.Parent).ToDictionary();
            foreach (var pair in newCarries) carries.Add(pair.Key, pair.Value);
            foreach (var pair in carries.Where(pair => pair.Value.Applied))
            {
                bool needed = pair.Key.Destination.Equals(farFolder, StringComparison.OrdinalIgnoreCase) && farUsed.Contains(pair.Key.Material)
                    || !originalFineMaterials.Contains(pair.Key.Material) && fineByFolder.TryGetValue(pair.Key.Destination, out var used) && used.Contains(pair.Key.Material);
                if (!needed) { changedCarries.Add((pair.Value, true)); pair.Value.Edit.Undo(); pair.Value.Applied = false; }
            }
            AcceptProxySlotSourceFiles(state, carries.Values.Where(carry => carry.Applied));
            ResetProxyInvariants(state);
            foreach (var pair in newCarries) _proxySlotCarries.Add(pair.Key, pair.Value);
            _proxySlotDependencies[state.Parent] = allowed;
            _proxySlotHighDependencies.TryAdd(state.Parent, originalFineMaterials);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or IOException or NotSupportedException or OverflowException)
        {
            foreach (var pair in before) Graph.Owner(pair.Key).Replace(pair.Key, pair.Value);
            foreach (var carry in changedCarries.AsEnumerable().Reverse())
            {
                if (carry.Before) carry.Carry.Edit.Redo(); else carry.Carry.Edit.Undo(); carry.Carry.Applied = carry.Before;
            }
            foreach (var pair in newCarries) _proxySlotCarries.Remove(pair.Key);
            foreach (var checkpoint in carryCheckpoints) checkpoint();
            foreach (var key in _vehicleResources.Keys.Where(key => !managerKeys.Contains(key)).ToArray()) _vehicleResources.Remove(key);
            state.Files = filesBefore; state.FileHashes = hashesBefore;
            foreach (var checkpoint in checkpoints) checkpoint();
            RestoreProxyStatePins(state, pins);
            return Refuse(ex.Message);
        }
    }
}
