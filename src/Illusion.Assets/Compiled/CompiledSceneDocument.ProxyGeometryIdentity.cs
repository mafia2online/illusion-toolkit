using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private readonly Dictionary<ulong, string> _provenProxyGeometryIdentities = [];
    private readonly Dictionary<string, (long Length, long Stamp, HashSet<ulong> Ids)> _stockProxyIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CompiledResource, string> _stockProxyOriginHashes = [];

    private IReadOnlyList<CompiledElement> ProxyGeometryIdentityClosure(CompiledElement visual)
    {
        var queue = new Queue<CompiledElement>();
        foreach (string name in new[] { "m_MeshSet", "m_FaceGroupSet" }) if (Graph.Resolve(Graph.Field(visual, name)) is { } root) queue.Enqueue(root);
        foreach (var pointer in Graph.Items(Graph.Field(visual, "m_CDSubdiv"))) if (Graph.Resolve(pointer) is { } root) queue.Enqueue(root);
        var found = new HashSet<CompiledElement>();
        while (queue.TryDequeue(out var root))
        {
            if (!found.Add(root)) continue;
            foreach (var node in Descendants(root))
                if ((node.Event == 4 || node.Event == 2 && Graph.Kind(node) == 173) && Graph.Resolve(node) is { } target
                    && (GeometryDependencyTypes.Contains(target.TypeId) || target.ObjectId == 0)) queue.Enqueue(target);
        }
        return found.ToArray();
    }

    private string ProxyGeometryIdentitySignature(CompiledElement visual)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var node in ProxyGeometryIdentityClosure(visual).SelectMany(Descendants).Distinct().Where(node => node.Event == 2)
            .OrderBy(node => node.ObjectId).ThenBy(node => node.TypeId))
        {
            hash.AppendData(BitConverter.GetBytes(node.ObjectId)); hash.AppendData(BitConverter.GetBytes(node.TypeId));
            foreach (string name in new[] { "m_ID", "m_HashId" }) if (Graph.Field(node, name) is { DataSize: 8 } field) hash.AppendData(Graph.Data(field));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Workspace metadata is only a hint. Prove current identities differ from the immutable input
    // SDS; a forged ledger naming a stock MeshSet/cache cannot authorize in-place stock mutation.
    private bool HasOwnedProxyGeometry(ProxyRuntimeState state, CompiledElement visual)
    {
        var set = Graph.Resolve(Graph.Field(visual, "m_MeshSet"));
        if (set is null || !state.GeometryOwners.TryGetValue(visual.ObjectId, out var id) || id != set.ObjectId) return false;
        string signature = ProxyGeometryIdentitySignature(visual);
        if (_provenProxyGeometryIdentities.TryGetValue(id, out var proven) && proven == signature) return true;
        try
        {
            foreach (var node in ProxyGeometryIdentityClosure(visual).SelectMany(Descendants).Distinct().Where(node => node.Event == 2))
            {
                var stock = StockProxyResourceIdentities(Graph.Owner(node), state);
                if (node.ObjectId != 0 && stock.Contains(node.ObjectId)) return false;
                foreach (string name in new[] { "m_ID", "m_HashId" })
                    if (Graph.Field(node, name) is { DataSize: 8 } field && Graph.Integer(field) is var cache && cache != 0 && stock.Contains(cache)) return false;
            }
            _provenProxyGeometryIdentities[id] = signature; return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or OverflowException)
        { return false; }
    }

    private void MarkOwnedProxyGeometry(ProxyRuntimeState state, CompiledElement visual)
    {
        var set = Graph.Resolve(Graph.Field(visual, "m_MeshSet")) ?? throw new InvalidDataException("Missing owned proxy MeshSet.");
        state.GeometryOwners[visual.ObjectId] = set.ObjectId;
        AttachProxyGeometryOrigins(state, visual);
        _provenProxyGeometryIdentities[set.ObjectId] = ProxyGeometryIdentitySignature(visual);
    }

    private HashSet<ulong> StockProxyResourceIdentities(CompiledResource resource, ProxyRuntimeState? state = null)
    {
        if (ReadProxyGeometryOrigin(resource, state) is { } prior) return prior;
        if (!_archives.TryGetValue(resource, out var archive)) throw new InvalidDataException("No immutable archive provenance for native geometry.");
        archive.Refresh(); var parts = Path.GetFileName(resource.Path).Split('_');
        if (!archive.Exists || parts.Length < 3 || !int.TryParse(parts[1], out int index)) throw new InvalidDataException("Unknown immutable Generic resource identity.");
        string key = archive.FullName + ":" + index.ToString(CultureInfo.InvariantCulture);
        if (_stockProxyIdentities.TryGetValue(key, out var existing) && existing.Length == archive.Length && existing.Stamp == archive.LastWriteTimeUtc.Ticks
            && _stockProxyOriginHashes.ContainsKey(resource)) return existing.Ids;
        var input = SdsArchive.Open(archive.FullName);
        if (index < 0 || index >= input.Entries.Count || input.ResourceTypes[input.Entries[index].TypeId].Name != "Generic") throw new InvalidDataException("Immutable Generic entry mismatch.");
        string hashText = parts[2].Split('.')[0];
        if (!ulong.TryParse(hashText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong fileHash) || input.Entries[index].FileHash != fileHash)
            throw new InvalidDataException("Immutable Generic file hash mismatch.");
        var data = input.Entries[index].Data ?? throw new InvalidDataException("Missing immutable Generic payload.");
        if (data.Length < 12) throw new InvalidDataException("Truncated immutable Generic wrapper.");
        int prefix = checked(12 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10)));
        if (prefix > data.Length) throw new InvalidDataException("Invalid immutable Generic prefix.");
        var original = CompiledResource.FromBytes(data[prefix..]); var ids = new HashSet<ulong>();
        foreach (var node in original.Elements)
        {
            if (node.Event == 2 && node.ObjectId != 0) ids.Add(node.ObjectId);
            else if (node.Event == 4 && node.DataSize is 8 or 12) ids.Add(BinaryPrimitives.ReadUInt64LittleEndian(original.Data(node)));
        }
        _stockProxyOriginHashes[resource] = CaptureProxyGeometryOrigin(resource, ids, data[prefix..]);
        _stockProxyIdentities[key] = (archive.Length, archive.LastWriteTimeUtc.Ticks, ids); return ids;
    }
}
