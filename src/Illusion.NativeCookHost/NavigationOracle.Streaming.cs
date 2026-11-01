using System.Buffers.Binary;
using System.Numerics;
using Illusion.Formats.Navigation;

namespace Illusion.NativeCookHost;

internal sealed unsafe partial class NavigationOracle
{
    public byte[][] CookStreamingPair(byte[][] resources, IReadOnlyDictionary<int, Vector3> edits, bool moveRepresentatives = false)
    {
        if (resources.Length != 2) throw new NotSupportedException("Joint deformation currently requires one reciprocal pair.");
        var source = Mafia3NavMeshFile.Read(resources[0]); var peer = Mafia3NavMeshFile.Read(resources[1]);
        var pair = source.PrepareStreamingDeformation(peer, edits, moveRepresentatives);
        var result = CookStreamingPlans(resources, [pair.Source, pair.Peer], moveRepresentatives);
        _ = source.ApplyStreamingCook(peer, edits, result[0], result[1], moveRepresentatives);
        _ = source.ValidateStreamingPair(peer);
        return result;
    }

    public byte[][] CookStreamingEdgeSplit(byte[][] resources, int edge, Vector3? position, bool moveRepresentatives)
    {
        if (resources.Length != 2) throw new NotSupportedException("Stream split requires exactly one loaded reciprocal pair.");
        var source = Mafia3NavMeshFile.Read(resources[0]); var peer = Mafia3NavMeshFile.Read(resources[1]);
        var pair = source.PrepareStreamingEdgeSplit(peer, edge, position);
        var result = CookStreamingPlans(resources, [pair.Source, pair.Peer], moveRepresentatives);
        _ = source.ApplyStreamingEdgeSplitCook(peer, edge, result[0], result[1], position, moveRepresentatives);
        _ = source.ValidateStreamingPair(peer);
        _ = ValidateStreamingGraphs(result);
        ProveStreamingHalf(result, pair.Source.FaceCount, pair.SourceNewEdge, pair.Peer.FaceCount, pair.PeerNewEdge);
        var old = source.ReadStreaming().Mesh.Single(c => c.Edge == edge && c.OppositeUid == peer.ResourceId);
        ProveStreamingHalf(result, old.Face, old.Edge, old.OppositeFace, old.OppositeEdge);
        return result;
    }

