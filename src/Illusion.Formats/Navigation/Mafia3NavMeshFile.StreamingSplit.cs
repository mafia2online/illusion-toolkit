using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Navigation;

public sealed record Mafia3NavigationStreamingSplitPlan(Mafia3NavigationCookPlan Source, Mafia3NavigationCookPlan Peer,
    int SourceFace, int PeerFace, int SourceNewEdge, int PeerNewEdge);

public sealed partial class Mafia3NavMeshFile
{
    public IReadOnlyList<int> StreamingEdgeSplitCandidates(Mafia3NavMeshFile peer, int limit = 32)
    {
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = new List<int>();
        foreach (var link in ReadStreaming().Mesh.Where(c => c.OppositeUid == peer.ResourceId))
        {
            try { _ = PrepareStreamingEdgeSplit(peer, link.Edge, null, false); result.Add(link.Edge); }
            catch (NotSupportedException) { }
            if (result.Count >= limit) break;
        }
        return result;
    }

    /// <summary>Splits one reciprocal edge while retaining both original streaming identities.
    /// Clips one corner from each convex face into an appended triangle.</summary>
    public Mafia3NavigationStreamingSplitPlan PrepareStreamingEdgeSplit(Mafia3NavMeshFile peer, int edgeIndex, Vector3? position = null)
        => PrepareStreamingEdgeSplit(peer, edgeIndex, position, true);

