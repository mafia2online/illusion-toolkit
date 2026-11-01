using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Navigation;

public sealed partial class Mafia3NavMeshFile
{
    public static Mafia3NavigationNetworkEdit ApplyStreamingNetworkCook(IReadOnlyList<Mafia3NavMeshFile> sections,
        uint sourceUid, IReadOnlyDictionary<int, Vector3> edits, IReadOnlyList<byte[]> cooked, bool moveRepresentatives = false)
    {
        var plans = PrepareStreamingNetwork(sections, sourceUid, edits, moveRepresentatives).Sections;
        if (cooked.Count != sections.Count) throw new InvalidDataException("The native worker returned an incomplete navigation network.");
        var byUid = sections.Select((section, index) => (section.ResourceId, index)).ToDictionary(pair => pair.ResourceId, pair => pair.index);
        var moved = new HashSet<int>[sections.Count];
        for (int index = 0; index < sections.Count; index++)
        {
            var plan = plans[index]; var bytes = cooked[index];
            if (bytes.Length != plan.Baseline.Length) throw new InvalidDataException("A fixed-topology network cook resized a section.");
            moved[index] = Enumerable.Range(0, plan.ClusterCount).Where(node => !bytes.AsSpan(plan.GraphPositions + node * 16, 12)
                .SequenceEqual(plan.Baseline.AsSpan(plan.GraphPositions + node * 16, 12))).ToHashSet();
            if (!moveRepresentatives && moved[index].Count != 0) throw new NotSupportedException("Strict network mode protects cluster representatives.");
        }
        var translations = new List<Mafia3NavigationTranslation>();
        for (int index = 0; index < sections.Count; index++)
        {
            var file = sections[index]; var plan = plans[index]; var bytes = cooked[index];
            var allowed = new bool[bytes.Length];
            for (int edge = 0; edge < plan.GraphEdgeCount; edge++) Array.Fill(allowed, true, plan.GraphEdges + edge * 8, 2);
            foreach (int node in moved[index])
            {
                int at = plan.GraphPositions + node * 16;
                if (!Mafia3AiWorldFile.Finite(new Vector3(BitConverter.ToSingle(bytes, at), BitConverter.ToSingle(bytes, at + 4), BitConverter.ToSingle(bytes, at + 8))))
                    throw new InvalidDataException("A network cluster representative is not finite.");
                Array.Fill(allowed, true, at, 12);
            }
            foreach (var link in file.ReadStreaming().Graph)
            {
                bool loaded = byUid.TryGetValue(link.OppositeUid, out int peer);
                if (!loaded && moved[index].Contains(link.Node)) throw new NotSupportedException("A moved cluster still connects to an unloaded navigation section.");
                if (!loaded || !moved[index].Contains(link.Node) && !moved[peer].Contains(link.OppositeNode)) continue;
                ushort cost = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(link.CostOffset));
                if ((cost & 0x7F80) == 0x7F80) throw new InvalidDataException("A native network cost is not finite.");
                Array.Fill(allowed, true, link.CostOffset, 2);
            }
            Array.Fill(allowed, true, plan.RootCount, 4);
            for (int at = 0; at < bytes.Length; at++)
                if (!allowed[at] && bytes[at] != plan.Modified[at]) throw new InvalidDataException("The native network cook changed unapproved metadata or geometry.");
            if (!bytes.AsSpan().SequenceEqual(plan.Baseline) && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(plan.RootCount)) != 2)
                throw new InvalidDataException("The native network cook retained stale acceleration structures.");
            var patches = new List<NavigationBytePatch>();
            for (int at = 0; at < bytes.Length;)
            {
                if (bytes[at] == plan.Baseline[at]) { at++; continue; }
                int start = at++;
                while (at < bytes.Length && bytes[at] != plan.Baseline[at]) at++;
                patches.Add(new(start, plan.Baseline.AsSpan(start, at - start).ToArray(), bytes.AsSpan(start, at - start).ToArray()));
            }
            translations.Add(new(file, patches));
        }
        var parsed = cooked.Select(bytes => Read(bytes)).ToArray();
        for (int index = 0; index < parsed.Length; index++)
            foreach (uint peer in parsed[index].ReadStreaming().PotentialNeighbors.Where(byUid.ContainsKey))
                _ = parsed[index].ValidateStreamingPair(parsed[byUid[peer]]);
        var result = new Mafia3NavigationNetworkEdit(sections, plans.Select(plan => plan.Baseline).ToArray(), cooked, translations);
        result.Redo(); return result;
    }
}

public sealed class Mafia3NavigationNetworkEdit
{
    private readonly IReadOnlyList<Mafia3NavMeshFile> _files;
    private readonly byte[][] _before, _after;
    private readonly IReadOnlyList<Mafia3NavigationTranslation> _edits;
    internal Mafia3NavigationNetworkEdit(IReadOnlyList<Mafia3NavMeshFile> files, byte[][] before,
        IReadOnlyList<byte[]> after, IReadOnlyList<Mafia3NavigationTranslation> edits)
        => (_files, _before, _after, _edits) = (files.ToArray(), before, after.Select(bytes => bytes.ToArray()).ToArray(), edits.ToArray());
    public void Undo() => Apply(false);
    public void Redo() => Apply(true);
    public void ValidateUndo() => Validate(false);
    public void ValidateRedo() => Validate(true);
    private void Validate(bool forward)
    {
        var expected = forward ? _before : _after;
        if (_files.Where((file, index) => !file.ToBytes().AsSpan().SequenceEqual(expected[index])).Any())
            throw new InvalidDataException("A navigation network section changed after preflight.");
    }
    private void Apply(bool forward)
    {
        Validate(forward);
        int completed = 0;
        try
        {
            if (forward) foreach (var edit in _edits) { edit.Redo(); completed++; }
            else foreach (var edit in _edits.Reverse()) { edit.Undo(); completed++; }
        }
        catch
        {
            if (forward) foreach (var edit in _edits.Take(completed).Reverse()) edit.Undo();
            else foreach (var edit in _edits.Reverse().Take(completed).Reverse()) edit.Redo();
            throw;
        }
    }
}
