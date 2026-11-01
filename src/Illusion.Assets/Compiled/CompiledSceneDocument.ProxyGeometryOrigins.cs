using System.Security.Cryptography;
using System.Text.Json;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private sealed record ProxyGeometryOrigin(int Version, string Resource, string ArchiveHash, string PayloadHash, ulong[] Identities);
    private readonly Dictionary<string, byte[]> _proxyGeometryOriginBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]?> _proxyGeometryOriginBaselines = new(StringComparer.OrdinalIgnoreCase);

    private static string ProxyGeometryOriginKey(CompiledResource resource) => Path.GetRelativePath(MafiaEnvironment.ResourcesFolder!, resource.Path).Replace('\\', '/');
    private static string ProxyGeometryOriginPath(string hash) => Path.Combine(CompiledProxySaveJournal.Workspace, "geometry-origins", hash + ".json");

    private HashSet<ulong>? ReadProxyGeometryOrigin(CompiledResource resource, ProxyRuntimeState? state = null)
    {
        string key = ProxyGeometryOriginKey(resource);
        var hashes = state is not null && state.GeometryOrigins.TryGetValue(key, out var selected) ? new[] { selected }
            : _linkedProxyStates.Values.Select(owner => owner.GeometryOrigins.GetValueOrDefault(key)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string hash in hashes)
        {
            if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character))) continue;
            string path = ProxyGeometryOriginPath(hash);
            if (!_proxyGeometryOriginBytes.TryGetValue(hash, out var bytes))
            {
                if (!File.Exists(path)) continue;
                bytes = File.ReadAllBytes(path);
            }
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase)) continue;
            ProxyGeometryOrigin? record;
            try { record = JsonSerializer.Deserialize<ProxyGeometryOrigin>(bytes); }
            catch (JsonException) { continue; }
            if (record is null || record.Version != 1 || !string.Equals(record.Resource, key, StringComparison.OrdinalIgnoreCase)
                || record.ArchiveHash is not { Length: 64 } || record.PayloadHash is not { Length: 64 } || record.Identities is null
                || record.Identities.Length == 0 || record.Identities.Distinct().Count() != record.Identities.Length) continue;
            TrackProxyGeometryOrigin(hash, bytes); _stockProxyOriginHashes[resource] = hash; return record.Identities.ToHashSet();
        }
        return null;
    }

    private void TrackProxyGeometryOrigin(string hash, byte[] bytes)
    {
        string path = ProxyGeometryOriginPath(hash);
        if (!_proxyGeometryOriginBaselines.ContainsKey(path))
        {
            _proxyGeometryOriginBaselines.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
            RegisterProxyGeometryOriginSnapshot(path);
        }
        _proxyGeometryOriginBytes[hash] = bytes;
    }

    private string CaptureProxyGeometryOrigin(CompiledResource resource, HashSet<ulong> ids, byte[] nativePayload)
    {
        if (!_archives.TryGetValue(resource, out var archive)) throw new InvalidDataException("Missing original archive provenance.");
        using var source = archive.OpenRead();
        var record = new ProxyGeometryOrigin(1, ProxyGeometryOriginKey(resource), Convert.ToHexString(SHA256.HashData(source)),
            Convert.ToHexString(SHA256.HashData(nativePayload)), ids.Order().ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record); string hash = Convert.ToHexString(SHA256.HashData(bytes));
        TrackProxyGeometryOrigin(hash, bytes); return hash;
    }

    private void AttachProxyGeometryOrigins(ProxyRuntimeState state, CompiledElement visual)
    {
        foreach (var resource in ProxyGeometryIdentityClosure(visual).Select(Graph.Owner).Distinct())
        {
            string key = ProxyGeometryOriginKey(resource);
            // Resolve a valid prior snapshot before reading a possibly rebuilt source archive.
            if (ReadProxyGeometryOrigin(resource, state) is not null && _stockProxyOriginHashes.TryGetValue(resource, out var saved))
            { state.GeometryOrigins[key] = saved; continue; }
            _ = StockProxyResourceIdentities(resource, state);
            if (_stockProxyOriginHashes.TryGetValue(resource, out var hash)) state.GeometryOrigins[key] = hash;
        }
    }

    partial void CollectProxyGeometryOriginPaths(List<string> paths) => paths.AddRange(_proxyGeometryOriginBaselines.Keys);

    partial void FlushProxyGeometryOrigins()
    {
        var active = _linkedProxyStates.Values.SelectMany(state => state.GeometryOrigins.Values).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _proxyGeometryOriginBaselines)
        {
            string hash = Path.GetFileNameWithoutExtension(pair.Key);
            if (active.Contains(hash))
            {
                if (!_proxyGeometryOriginBytes.TryGetValue(hash, out var bytes))
                    throw new InvalidDataException("Missing original native geometry identity evidence.");
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                AtomicFile.WriteAllBytes(pair.Key, bytes);
            }
            else if (pair.Value is { } baseline) AtomicFile.WriteAllBytes(pair.Key, baseline);
            else if (File.Exists(pair.Key)) File.Delete(pair.Key);
        }
    }
}
