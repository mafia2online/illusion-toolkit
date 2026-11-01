using System.Buffers.Binary;
using System.Numerics;
using Illusion.Formats.Navigation;

namespace Illusion.NativeCookHost;

internal sealed unsafe partial class NavigationOracle
{
    public byte[][] CookStreamingNetwork(byte[][] resources, IReadOnlyDictionary<int, Vector3> edits, bool moveRepresentatives)
    {
        _ = ValidateStreamingGraphs(resources);
        if (moveRepresentatives) _ = ValidateStreamingCrossCosts(resources);
        var files = resources.Select(bytes => Mafia3NavMeshFile.Read(bytes)).ToArray();
        var plans = Mafia3NavMeshFile.PrepareStreamingNetwork(files, files[0].ResourceId, edits, moveRepresentatives).Sections;
        var byUid = files.Select((file, index) => (file.ResourceId, index)).ToDictionary(pair => pair.ResourceId, pair => pair.index);
        var result = new byte[files.Length][]; var graphs = new Graph[files.Length];
        var moved = new HashSet<int>[files.Length];
        for (int index = 0; index < files.Length; index++)
        {
            var plan = plans[index];
            var before = Generate(plan.Baseline, plan, null, out var representatives,
                plans.Where((_, peer) => peer != index).Select(peer => peer.Baseline).ToArray());
            var after = Generate(plan.Modified, plan, representatives, out _,
                plans.Where((_, peer) => peer != index).Select(peer => peer.Modified).ToArray());
            graphs[index] = after;
            moved[index] = Enumerable.Range(0, plan.ClusterCount).Where(node => before.Positions[node] != after.Positions[node]).ToHashSet();
            if (!moveRepresentatives && moved[index].Count != 0) throw new NotSupportedException("Strict network mode protects every cluster representative.");
            if (!before.Costs.Keys.ToHashSet().SetEquals(after.Costs.Keys)) throw new NotSupportedException("Network deformation changed cluster adjacency.");
            result[index] = plan.Modified.ToArray();
            foreach (int node in moved[index])
            {
                int at = plan.GraphPositions + node * 16; var position = after.Positions[node];
                BinaryPrimitives.WriteSingleLittleEndian(result[index].AsSpan(at), position.X);
                BinaryPrimitives.WriteSingleLittleEndian(result[index].AsSpan(at + 4), position.Y);
                BinaryPrimitives.WriteSingleLittleEndian(result[index].AsSpan(at + 8), position.Z);
            }
            for (int node = 0; node < plan.ClusterCount; node++)
            {
                int first = BinaryPrimitives.ReadInt32LittleEndian(result[index].AsSpan(plan.GraphNodes + node * 8));
                int count = BinaryPrimitives.ReadInt32LittleEndian(result[index].AsSpan(plan.GraphNodes + node * 8 + 4));
                for (int edge = first; edge < first + count; edge++)
                {
                    int at = plan.GraphEdges + edge * 8;
                    int target = BinaryPrimitives.ReadInt32LittleEndian(result[index].AsSpan(at + 4));
                    BinaryPrimitives.WriteUInt16LittleEndian(result[index].AsSpan(at), after.Costs[(node, target)]);
                }
            }
        }
        for (int index = 0; index < files.Length; index++)
            foreach (var link in files[index].ReadStreaming().Graph)
            {
                bool loaded = byUid.TryGetValue(link.OppositeUid, out int peer);
                if (!loaded && moved[index].Contains(link.Node)) throw new NotSupportedException("A moved cluster connects to an unloaded section outside this network.");
                if (loaded && (moved[index].Contains(link.Node) || moved[peer].Contains(link.OppositeNode)))
                    BinaryPrimitives.WriteUInt16LittleEndian(result[index].AsSpan(link.CostOffset),
                        NativeStreamingCost(graphs[index].Positions[link.Node], graphs[peer].Positions[link.OppositeNode]));
            }
        for (int index = 0; index < files.Length; index++)
            if (!result[index].AsSpan().SequenceEqual(resources[index])) BinaryPrimitives.WriteInt32LittleEndian(result[index].AsSpan(plans[index].RootCount), 2);
        _ = Mafia3NavMeshFile.ApplyStreamingNetworkCook(files, files[0].ResourceId, edits, result, moveRepresentatives);
        _ = ValidateStreamingGraphs(result);
        if (moveRepresentatives) _ = ValidateStreamingCrossCosts(result);
        return result;
    }
}
