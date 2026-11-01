using System.Numerics;

namespace Illusion.Formats.Navigation;

public sealed record Mafia3NavigationNetworkPlan(IReadOnlyList<Mafia3NavigationCookPlan> Sections, IReadOnlyList<int[]> PermittedSeamVertices);

public sealed partial class Mafia3NavMeshFile
{
    /// <summary>Propagates coincident endpoints through explicitly loaded reciprocal seams only.
    /// Open, blocked and unloaded boundaries remain protected, including multi-way junctions.</summary>
    public static Mafia3NavigationNetworkPlan PrepareStreamingNetwork(IReadOnlyList<Mafia3NavMeshFile> sections,
        uint sourceUid, IReadOnlyDictionary<int, Vector3> edits, bool moveRepresentatives = false)
    {
        if (sections.Count is < 2 or > 32 || sections.Select(section => section.ResourceId).Distinct().Count() != sections.Count)
            throw new NotSupportedException("A streaming network requires 2..32 distinct loaded section UIDs.");
        var byUid = sections.Select((section, index) => (section.ResourceId, index)).ToDictionary(pair => pair.ResourceId, pair => pair.index);
        if (!byUid.TryGetValue(sourceUid, out int source)) throw new InvalidDataException("The source navigation section is not loaded.");
        var layouts = sections.Select(section => new TranslationLayout(section._bytes)).ToArray();
        var edgeArrays = layouts.Select(layout => layout.Array(layout.Variants[0], 32, 20)).ToArray();
        var adjacency = new Dictionary<(int Section, int Vertex), HashSet<(int Section, int Vertex)>>();
        var permitted = new HashSet<int>[sections.Count]; var validated = new HashSet<(int, int)>();
        for (int index = 0; index < sections.Count; index++)
        {
            var file = sections[index]; var layout = layouts[index]; var edges = edgeArrays[index];
            var links = file.ReadStreaming().Mesh.Where(link => byUid.ContainsKey(link.OppositeUid)).ToDictionary(link => link.Edge);
            foreach (var link in links.Values)
            {
                int peer = byUid[link.OppositeUid];
                if (validated.Add((Math.Min(index, peer), Math.Max(index, peer)))) _ = file.ValidateStreamingPair(sections[peer]);
                int own = edges.Address + link.Edge * 20, other = edgeArrays[peer].Address + link.OppositeEdge * 20;
                Add((index, layout.Int(own)), (peer, layouts[peer].Int(other + 4)));
                Add((index, layout.Int(own + 4)), (peer, layouts[peer].Int(other)));
            }
            var allowed = new HashSet<int>(); var forbidden = new HashSet<int>();
            int faceCount = layout.Array(layout.Variants[0], 16, 16).Count;
            for (int edge = 0; edge < edges.Count; edge++)
            {
                int at = edges.Address + edge * 20;
                bool boundary = (uint)layout.Int(at + 8) >= edges.Count || (uint)layout.Int(at + 12) >= faceCount || (file._bytes[at + 16] & 0x60) != 0;
                if (!boundary) continue;
                var target = links.ContainsKey(edge) && (file._bytes[at + 16] & 0x60) == 0 ? allowed : forbidden;
                target.Add(layout.Int(at)); target.Add(layout.Int(at + 4));
            }
            allowed.ExceptWith(forbidden); permitted[index] = allowed;
        }
        var expanded = Enumerable.Range(0, sections.Count).Select(_ => new Dictionary<int, Vector3>()).ToArray();
        var pending = new Queue<(int Section, int Vertex, Vector3 Position)>();
        foreach (var pair in edits)
        {
            if (!moveRepresentatives && !adjacency.ContainsKey((source, pair.Key)))
                throw new NotSupportedException("Strict network edits must select explicit shared seam vertices.");
            pending.Enqueue((source, pair.Key, pair.Value));
        }
        while (pending.TryDequeue(out var next))
        {
            if (expanded[next.Section].TryGetValue(next.Vertex, out var previous))
            {
                if (previous != next.Position) throw new NotSupportedException("Conflicting multi-section seam vertex positions.");
                continue;
            }
            expanded[next.Section].Add(next.Vertex, next.Position);
            if (adjacency.TryGetValue((next.Section, next.Vertex), out var targets))
                foreach (var target in targets) pending.Enqueue((target.Section, target.Vertex, next.Position));
        }
        return new(sections.Select((section, index) => section.PrepareDeformation(expanded[index], permitted[index])).ToArray(),
            permitted.Select(vertices => vertices.Order().ToArray()).ToArray());

        void Add((int Section, int Vertex) first, (int Section, int Vertex) second)
        {
            if (!adjacency.TryGetValue(first, out var targets)) adjacency.Add(first, targets = []);
            targets.Add(second);
        }
    }
}
