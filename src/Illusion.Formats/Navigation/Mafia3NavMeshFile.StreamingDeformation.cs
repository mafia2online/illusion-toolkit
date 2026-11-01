using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Navigation;

public sealed record Mafia3NavigationStreamingPlan(Mafia3NavigationCookPlan Source, Mafia3NavigationCookPlan Peer);

public sealed partial class Mafia3NavMeshFile
{
    /// <summary>Prepares a coincident fixed-topology seam edit. Every incident boundary edge must
    /// belong to this exact reciprocal pair; open boundaries and unloaded third sections remain protected.</summary>
    public Mafia3NavigationStreamingPlan PrepareStreamingDeformation(Mafia3NavMeshFile peer, IReadOnlyDictionary<int, Vector3> edits, bool moveRepresentatives = false)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (ValidateStreamingPair(peer).MeshLinks == 0) throw new NotSupportedException("Sections do not share an explicit navigation seam.");
        var a = new TranslationLayout(_bytes); var b = new TranslationLayout(peer._bytes);
        var ae = a.Array(a.Variants[0], 32, 20); var be = b.Array(b.Variants[0], 32, 20);
        var mapping = new Dictionary<int, HashSet<int>>();
        var ownLinks = ReadStreaming().Mesh.Where(c => c.OppositeUid == peer.ResourceId).ToArray();
        foreach (var link in ownLinks)
        {
            int ea = ae.Address + link.Edge * 20, eb = be.Address + link.OppositeEdge * 20;
            foreach (var (x, y) in new[] { (a.Int(ea), b.Int(eb + 4)), (a.Int(ea + 4), b.Int(eb)) })
            {
                if (!mapping.TryGetValue(x, out var values)) mapping.Add(x, values = []);
                values.Add(y);
            }
        }
        var peerEdits = new Dictionary<int, Vector3>();
        foreach (var (vertex, position) in edits)
        {
            if (!mapping.TryGetValue(vertex, out var targets))
            {
                if (moveRepresentatives) continue; // The common validator still refuses every unpaired boundary.
                throw new NotSupportedException("Joint edits must select shared seam vertices.");
            }
            foreach (int target in targets)
                if (peerEdits.TryGetValue(target, out var prior) && prior != position) throw new NotSupportedException("Conflicting seam vertex positions.");
                else peerEdits[target] = position;
        }
        static HashSet<int> Permitted(Mafia3NavMeshFile file, TranslationLayout layout, (int Address, int Count) edges, uint peerId)
        {
            var links = file.ReadStreaming().Mesh.Where(c => c.OppositeUid == peerId).Select(c => c.Edge).ToHashSet();
            var permitted = new HashSet<int>(); var forbidden = new HashSet<int>();
            int faceCount = layout.Array(layout.Variants[0], 16, 16).Count;
            for (int i = 0; i < edges.Count; i++)
            {
                int p = edges.Address + i * 20, x = layout.Int(p), y = layout.Int(p + 4);
                bool boundary = (uint)layout.Int(p + 8) >= edges.Count || (uint)layout.Int(p + 12) >= faceCount || (file._bytes[p + 16] & 0x60) != 0;
                if (!boundary) continue;
                var set = links.Contains(i) && (file._bytes[p + 16] & 0x60) == 0 ? permitted : forbidden;
                set.Add(x); set.Add(y);
            }
            permitted.ExceptWith(forbidden); return permitted;
        }
        return new(PrepareDeformation(edits, Permitted(this, a, ae, peer.ResourceId)),
            peer.PrepareDeformation(peerEdits, Permitted(peer, b, be, ResourceId)));
    }

    /// <summary>Applies a validated two-section worker result as one reversible edit.</summary>
    public Mafia3NavigationStreamingEdit ApplyStreamingCook(Mafia3NavMeshFile peer, IReadOnlyDictionary<int, Vector3> edits,
        byte[] cooked, byte[] cookedPeer, bool moveRepresentatives = false)
    {
        var plan = PrepareStreamingDeformation(peer, edits, moveRepresentatives);
        static HashSet<int> Moved(Mafia3NavigationCookPlan plan, byte[] result)
        {
            if (result.Length != plan.Baseline.Length) throw new InvalidDataException("Joint cook resized a fixed-topology section.");
            return Enumerable.Range(0, plan.ClusterCount).Where(c => !result.AsSpan(plan.GraphPositions + c * 16, 12)
                .SequenceEqual(plan.Baseline.AsSpan(plan.GraphPositions + c * 16, 12))).ToHashSet();
        }
        var moved = Moved(plan.Source, cooked); var movedPeer = Moved(plan.Peer, cookedPeer);
        if (!moveRepresentatives && (moved.Count != 0 || movedPeer.Count != 0)) throw new NotSupportedException("Cluster representatives are protected in strict joint mode.");
        static Mafia3NavigationTranslation Validate(Mafia3NavMeshFile owner, uint peerId, Mafia3NavigationCookPlan plan, byte[] result, HashSet<int> moved, HashSet<int> movedPeer)
        {
            if (result.Length != plan.Baseline.Length) throw new InvalidDataException("Joint cook resized a fixed-topology section.");
            _ = Read(result);
            var allowed = new bool[result.Length];
            for (int e = 0; e < plan.GraphEdgeCount; e++) Array.Fill(allowed, true, plan.GraphEdges + e * 8, 2);
            foreach (int node in moved)
            {
                int at = plan.GraphPositions + node * 16;
                if (!Mafia3AiWorldFile.Finite(new Vector3(BitConverter.ToSingle(result, at), BitConverter.ToSingle(result, at + 4), BitConverter.ToSingle(result, at + 8))))
                    throw new InvalidDataException("Joint cook returned a non-finite representative.");
                Array.Fill(allowed, true, at, 12);
            }
            foreach (var link in owner.ReadStreaming().Graph)
            {
                if (moved.Contains(link.Node) && link.OppositeUid != peerId) throw new NotSupportedException("A moved cluster connects to an unloaded third section.");
                if (link.OppositeUid == peerId && (moved.Contains(link.Node) || movedPeer.Contains(link.OppositeNode)))
                {
                    ushort cost = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(link.CostOffset));
                    if ((cost & 0xFF80) >= 0x7F80) throw new InvalidDataException("Joint cook returned an invalid streaming cost.");
                    Array.Fill(allowed, true, link.CostOffset, 2);
                }
            }
            Array.Fill(allowed, true, plan.RootCount, 4);
            for (int i = 0; i < result.Length; i++)
                if (!allowed[i] && result[i] != plan.Modified[i]) throw new InvalidDataException("Joint cook changed unsupported metadata or requested geometry.");
            if (BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(plan.RootCount)) != 2)
                throw new InvalidDataException("Joint cook retained stale accelerators.");
            var patches = new List<NavigationBytePatch>();
            for (int i = 0; i < result.Length;)
            {
                if (result[i] == plan.Baseline[i]) { i++; continue; }
                int first = i++;
                while (i < result.Length && result[i] != plan.Baseline[i]) i++;
                patches.Add(new(first, plan.Baseline.AsSpan(first, i - first).ToArray(), result.AsSpan(first, i - first).ToArray()));
            }
            return new(owner, patches);
        }
        var first = Validate(this, peer.ResourceId, plan.Source, cooked, moved, movedPeer);
        var second = Validate(peer, ResourceId, plan.Peer, cookedPeer, movedPeer, moved);
        // Whitelisting individual native cost slots is not enough: both returned resources
        // must still agree on reciprocal records before either canonical file changes.
        _ = Read(cooked).ValidateStreamingPair(Read(cookedPeer));
        var result = new Mafia3NavigationStreamingEdit(first, second); result.Redo(); return result;
    }
}

public sealed class Mafia3NavigationStreamingEdit(Mafia3NavigationTranslation source, Mafia3NavigationTranslation peer)
{
    public void Undo() { peer.Undo(); source.Undo(); }
    public void Redo() { source.Redo(); try { peer.Redo(); } catch { source.Undo(); throw; } }
}
