using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private long _proxyStructuralRevision;
    private sealed record ProxyWorkspaceRecord(int Version, ulong Parent, ulong Child,
        Dictionary<ulong, ulong> Pairs, Dictionary<string, string> Files, Dictionary<ulong, float[]> Bounds,
        Dictionary<ulong, string> MaterialTextures, string DestinationResource, Dictionary<ulong, ulong>? GeometryOwners = null,
        Dictionary<string, string>? GeometryOrigins = null, Dictionary<ulong, string>? FineMaterialTextures = null);
    private sealed class ProxyRuntimeState(ulong parent, ulong child, IReadOnlyDictionary<CompiledFrameNode, CompiledFrameNode> pairs,
        CompiledResource[] resources, string[] files)
    {
        internal ulong Parent { get; } = parent;
        internal ulong Child { get; } = child;
        internal Dictionary<CompiledFrameNode, CompiledFrameNode> Pairs { get; } = new(pairs);
        internal CompiledResource[] Resources { get; } = resources;
        internal string[] Files { get; set; } = files;
        internal Dictionary<string, byte[]> FileHashes { get; set; } = files.ToDictionary(p => p,
            p => SHA256.HashData(File.ReadAllBytes(p)), StringComparer.OrdinalIgnoreCase);
        internal Dictionary<CompiledResource, long> Revisions { get; } = resources.ToDictionary(r => r, r => r.Revision);
        internal Dictionary<CompiledElement, byte[]> PinnedData { get; } = [];
        internal Dictionary<CompiledElement, CompiledElement[]> PinnedChildren { get; } = [];
        internal Dictionary<CompiledElement, byte[]> ExpectedTransforms { get; } = [];
        internal Dictionary<CompiledElement, byte[]> ExpectedBounds { get; } = [];
        internal long BoundsRevision { get; set; }
        internal long StructuralRevision { get; set; }
        internal Dictionary<ulong, string> MaterialTextures { get; } = [];
        internal Dictionary<ulong, string> FineMaterialTextures { get; } = [];
        // Persisted hints only. Geometry's native ownership verifier must independently authorize them.
        internal Dictionary<ulong, ulong> GeometryOwners { get; } = [];
        internal Dictionary<string, string> GeometryOrigins { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal string DestinationResource { get; set; } = "";
        internal void AcceptRevisions() { foreach (var resource in Resources) Revisions[resource] = resource.Revision; }
    }
    private readonly Dictionary<ulong, ProxyRuntimeState> _linkedProxyStates = [];
    private readonly Dictionary<string, byte[]?> _proxyMappingBaselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]?> _proxyMappingExpected = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ulong> PreparedLinkedProxyGroups => _linkedProxyStates.Keys.ToArray();
    public IReadOnlyList<string> LinkedProxyMappingWarnings => _proxyMappingWarnings;
    private readonly List<string> _proxyMappingWarnings = [];
    internal Action? ProxySaveAfterNativeWrite { get; set; }
    internal Action? ProxySaveBeforeSnapshot { get; set; }
    private static string ProxyMappingPath(ulong parent) => Path.Combine(CompiledProxySaveJournal.Workspace, parent.ToString("X16") + ".json");

    private void RegisterPreparedProxyState(PreparedProxyEdit edit)
    {
        var plan = edit.Preview;
        string path = ProxyMappingPath(plan.ParentGroupId);
        if (!_proxyMappingBaselines.ContainsKey(path)) _proxyMappingBaselines.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
        _proxyMappingExpected.TryAdd(path, _proxyMappingBaselines[path]);
        var resources = plan.ResourceHashes.Keys.Append(plan.Destination).Concat(_nativeLodGroupFields.Values.Select(Graph.Owner))
            .Concat(plan.Fine.SelectMany(frame =>
            {
                var ancestors = new List<CompiledResource>();
                for (var current = frame; current is not null; current = current.ParentFrame)
                {
                    ancestors.Add(Graph.Owner(current.Element));
                    if (current.LinkTransform is { } link) ancestors.Add(Graph.Owner(link));
                }
                return ancestors;
            })).Distinct().ToArray();
        var files = resources.Select(r => r.Path).Concat(plan.FileHashes.Keys).Concat(resources.Select(r => Path.Combine(Path.GetDirectoryName(r.Path)!, "SDSContent.xml")))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var state = new ProxyRuntimeState(plan.ParentGroupId, plan.ChildGroup.ObjectId, edit.Pairs, resources, files);
        state.DestinationResource = Path.GetRelativePath(MafiaEnvironment.ResourcesFolder!, plan.Destination.Path);
        CaptureProxyInvariants(state); CaptureProxyMaterialSignatures(state);
        _linkedProxyStates.Add(plan.ParentGroupId, state); CompiledProxyMaterialGuard.Track(this);
    }

    private void LoadLinkedProxyMappings()
    {
        if (!Directory.Exists(CompiledProxySaveJournal.Workspace)) return;
        foreach (string path in Directory.GetFiles(CompiledProxySaveJournal.Workspace, "*.json"))
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var mapping = JsonSerializer.Deserialize<ProxyWorkspaceRecord>(bytes) ?? throw new InvalidDataException("Empty mapping.");
                if (!Graph.Objects.ContainsKey(mapping.Parent)) continue;
                if (mapping.Version != 2 || mapping.MaterialTextures is null || mapping.MaterialTextures.Count == 0)
                    throw new InvalidDataException("This proxy mapping lacks material dependencies; restore or explicitly revalidate it before authoring.");
                if (mapping.Files is null || mapping.Pairs is null || mapping.Bounds is null || string.IsNullOrEmpty(mapping.DestinationResource))
                    throw new InvalidDataException("Proxy mapping has missing required fields.");
                _ = CompiledProxyMaterialMapVerifier.Read(path);
                if (Path.GetFileName(path) != mapping.Parent.ToString("X16") + ".json") throw new InvalidDataException("Mapping identity mismatch.");
                string Full(string relative)
                {
                    string root = Path.GetFullPath(MafiaEnvironment.ResourcesFolder!); string full = Path.GetFullPath(Path.Combine(root, relative));
                    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Mapping path escapes workspace.");
                    return full;
                }
                var files = mapping.Files.ToDictionary(p => Full(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                    if (!File.Exists(file.Key) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Key))) != file.Value)
                        throw new InvalidDataException("Native files changed or are missing; inspect/rebuild the proxy mapping explicitly.");
                if (mapping.Pairs.Count == 0 || mapping.Pairs.Values.Distinct().Count() != mapping.Pairs.Count
                    || Graph.Objects.GetValueOrDefault(mapping.Child) is not { TypeId: 0x23001d00 } child
                    || Graph.Resolve(Graph.Field(child, "m_Parent"))?.ObjectId != mapping.Parent)
                    throw new InvalidDataException("Native proxy family identity changed.");
                var pairs = new Dictionary<CompiledFrameNode, CompiledFrameNode>();
                foreach (var pair in mapping.Pairs)
                {
                    if (!Graph.Objects.TryGetValue(pair.Key, out var source) || !_frames.TryGetValue(source, out var fine)
                        || !Graph.Objects.TryGetValue(pair.Value, out var target) || !_frames.TryGetValue(target, out var far)
                        || ProxyGroup(fine)?.ObjectId != mapping.Child || !ProxyFineAncestrySupported(fine) || fine.Children.Count != 0
                        || ProxyGroup(far)?.ObjectId != mapping.Parent || far.ParentFrame is not null || far.Children.Count != 0
                        || !files.ContainsKey(Graph.Owner(source).Path) || !files.ContainsKey(Graph.Owner(target).Path)
                        || !Graph.Owner(target).Path.Equals(Full(mapping.DestinationResource),StringComparison.OrdinalIgnoreCase)
                        || Components(target).Any(c => c.TypeId is not (0x0fa73185 or 0x2da22400)))
                        throw new InvalidDataException("Prepared render ownership or group membership changed.");
                    pairs.Add(fine, far);
                }
                if (!_frames.Values.Where(f => ProxyGroup(f)?.ObjectId == mapping.Child).ToHashSet().SetEquals(pairs.Keys)
                    || !_frames.Values.Where(f => ProxyGroup(f)?.ObjectId == mapping.Parent && ProxyVisuals(f).Any()).ToHashSet().SetEquals(pairs.Values))
                    throw new InvalidDataException("The prepared mapping no longer covers the complete native fine/coarse family.");
                var resources = Graph.Resources.Where(r => files.ContainsKey(r.Path)).ToArray();
                if (resources.Length == 0) throw new InvalidDataException("Proxy mapping has no native resource snapshots.");
                if (mapping.Bounds.Any(p => p.Value is null || p.Value.Length != 6)) throw new InvalidDataException("Proxy bounds baseline is missing.");
                var baseline = mapping.Bounds.ToDictionary(p => p.Key,
                    p => new NativeLodBounds(new(p.Value[0], p.Value[1], p.Value[2]), new(p.Value[3], p.Value[4], p.Value[5])));
                var state = new ProxyRuntimeState(mapping.Parent, mapping.Child, pairs, resources, files.Keys.ToArray());
                state.DestinationResource = mapping.DestinationResource;
                if (mapping.GeometryOwners is not null)
                    foreach (var pair in mapping.GeometryOwners.Where(pair => pair.Key != 0 && pair.Value != 0)) state.GeometryOwners.TryAdd(pair.Key, pair.Value);
                if (mapping.GeometryOrigins is not null)
                    foreach (var pair in mapping.GeometryOrigins.Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                        && pair.Value is { Length: 64 } && pair.Value.All(Uri.IsHexDigit))) state.GeometryOrigins.TryAdd(pair.Key, pair.Value);
                CaptureProxyInvariants(state); CaptureProxyMaterialSignatures(state);
                if (state.MaterialTextures.Count != mapping.MaterialTextures.Count || state.MaterialTextures.Any(p =>
                    !mapping.MaterialTextures.TryGetValue(p.Key, out var signature) || signature != p.Value))
                    throw new InvalidDataException("Prepared proxy material texture dependencies changed; its carried textures have not been rebuilt.");
                if (mapping.FineMaterialTextures is not null && (state.FineMaterialTextures.Count != mapping.FineMaterialTextures.Count
                    || state.FineMaterialTextures.Any(p => !mapping.FineMaterialTextures.TryGetValue(p.Key, out var signature) || signature != p.Value)))
                    throw new InvalidDataException("Prepared fine LOD material texture dependencies changed.");
                InstallNativeLodBoundsBaseline(baseline);
                _linkedProxyStates.Add(mapping.Parent, state); CompiledProxyMaterialGuard.Track(this);
                _proxyMappingBaselines.Add(path, bytes);
                _proxyMappingExpected.Add(path, bytes);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
            { _proxyMappingWarnings.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }
    }

    private string SaveWorkingCopyWithProxyRecovery()
    {
        if (_proxyMappingBaselines.Count == 0 && _linkedProxyStates.Count == 0) return SaveWorkingCopyCore();
        foreach (var state in _linkedProxyStates.Values)
            if (ValidateProxyMaterialSignatures(state) is { } materialRefusal) throw new InvalidOperationException(materialRefusal);
            else if (ValidateProxyInvariants(state) is not null || state.FileHashes.Any(p => !File.Exists(p.Key)
                    || !SHA256.HashData(File.ReadAllBytes(p.Key)).AsSpan().SequenceEqual(p.Value)))
                throw new InvalidOperationException("A prepared proxy resource changed outside its paired transform transaction. Reload before saving.");
        foreach (var expected in _proxyMappingExpected)
            if (File.Exists(expected.Key) ? expected.Value is null || !File.ReadAllBytes(expected.Key).AsSpan().SequenceEqual(expected.Value) : expected.Value is not null)
                throw new InvalidOperationException("The proxy workspace mapping changed independently; reload before saving.");
        var mappingPaths = _proxyMappingBaselines.Keys.Concat(_linkedProxyStates.Keys.Select(ProxyMappingPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var metadataPaths = mappingPaths.Concat(ProxyGeometryOriginPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var folders = Graph.Resources.Where(r => r.IsDirty || HasStructuralChanges(r)).Select(r => Path.GetDirectoryName(r.Path)!)
            .Concat(_vehicleResources.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var dirty = Graph.Resources.Select(r => r.CaptureDirtyCheckpoint()).ToArray();
        var carryCheckpoints = _vehicleResources.Values.Select(c => c.CaptureFlushCheckpoint()).ToArray();
        var oldFiles = _linkedProxyStates.Values.ToDictionary(state => state, state => state.Files);
        using var journal = new CompiledProxySaveJournal(folders, metadataPaths);
        try
        {
            string result = SaveWorkingCopyCore();
            ProxySaveAfterNativeWrite?.Invoke();
            FlushProxyGeometryOrigins();
            foreach (string path in mappingPaths)
            {
                ulong parent = Convert.ToUInt64(Path.GetFileNameWithoutExtension(path), 16);
                if (_linkedProxyStates.TryGetValue(parent, out var state))
                {
                    var payloads = state.Resources.Select(r => Path.GetDirectoryName(r.Path)!).Distinct(StringComparer.OrdinalIgnoreCase)
                        .SelectMany(folder => XElement.Load(Path.Combine(folder, "SDSContent.xml")).Elements("ResourceEntry")
                            .Where(e => e.Element("Type")?.Value is "Texture" or "MemFile")
                            .Select(e => Path.Combine(folder, e.Element("File")!.Value)));
                    var stagedPayloads = _vehicleResources.Values.SelectMany(manager => manager.StagedPayloadFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var activePayloads = _vehicleResources.Values.SelectMany(manager => manager.ActivePayloadFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    state.Files = state.Files.Where(file => !stagedPayloads.Contains(file) || activePayloads.Contains(file))
                        .Concat(payloads).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var files = state.Files.ToDictionary(p => Path.GetRelativePath(MafiaEnvironment.ResourcesFolder!, p),
                        p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))), StringComparer.OrdinalIgnoreCase);
                    var record = new ProxyWorkspaceRecord(2, state.Parent, state.Child,
                        state.Pairs.ToDictionary(p => p.Key.Element.ObjectId, p => p.Value.Element.ObjectId), files,
                        NativeLodBoundsBaseline.ToDictionary(p => p.Key, p => new[] { p.Value.Min.X, p.Value.Min.Y, p.Value.Min.Z, p.Value.Max.X, p.Value.Max.Y, p.Value.Max.Z }),
                        new(state.MaterialTextures), state.DestinationResource, new(state.GeometryOwners), new(state.GeometryOrigins), new(state.FineMaterialTextures));
                    AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(record));
                }
                else if (_proxyMappingBaselines.GetValueOrDefault(path) is { } original) AtomicFile.WriteAllBytes(path, original);
                else if (File.Exists(path)) File.Delete(path);
            }
            ProxySaveBeforeSnapshot?.Invoke();
            var mappingSnapshots = metadataPaths.Select(path => (Path: path, Bytes: File.Exists(path) ? File.ReadAllBytes(path) : null)).ToArray();
            var nativeSnapshots = _linkedProxyStates.Values.Select(state => (State: state, Hashes: state.Files.ToDictionary(path => path,
                path => SHA256.HashData(File.ReadAllBytes(path)), StringComparer.OrdinalIgnoreCase))).ToArray();
            journal.Commit();
            foreach (var snapshot in mappingSnapshots) _proxyMappingExpected[snapshot.Path] = snapshot.Bytes;
            foreach (var snapshot in nativeSnapshots) snapshot.State.FileHashes = snapshot.Hashes;
            return result;
        }
        catch
        {
            foreach (var pair in oldFiles) pair.Key.Files = pair.Value;
            foreach (var restore in dirty) restore();
            foreach (var restore in carryCheckpoints) restore();
            throw;
        }
    }

    private void CaptureProxyInvariants(ProxyRuntimeState state)
    {
        state.StructuralRevision = _proxyStructuralRevision;
        state.BoundsRevision = NativeLods?.BoundsRevision ?? -1;
        foreach (ulong group in new[] { state.Parent, state.Child })
            if (_nativeLodGroupFields.TryGetValue(group, out var field)) state.ExpectedBounds[field] = Graph.Data(field).ToArray();
        var selected = SelectProxyLowestGeometry(state.Pairs.Keys.Concat(state.Pairs.Values).ToArray());
        var transforms = new HashSet<CompiledElement>();
        foreach (var frame in state.Pairs.Keys.Concat(state.Pairs.Values))
            for (var ancestor = frame; ancestor is not null; ancestor = ancestor.ParentFrame)
            {
                if (Graph.Field(ancestor.Element, "m_Transform") is { } field) transforms.Add(field);
                if (ancestor.LinkTransform is { } link) transforms.Add(link);
            }
        foreach (var field in transforms) state.ExpectedTransforms[field] = Graph.Data(field).ToArray();
        var bounds = _nativeLodGroupFields.Values.ToHashSet();
        foreach (var node in selected.Closure.SelectMany(root => ProxyNodes(root)).Concat(ProxyNodes(Graph.Objects[state.Parent]))
            .Concat(ProxyNodes(Graph.Objects[state.Child])).Distinct())
        {
            if (node.Event == 4 && !transforms.Contains(node) && !bounds.Contains(node)) state.PinnedData[node] = ProxyCurrentData(node);
            if (node.Event == 2) state.PinnedChildren[node] = ProxyCurrentChildren(node);
        }
    }
    private byte[] ProxyCurrentData(CompiledElement field)
        => (EffectivePlans().GetValueOrDefault(Graph.Owner(field))?.Values.GetValueOrDefault(field) ?? Graph.Data(field).ToArray()).ToArray();
    private CompiledElement[] ProxyCurrentChildren(CompiledElement node)
        => (EffectivePlans().GetValueOrDefault(Graph.Owner(node))?.Children.GetValueOrDefault(node) ?? node.Children).ToArray();
    private string? ValidateProxyInvariants(ProxyRuntimeState state)
    {
        // Revisions are a quick common path, but semantic validation permits unrelated native objects
        // sharing a GENR to change without granting access to any protected proxy-owned field.
        if (state.StructuralRevision == _proxyStructuralRevision && state.BoundsRevision == (NativeLods?.BoundsRevision ?? -1)
            && state.Revisions.All(p => p.Key.Revision == p.Value)) return null;
        if (state.PinnedData.Any(p => !ProxyCurrentData(p.Key).AsSpan().SequenceEqual(p.Value))
            || state.PinnedChildren.Any(p => !ProxyCurrentChildren(p.Key).SequenceEqual(p.Value))
            || state.ExpectedTransforms.Any(p => !Graph.Data(p.Key).SequenceEqual(p.Value))
            || state.ExpectedBounds.Any(p => !Graph.Data(p.Key).SequenceEqual(p.Value)) || !ProxyBoundsCoherent())
            return "A prepared proxy field changed outside its paired transform transaction. Reload or undo that change.";
        state.AcceptRevisions(); state.StructuralRevision = _proxyStructuralRevision; state.BoundsRevision = NativeLods?.BoundsRevision ?? -1; return null;
    }
    private void AcceptProxyTransformState(IReadOnlyList<ProxyRuntimeState> states)
    {
        foreach (var state in states)
        {
            foreach (var field in state.ExpectedTransforms.Keys.ToArray()) state.ExpectedTransforms[field] = Graph.Data(field).ToArray();
            foreach (var field in state.ExpectedBounds.Keys.ToArray()) state.ExpectedBounds[field] = Graph.Data(field).ToArray();
            state.AcceptRevisions();
            state.StructuralRevision = _proxyStructuralRevision;
            state.BoundsRevision = NativeLods?.BoundsRevision ?? -1;
        }
    }
    private bool ProxyBoundsCoherent()
    {
        if (NativeLods is not { } graph) return false;
        foreach (var group in graph.Groups)
            if (!_nativeLodGroupFields.TryGetValue(group.Id, out var field)
                || !Graph.Data(field).SequenceEqual(NativeBoundsBytes(new(group.WorldMin, group.WorldMax)))) return false;
        return true;
    }
}