    private byte[][] CookStreamingPlans(byte[][] resources, Mafia3NavigationCookPlan[] plans, bool moveRepresentatives)
    {
        _ = ValidateStreamingGraphs(resources);
        if (moveRepresentatives) _ = ValidateStreamingCrossCosts(resources);
        var result = new byte[2][]; var files = resources.Select(bytes => Mafia3NavMeshFile.Read(bytes)).ToArray();
        var graphs = new Graph[2]; HashSet<int>[] moved = [[], []];
        for (int i = 0; i < 2; i++)
        {
            var plan = plans[i];
            var updated = plan.ModifiedLayout ?? plan;
            var before = Generate(plan.Baseline, plan, null, out var representatives, [plans[1 - i].Baseline]);
            var after = Generate(plan.Modified, updated, representatives, out _, [plans[1 - i].Modified]);
            graphs[i] = after;
            for (int c = 0; c < plan.ClusterCount; c++) if (before.Positions[c] != after.Positions[c]) moved[i].Add(c);
            if (!moveRepresentatives && moved[i].Count != 0)
                throw new NotSupportedException("Joint seam edits must preserve every cluster representative; external graph costs remain protected.");
            if (!before.Costs.Keys.ToHashSet().SetEquals(after.Costs.Keys))
                throw new NotSupportedException("Joint seam edits change cluster adjacency.");
            result[i] = plan.Modified.ToArray();
            for (int c = 0; c < plan.ClusterCount; c++)
            {
                if (moved[i].Contains(c))
                {
                    var p = after.Positions[c]; int at = updated.GraphPositions + c * 16;
                    BinaryPrimitives.WriteSingleLittleEndian(result[i].AsSpan(at), p.X);
                    BinaryPrimitives.WriteSingleLittleEndian(result[i].AsSpan(at + 4), p.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(result[i].AsSpan(at + 8), p.Z);
                }
                int first = BinaryPrimitives.ReadInt32LittleEndian(result[i].AsSpan(updated.GraphNodes + c * 8));
                int count = BinaryPrimitives.ReadInt32LittleEndian(result[i].AsSpan(updated.GraphNodes + c * 8 + 4));
                for (int e = first; e < first + count; e++)
                {
                    int at = updated.GraphEdges + e * 8;
                    int target = BinaryPrimitives.ReadInt32LittleEndian(result[i].AsSpan(at + 4));
                    BinaryPrimitives.WriteUInt16LittleEndian(result[i].AsSpan(at), after.Costs[(c, target)]);
                }
            }
            BinaryPrimitives.WriteInt32LittleEndian(result[i].AsSpan(updated.RootCount), 2);
        }
        for (int i = 0; i < 2; i++)
            foreach (var link in files[i].ReadStreaming().Graph)
            {
                if (moved[i].Contains(link.Node) && link.OppositeUid != files[1 - i].ResourceId)
                    throw new NotSupportedException("A moved cluster connects to an unloaded third section.");
                if (link.OppositeUid == files[1 - i].ResourceId && (moved[i].Contains(link.Node) || moved[1 - i].Contains(link.OppositeNode)))
                    BinaryPrimitives.WriteUInt16LittleEndian(result[i].AsSpan(link.CostOffset),
                        NativeStreamingCost(graphs[i].Positions[link.Node], graphs[1 - i].Positions[link.OppositeNode]));
            }
        if (moveRepresentatives) _ = ValidateStreamingCrossCosts(result);
        return result;
    }

    private static Graph GenerateStreamingGraph(byte[] bytes, Mafia3NavigationCookPlan plan, int[] representatives,
        nint centers, nint context, nint settings)
    {
        // The shipped cluster builder enumerates one section's raw local edges. Its adjacency
        // array is local-only, so feeding patched external face keys into it would index beyond
        // that array. Reproduce only that enumeration; each distance still executes its exact
        // reviewed native cost routine against the connected multi-instance collection.
        int Read(int p) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p));
        int Cluster(int f) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(plan.Faces + f * 16 + 12));
        var costs = new Dictionary<(int, int), ushort>();
        nint work = Allocate(64);
        for (int f = 0; f < plan.FaceCount; f++)
        {
            int first = Read(plan.Faces + f * 16);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(plan.Faces + f * 16 + 8));
            for (int e = first; e < first + count; e++)
            {
                int at = plan.Edges + e * 20, opposite = Read(at + 8), face = Read(at + 12);
                if (opposite == -1 || face == -1 || (bytes[at + 16] & 0x20) != 0) continue;
                if ((uint)opposite >= plan.EdgeCount || (uint)face >= plan.FaceCount)
                    throw new NotSupportedException("Serialized local adjacency contains an external packed key.");
                int a = Cluster(f), b = Cluster(face);
                if (a == b || costs.ContainsKey((a, b))) continue;
                float distance = ((delegate* unmanaged<nint, int, int, nint, nint, nint, float>)0x143C34F40L)
                    (context, representatives[a], representatives[b], settings + 16, settings + 96, work);
                if (!float.IsFinite(distance) || distance < 0 || distance >= 1e30f)
                    throw new NotSupportedException("Joint native cluster path query failed.");
                ushort half = (ushort)(BitConverter.SingleToUInt32Bits(distance * 1.00390625f) >> 16);
                costs.Add((a, b), half); costs.Add((b, a), half);
            }
        }
        if (plan.RequiredFaceConnections is { } connections)
            for (int i = 0; i < connections.Length; i += 2)
                foreach (var (from, to) in new[] { (connections[i], connections[i + 1]), (connections[i + 1], connections[i]) })
                {
                    if ((uint)from >= plan.FaceCount || (uint)to >= plan.FaceCount) throw new InvalidDataException("Invalid streaming patch proof face.");
                    float distance = ((delegate* unmanaged<nint, int, int, nint, nint, nint, float>)0x143C34F40L)(context, from, to, settings + 16, settings + 96, work);
                    if (!float.IsFinite(distance) || distance < 0 || distance >= 1e30f) throw new NotSupportedException("Native pathfinder cannot traverse the new streaming patch.");
                }
        return new(representatives.Select(f => V(centers + f * 16)).ToArray(), costs);
    }

    public (int Sections, int Edges) ValidateStreamingGraphs(byte[][] resources)
    {
        if (resources.Length is < 2 or > 32 || resources.Sum(b => (long)b.Length) > 32 * 1024 * 1024)
            throw new NotSupportedException("Joint graph validation requires 2..32 bounded copied sections.");
        var files = resources.Select(b => Mafia3NavMeshFile.Read(b)).ToArray();
        if (files.Select(f => f.ResourceId).Distinct().Count() != files.Length)
            throw new NotSupportedException("Joint graph validation refuses ambiguous section UIDs.");
        int edges = 0;
        for (int i = 0; i < files.Length; i++)
        {
            var plan = files[i].PrepareInteriorDeformation(new Dictionary<int, Vector3>());
            var graph = Generate(resources[i], plan, null, out _, resources.Where((_, j) => i != j).ToArray());
            if (graph.Costs.Count != plan.GraphEdgeCount) throw new NotSupportedException("Joint native graph edge count differs from source.");
            for (int c = 0; c < plan.ClusterCount; c++)
            {
                var p = graph.Positions[c];
                if (p.X != BitConverter.ToSingle(resources[i], plan.GraphPositions + c * 16) ||
                    p.Y != BitConverter.ToSingle(resources[i], plan.GraphPositions + c * 16 + 4) ||
                    p.Z != BitConverter.ToSingle(resources[i], plan.GraphPositions + c * 16 + 8))
                    throw new NotSupportedException("Joint native graph representative differs from source.");
                int node = plan.GraphNodes + c * 8;
                int first = BinaryPrimitives.ReadInt32LittleEndian(resources[i].AsSpan(node));
                int count = BinaryPrimitives.ReadInt32LittleEndian(resources[i].AsSpan(node + 4));
                for (int e = first; e < first + count; e++)
                {
                    int at = plan.GraphEdges + e * 8;
                    int target = BinaryPrimitives.ReadInt32LittleEndian(resources[i].AsSpan(at + 4));
                    if (BinaryPrimitives.ReadUInt16LittleEndian(resources[i].AsSpan(at + 2)) != 0 ||
                        !graph.Costs.TryGetValue((c, target), out var cost) || cost != BinaryPrimitives.ReadUInt16LittleEndian(resources[i].AsSpan(at)))
                        throw new NotSupportedException($"Joint native baseline differs for {files[i].ResourceId:X8} cluster {c}->{target}.");
                    edges++;
                }
            }
        }
        return (files.Length, edges);
    }

    private static nint StreamingCollection(byte[] source, nint sourceMesh, nint sourceInstance, byte[][] peers)
    {
        var files = new List<Mafia3NavMeshFile> { Mafia3NavMeshFile.Read(source) };
        var meshes = new List<nint> { sourceMesh }; var instances = new List<nint> { sourceInstance };
        foreach (byte[] bytes in peers)
        {
            var file = Mafia3NavMeshFile.Read(bytes);
            var plan = file.PrepareInteriorDeformation(new Dictionary<int, Vector3>());
            nint mesh = Map(bytes) + plan.MeshObject, instance = Allocate(512);
            foreach (var (dst, src) in new[] { (16, 16), (32, 32), (48, 48), (64, 80), (80, 96) }) Q(instance, dst, P(mesh, src));
            foreach (var (dst, src) in new[] { (24, 24), (40, 40), (56, 56), (72, 112), (88, 116) }) W(instance, dst, I(mesh, src));
            Q(instance, 96, mesh);
            for (int i = 0; i < 3; i++) *(float*)(instance + 112 + i * 16 + i * 4) = 1;
            for (int off = 208; off < 448; off += 16) W(instance, off + 12, unchecked((int)0x80000000));
            foreach (var (off, count) in new[] { (208, plan.EdgeCount), (240, plan.FaceCount) })
            {
                nint data = Allocate(count * 4); new Span<byte>((void*)data, count * 4).Fill(255);
                Q(instance, off, data); W(instance, off + 8, count); W(instance, off + 12, unchecked((int)0x80000000) | count);
            }
            W(instance, 456, 1); files.Add(file); meshes.Add(mesh); instances.Add(instance);
        }
        if (files.Select(f => f.ResourceId).Distinct().Count() != files.Count) throw new InvalidDataException("Duplicate streaming section UID.");
        nint collection = Allocate(128), info = Allocate(48 * files.Count);
        Q(collection, 32, info); W(collection, 40, files.Count);
        var validated = new HashSet<(int, int)>();
        for (int i = 0; i < files.Count; i++)
        {
            Q(info, i * 48, instances[i]);
            W(instances[i], 448, unchecked((int)files[i].ResourceId)); W(instances[i], 452, i);
            foreach (var link in files[i].ReadStreaming().Mesh)
            {
                int j = files.FindIndex(f => f.ResourceId == link.OppositeUid);
                if (j < 0) continue;
                if (validated.Add((Math.Min(i, j), Math.Max(i, j)))) files[i].ValidateStreamingPair(files[j]);
                nint edge = P(meshes[i], 32) + link.Edge * 20;
                if (U(edge, 8) != uint.MaxValue || U(edge, 12) != uint.MaxValue || (*((byte*)edge + 16) & 0x60) != 0)
                    throw new NotSupportedException("Streaming source edge is already connected or blocked.");
                W(edge, 8, (j << 22) | link.OppositeEdge); W(edge, 12, (j << 22) | link.OppositeFace);
                *((byte*)edge + 16) |= 64;
            }
        }
        return collection;
    }
}