    private Mafia3NavigationStreamingSplitPlan PrepareStreamingEdgeSplit(Mafia3NavMeshFile peer, int edgeIndex, Vector3? position, bool write)
    {
        _ = ValidateStreamingPair(peer);
        var connection = ReadStreaming().Mesh.SingleOrDefault(c => c.Edge == edgeIndex && c.OppositeUid == peer.ResourceId)
            ?? throw new NotSupportedException("Select an explicit edge connection to this loaded peer.");
        var a = PrepareInteriorDeformation(new Dictionary<int, Vector3>()); var b = peer.PrepareInteriorDeformation(new Dictionary<int, Vector3>());
        int clusterA = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(a.Faces + connection.Face * 16 + 12));
        int clusterB = BinaryPrimitives.ReadUInt16LittleEndian(peer._bytes.AsSpan(b.Faces + connection.OppositeFace * 16 + 12));
        if (ReadStreaming().Graph.Count(c => c.Node == clusterA && c.OppositeNode == clusterB && c.OppositeUid == peer.ResourceId) != 1 ||
            peer.ReadStreaming().Graph.Count(c => c.Node == clusterB && c.OppositeNode == clusterA && c.OppositeUid == ResourceId) != 1)
            throw new NotSupportedException("Splitting requires the exact existing reciprocal cluster connection.");
        int edge = a.Edges + edgeIndex * 20;
        Vector3 Vertex(int id) => new(BitConverter.ToSingle(_bytes, a.Vertices + id * 16), BitConverter.ToSingle(_bytes, a.Vertices + id * 16 + 4), BitConverter.ToSingle(_bytes, a.Vertices + id * 16 + 8));
        Vector3 from = Vertex(BitConverter.ToInt32(_bytes, edge)), to = Vertex(BitConverter.ToInt32(_bytes, edge + 4));
        Vector3 point = position ?? from + (to - from) * .5f, delta = to - from;
        float length = delta.LengthSquared(), fraction = Vector3.Dot(point - from, delta) / length;
        if (!Mafia3AiWorldFile.Finite(point) || !float.IsFinite(fraction) || length < .000001f || fraction <= .0001f || fraction >= .9999f || Vector3.DistanceSquared(point, from + delta * fraction) > .00000001f)
            throw new NotSupportedException("The split point must lie strictly inside the original shared edge.");
        return new(SplitStreamingFace(a, connection.Face, edgeIndex, peer.ResourceId, connection.OppositeFace, connection.OppositeEdge,
                b.FaceCount, b.EdgeCount + 2, point, true, write),
            peer.SplitStreamingFace(b, connection.OppositeFace, connection.OppositeEdge, ResourceId, connection.Face, edgeIndex,
                a.FaceCount, a.EdgeCount, point, false, write), connection.Face, connection.OppositeFace, a.EdgeCount, b.EdgeCount + 2);
    }

    private Mafia3NavigationCookPlan SplitStreamingFace(Mafia3NavigationCookPlan plan, int faceIndex, int selected, uint peerUid,
        int peerFace, int peerEdge, int newPeerFace, int newPeerEdge, Vector3 point, bool keepStart, bool write)
    {
        if (plan.FaceCount >= 100000 || plan.VertexCount >= 200000 || plan.EdgeCount > 599997)
            throw new NotSupportedException("Stream splitting exceeds bounded native cook limits.");
        var layout = new TranslationLayout(_bytes); var mesh = layout.Variants[0]; var graph = layout.Variants[1];
        int face = plan.Faces + faceIndex * 16, first = layout.Int(face);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(face + 8));
        if (count is < 3 or > 64 || BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(face + 10)) != 0 || selected < first || selected >= first + count)
            throw new NotSupportedException("Streaming split requires a bounded ordinary polygon.");
        int slot = selected - first, next = first + (slot + 1) % count, previous = first + (slot + count - 1) % count;
        int replaced = keepStart ? next : previous;
        int cluster = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(face + 12));
        if (ReadStreaming().Graph.Count(c => c.Node == cluster && c.OppositeUid == peerUid) == 0)
            throw new NotSupportedException("The streamed face has no original graph connection to its peer.");
        if (ReadStreaming().Mesh.Any(c => (c.Face == faceIndex || c.Edge >= first && c.Edge < first + count) &&
            !(c.Edge == selected && c.Face == faceIndex && c.OppositeUid == peerUid && c.OppositeFace == peerFace && c.OppositeEdge == peerEdge)))
            throw new NotSupportedException("The split patch touches another streaming connection.");
        var edgeData = layout.Array(mesh, 96, 4); int edgeStride = layout.Int(mesh.Object + 116);
        int At(int e) => plan.Edges + e * 20;
        for (int i = 0; i < count; i++)
        {
            int e = first + i, at = At(e);
            int vertex = layout.Int(at);
            if ((uint)vertex >= plan.VertexCount || BitConverter.ToSingle(_bytes, plan.Vertices + vertex * 16 + 12) != 0)
                throw new NotSupportedException("Streaming split requires ordinary zero-W native vertices.");
            if (layout.Int(at + 4) != layout.Int(At(first + (i + 1) % count)) || _bytes[at + 16] is not (0 or 4) ||
                !_bytes.AsSpan(at + 16, 4).SequenceEqual(_bytes.AsSpan(At(selected) + 16, 4)) ||
                !_bytes.AsSpan(edgeData.Address + e * edgeStride * 4, edgeStride * 4).SequenceEqual(_bytes.AsSpan(edgeData.Address + selected * edgeStride * 4, edgeStride * 4)))
                throw new NotSupportedException("Streaming split contains unsupported edge flags or unequal user data.");
            int opposite = layout.Int(at + 8), owner = layout.Int(at + 12);
            if (e == selected)
            {
                if (opposite != -1 || owner != -1) throw new NotSupportedException("The original stream edge must be uninstanced.");
                continue;
            }
            if ((uint)opposite >= plan.EdgeCount || (uint)owner >= plan.FaceCount || opposite >= first && opposite < first + count ||
                layout.Int(At(opposite) + 8) != e || layout.Int(At(opposite) + 12) != faceIndex ||
                layout.Int(At(opposite)) != layout.Int(at + 4) || layout.Int(At(opposite) + 4) != layout.Int(at))
                throw new NotSupportedException("Streaming split requires a closed reciprocal ordinary patch around its selected seam.");
            int neighbor = plan.Faces + owner * 16, neighborFirst = layout.Int(neighbor), neighborCount = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(neighbor + 8));
            if (opposite < neighborFirst || opposite >= neighborFirst + neighborCount) throw new InvalidDataException("Streaming neighbor has the wrong owning face.");
        }
        Vector3 V(int id) => new(BitConverter.ToSingle(_bytes, plan.Vertices + id * 16), BitConverter.ToSingle(_bytes, plan.Vertices + id * 16 + 4), BitConverter.ToSingle(_bytes, plan.Vertices + id * 16 + 8));
        var polygon = Enumerable.Range(first, count).Select(e => V(layout.Int(At(e)))).ToArray();
        var changed = polygon.ToArray(); changed[keepStart ? (slot + 1) % count : slot] = point;
        Vector3 normal = Vector3.Zero;
        for (int i = 1; i < count - 1; i++) normal += Vector3.Cross(polygon[i] - polygon[0], polygon[i + 1] - polygon[0]);
        if (!Mafia3AiWorldFile.Finite(normal) || normal.LengthSquared() < 1e-12f) throw new NotSupportedException("Degenerate original streaming polygon.");
        normal = Vector3.Normalize(normal);
        void Convex(Vector3[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (MathF.Abs(Vector3.Dot(values[i] - values[0], normal)) > .001f ||
                    Vector3.Dot(Vector3.Cross(values[(i + 1) % values.Length] - values[i], values[(i + 2) % values.Length] - values[(i + 1) % values.Length]), normal) <= 1e-8f)
                    throw new NotSupportedException("Streaming split needs strictly convex planar source and resulting polygons.");
                for (int j = 0; j < values.Length; j++)
                    if (j != i && j != (i + 1) % values.Length && Vector3.Dot(Vector3.Cross(values[(i + 1) % values.Length] - values[i], values[j] - values[i]), normal) <= 1e-8f)
                        throw new NotSupportedException("Streaming split polygon is not globally convex.");
            }
        }
        Convex(polygon); Convex(changed);
        int startId = layout.Int(At(selected)), endId = layout.Int(At(selected) + 4);
        int corner = keepStart ? layout.Int(At(next) + 4) : layout.Int(At(previous));
        Convex(keepStart ? [point, V(endId), V(corner)] : [point, V(corner), V(startId)]);
        if (!write) return plan;
        byte[] vertices = new byte[(plan.VertexCount + 1) * 16]; _bytes.AsSpan(plan.Vertices, plan.VertexCount * 16).CopyTo(vertices);
        BinaryPrimitives.WriteSingleLittleEndian(vertices.AsSpan(plan.VertexCount * 16), point.X);
        BinaryPrimitives.WriteSingleLittleEndian(vertices.AsSpan(plan.VertexCount * 16 + 4), point.Y);
        BinaryPrimitives.WriteSingleLittleEndian(vertices.AsSpan(plan.VertexCount * 16 + 8), point.Z);
        byte[] faces = new byte[(plan.FaceCount + 1) * 16]; _bytes.AsSpan(plan.Faces, plan.FaceCount * 16).CopyTo(faces);
        _bytes.AsSpan(face, 16).CopyTo(faces.AsSpan(plan.FaceCount * 16));
        BinaryPrimitives.WriteInt32LittleEndian(faces.AsSpan(plan.FaceCount * 16), plan.EdgeCount);
        BinaryPrimitives.WriteUInt16LittleEndian(faces.AsSpan(plan.FaceCount * 16 + 8), 3);
        byte[] edges = new byte[(plan.EdgeCount + 3) * 20]; _bytes.AsSpan(plan.Edges, plan.EdgeCount * 20).CopyTo(edges);
        void Int(int e, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(edges.AsSpan(e * 20 + offset), value);
        for (int i = 0; i < 3; i++) _bytes.AsSpan(At(selected) + 16, 4).CopyTo(edges.AsSpan((plan.EdgeCount + i) * 20 + 16));
        int streamNew = plan.EdgeCount + (keepStart ? 0 : 2), internalNew = plan.EdgeCount + (keepStart ? 2 : 0), boundaryNew = plan.EdgeCount + 1;
        if (keepStart)
        {
            Int(selected, 4, plan.VertexCount); Int(replaced, 0, plan.VertexCount);
            Int(streamNew, 0, plan.VertexCount); Int(streamNew, 4, endId);
            Int(internalNew, 0, corner); Int(internalNew, 4, plan.VertexCount);
        }
        else
        {
            Int(selected, 0, plan.VertexCount); Int(replaced, 4, plan.VertexCount);
            Int(streamNew, 0, startId); Int(streamNew, 4, plan.VertexCount);
            Int(internalNew, 0, plan.VertexCount); Int(internalNew, 4, corner);
        }
        _bytes.AsSpan(At(replaced), 20).CopyTo(edges.AsSpan(boundaryNew * 20));
        int oldNeighbor = layout.Int(At(replaced) + 8);
        Int(oldNeighbor, 8, boundaryNew); Int(oldNeighbor, 12, plan.FaceCount);
        Int(replaced, 8, internalNew); Int(replaced, 12, plan.FaceCount);
        Int(internalNew, 8, replaced); Int(internalNew, 12, faceIndex);
        Int(streamNew, 8, -1); Int(streamNew, 12, -1);
        var append = new NavigationPackAppend(_bytes);
        append.Array(mesh.Object + 16, faces, plan.FaceCount + 1); append.Array(mesh.Object + 32, edges, plan.EdgeCount + 3); append.Array(mesh.Object + 48, vertices, plan.VertexCount + 1);
        foreach (int header in new[] { 80, 96 })
        {
            int stride = layout.Int(mesh.Object + (header == 80 ? 112 : 116)); if (stride == 0) continue;
            int oldCount = header == 80 ? plan.FaceCount : plan.EdgeCount, added = header == 80 ? 1 : 3, source = header == 80 ? faceIndex : selected;
            var old = layout.Array(mesh, header, 4); byte[] data = new byte[(oldCount + added) * stride * 4];
            _bytes.AsSpan(old.Address, oldCount * stride * 4).CopyTo(data);
            for (int i = 0; i < added; i++) _bytes.AsSpan(old.Address + source * stride * 4, stride * 4).CopyTo(data.AsSpan((oldCount + i) * stride * 4));
            append.Array(mesh.Object + header, data, (oldCount + added) * stride);
        }
        int found = 0;
        foreach (var sets in new[] { layout.Array(mesh, 64, 56), layout.Array(graph, 104, 56) })
            for (int i = 0; i < sets.Count; i++)
            {
                int set = sets.Address + i * 56; if (unchecked((uint)layout.Int(set + 4)) != peerUid) continue;
                var old = layout.ArrayAt(set + 8, 16);
                if (!Enumerable.Range(0, old.Count).Any(c => layout.Int(old.Address + c * 16) == faceIndex && layout.Int(old.Address + c * 16 + 4) == selected)) continue;
                byte[] records = new byte[(old.Count + 1) * 16]; _bytes.AsSpan(old.Address, old.Count * 16).CopyTo(records);
                int at = old.Count * 16;
                foreach (var (offset, value) in new[] { (0, plan.FaceCount), (4, streamNew), (8, newPeerFace), (12, newPeerEdge) }) BinaryPrimitives.WriteInt32LittleEndian(records.AsSpan(at + offset), value);
                append.Array(set + 8, records, old.Count + 1); found++;
            }
        if (found != 1) throw new NotSupportedException("Streaming edge has ambiguous native owning sets.");
        append.Int(plan.RootCount, 2); byte[] modified = append.Finish();
        var updated = Read(modified).PrepareInteriorDeformation(new Dictionary<int, Vector3>());
        return plan with { Modified = modified, ModifiedLayout = updated with { RequiredFaceConnections = [faceIndex, plan.FaceCount] } };
    }
}

