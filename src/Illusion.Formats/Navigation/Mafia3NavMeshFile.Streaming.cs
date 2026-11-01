using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Navigation;

public sealed record Mafia3NavigationMeshConnection(uint ThisUid, uint OppositeUid, int Face, int Edge, int OppositeFace, int OppositeEdge);
public sealed record Mafia3NavigationGraphConnection(uint ThisUid, uint OppositeUid, int Node, int OppositeNode, uint EdgeData, ushort Cost, int CostOffset = 0);
public sealed record Mafia3NavigationStreaming(IReadOnlyList<Mafia3NavigationMeshConnection> Mesh,
    IReadOnlyList<Mafia3NavigationGraphConnection> Graph, IReadOnlyList<uint> PotentialNeighbors);

public sealed partial class Mafia3NavMeshFile
{
    public IReadOnlyList<Vector3> ReadClusterRepresentatives()
    {
        var layout = new TranslationLayout(_bytes);
        if (layout.Variants.Count < 2 || layout.Variants[1].Type != "hkaiDirectedGraphExplicitCost")
            throw new NotSupportedException("Navigation does not contain the supported cluster graph.");
        var positions = layout.Array(layout.Variants[1], 16, 16);
        return Enumerable.Range(0, positions.Count).Select(i =>
        {
            int at = positions.Address + i * 16;
            var value = new Vector3(BitConverter.ToSingle(_bytes, at), BitConverter.ToSingle(_bytes, at + 4), BitConverter.ToSingle(_bytes, at + 8));
            if (!Mafia3AiWorldFile.Finite(value)) throw new InvalidDataException("Non-finite navigation cluster representative.");
            return value;
        }).ToArray();
    }

    public IReadOnlyList<int> StreamingSeamVertices(Mafia3NavMeshFile peer, int limit = 128)
    {
        if (limit is < 1 or > 200000) throw new ArgumentOutOfRangeException(nameof(limit));
        _ = ValidateStreamingPair(peer);
        var layout = new TranslationLayout(_bytes); var edges = layout.Array(layout.Variants[0], 32, 20);
        return ReadStreaming().Mesh.Where(c => c.OppositeUid == peer.ResourceId)
            .SelectMany(c => new[] { layout.Int(edges.Address + c.Edge * 20), layout.Int(edges.Address + c.Edge * 20 + 4) })
            .Distinct().Order().Take(limit).ToArray();
    }

    /// <summary>Reads native streaming identities and explicit links without changing geometry.</summary>
    public Mafia3NavigationStreaming ReadStreaming()
    {
        var layout = new TranslationLayout(_bytes);
        if (layout.Variants.Count < 2 || layout.Variants[0].Type != "hkaiNavMesh" || layout.Variants[1].Type != "hkaiDirectedGraphExplicitCost")
            throw new NotSupportedException("Streaming inspection requires the native mesh/graph pair.");
        var mesh = new List<Mafia3NavigationMeshConnection>(); var graph = new List<Mafia3NavigationGraphConnection>(); var neighbors = new HashSet<uint>();
        foreach (var sets in new[] { layout.Array(layout.Variants[0], 64, 56), layout.Array(layout.Variants[1], 104, 56) })
            for (int s = 0; s < sets.Count; s++)
            {
                int set = sets.Address + s * 56; uint own = unchecked((uint)layout.Int(set)), other = unchecked((uint)layout.Int(set + 4));
                if (own != ResourceId) throw new NotSupportedException("Streaming set identity differs from its resource UID.");
                neighbors.Add(other);
                var edges = layout.ArrayAt(set + 8, 16);
                for (int i = 0; i < edges.Count; i++)
                {
                    int at = edges.Address + i * 16;
                    mesh.Add(new(own, other, layout.Int(at), layout.Int(at + 4), layout.Int(at + 8), layout.Int(at + 12)));
                }
                var nodes = layout.ArrayAt(set + 24, 16);
                for (int i = 0; i < nodes.Count; i++)
                {
                    int at = nodes.Address + i * 16;
                    graph.Add(new(own, other, layout.Int(at), layout.Int(at + 4), unchecked((uint)layout.Int(at + 8)),
                        BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(at + 12)), at + 12));
                }
                if (layout.ArrayAt(set + 40, 8).Count != 0) throw new NotSupportedException("Navigation volume streaming links need their own reader.");
            }
        return new(mesh, graph, neighbors.Order().ToArray());
    }
}

public sealed partial class Mafia3NavMeshFile
{
    /// <summary>Proves the existing mutual seam and graph metadata; does not imply all external neighbors are loaded.</summary>
    public (int MeshLinks, int GraphLinks) ValidateStreamingPair(Mafia3NavMeshFile peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (ResourceId == peer.ResourceId) throw new InvalidDataException("Streaming peers must have distinct resource UIDs.");
        var own = ReadStreaming(); var other = peer.ReadStreaming();
        var a = new TranslationLayout(_bytes); var b = new TranslationLayout(peer._bytes);
        var ae = a.Array(a.Variants[0], 32, 20); var be = b.Array(b.Variants[0], 32, 20);
        var af = a.Array(a.Variants[0], 16, 16); var bf = b.Array(b.Variants[0], 16, 16);
        var links = own.Mesh.Where(c => c.OppositeUid == peer.ResourceId).ToArray();
        var reverse = other.Mesh.Where(c => c.OppositeUid == ResourceId).ToArray();
        if (links.Length != reverse.Length || links.Select(c => c.Edge).Distinct().Count() != links.Length || reverse.Select(c => c.Edge).Distinct().Count() != reverse.Length)
            throw new InvalidDataException("Streaming seam contains duplicate or asymmetric edge records.");
        foreach (var link in links)
        {
            if ((uint)link.Edge >= ae.Count || (uint)link.OppositeEdge >= be.Count ||
                (uint)link.Face >= af.Count || (uint)link.OppositeFace >= bf.Count ||
                reverse.Count(c => c.Face == link.OppositeFace && c.Edge == link.OppositeEdge && c.OppositeFace == link.Face && c.OppositeEdge == link.Edge) != 1)
                throw new InvalidDataException("Streaming mesh connection lacks an exact reciprocal record.");
            static bool Owns(TranslationLayout layout, (int Address, int Count) faces, int face, int edge, byte[] bytes)
            {
                int p = faces.Address + face * 16, first = layout.Int(p);
                int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 8));
                return edge >= first && edge < first + count;
            }
            if (!Owns(a, af, link.Face, link.Edge, _bytes) || !Owns(b, bf, link.OppositeFace, link.OppositeEdge, peer._bytes))
                throw new InvalidDataException("Streaming face does not own its linked edge.");
            int ea = ae.Address + link.Edge * 20, eb = be.Address + link.OppositeEdge * 20;
            int a0 = a.Int(ea), a1 = a.Int(ea + 4), b0 = b.Int(eb), b1 = b.Int(eb + 4);
            var av = Meshes.Single().Vertices; var bv = peer.Meshes.Single().Vertices;
            if ((uint)a0 >= av.Count || (uint)a1 >= av.Count || (uint)b0 >= bv.Count || (uint)b1 >= bv.Count ||
                av[a0] != bv[b1] || av[a1] != bv[b0])
                throw new NotSupportedException("Streaming seam endpoints are not exactly coincident in reversed order.");
        }
        var graph = own.Graph.Where(c => c.OppositeUid == peer.ResourceId).ToArray();
        var back = other.Graph.Where(c => c.OppositeUid == ResourceId).ToArray();
        int ownNodes = a.Array(a.Variants[1], 32, 8).Count, peerNodes = b.Array(b.Variants[1], 32, 8).Count;
        if (graph.Any(c => (uint)c.Node >= ownNodes || (uint)c.OppositeNode >= peerNodes || (c.Cost & 0x7F80) == 0x7F80) ||
            back.Any(c => (uint)c.Node >= peerNodes || (uint)c.OppositeNode >= ownNodes || (c.Cost & 0x7F80) == 0x7F80))
            throw new InvalidDataException("Streaming graph references an invalid node or non-finite cost.");
        if (graph.Length != back.Length || graph.Any(c => back.Count(r => r.Node == c.OppositeNode && r.OppositeNode == c.Node && r.EdgeData == c.EdgeData && r.Cost == c.Cost) != 1))
            throw new InvalidDataException("Streaming graph metadata lacks an exact reciprocal record.");
        return (links.Length, graph.Length);
    }
}