public sealed partial class Mafia3NavMeshFile
{
    public Mafia3NavigationStreamingGrowthEdit ApplyStreamingEdgeSplitCook(Mafia3NavMeshFile peer, int edgeIndex,
        byte[] cooked, byte[] cookedPeer, Vector3? position = null, bool moveRepresentatives = false)
    {
        var plan = PrepareStreamingEdgeSplit(peer, edgeIndex, position);
        static HashSet<int> Moved(Mafia3NavigationCookPlan p, byte[] bytes)
        {
            if (bytes.Length != p.Modified.Length) throw new InvalidDataException("Stream split resized unplanned storage.");
            return Enumerable.Range(0, p.ClusterCount).Where(c => !p.Baseline.AsSpan(p.GraphPositions + c * 16, 12)
                .SequenceEqual(bytes.AsSpan(p.ModifiedLayout!.GraphPositions + c * 16, 12))).ToHashSet();
        }
        var moved = Moved(plan.Source, cooked); var movedPeer = Moved(plan.Peer, cookedPeer);
        if (!moveRepresentatives && (moved.Count != 0 || movedPeer.Count != 0)) throw new NotSupportedException("Stream split moves a protected cluster representative; opt in to native streaming cost recooking.");
        static HashSet<int> CostFields(Mafia3NavMeshFile file, uint peerId, HashSet<int> own, HashSet<int> other)
        {
            var fields = new HashSet<int>();
            foreach (var link in file.ReadStreaming().Graph)
            {
                if (own.Contains(link.Node) && link.OppositeUid != peerId) throw new NotSupportedException("A split cluster connects to an unloaded third section.");
                if (link.OppositeUid == peerId && (own.Contains(link.Node) || other.Contains(link.OppositeNode))) fields.Add(link.CostOffset);
            }
            return fields;
        }
        var a = ApplyNavigationGrowth(plan.Source, cooked, CostFields(this, peer.ResourceId, moved, movedPeer), false);
        var b = peer.ApplyNavigationGrowth(plan.Peer, cookedPeer, CostFields(peer, ResourceId, movedPeer, moved), false);
        _ = Read(cooked).ValidateStreamingPair(Read(cookedPeer));
        var result = new Mafia3NavigationStreamingGrowthEdit(a, b); result.Redo(); return result;
    }
}

public sealed class Mafia3NavigationStreamingGrowthEdit(Mafia3NavigationReplacement source, Mafia3NavigationReplacement peer)
{
    private bool _applied;
    public void Undo() { if (!_applied) return; peer.Undo(); try { source.Undo(); _applied = false; } catch { peer.Redo(); throw; } }
    public void Redo() { if (_applied) return; source.Redo(); try { peer.Redo(); _applied = true; } catch { source.Undo(); throw; } }
}
