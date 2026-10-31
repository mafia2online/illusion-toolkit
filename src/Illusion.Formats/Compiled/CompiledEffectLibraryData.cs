using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Native M3 GENR effect definitions, operators and shared parameter sources. No particle simulation.</summary>
public class CompiledEffectLibraryData
{
    public const uint LibraryType = 0x6B08346E;
    public CompiledGraph Graph { get; }
    public IReadOnlyList<CompiledElement> Effects { get; }
    private readonly Dictionary<ulong, int> _references = [];
    public CompiledEffectLibraryData(CompiledGraph graph)
    {
        Graph = graph;
        var libraries = graph.Nodes.Where(n => n.TypeId == LibraryType).ToArray();
        if (libraries.Length == 0) throw new InvalidDataException("No Mafia III compiled effect library.");
        Effects = libraries.SelectMany(n => graph.Items(graph.Field(n, "m_Effects")))
            .Select(graph.Resolve).OfType<CompiledElement>().Distinct().ToArray();
        foreach (var n in graph.Nodes)
            if (graph.Field(n, "m_Interpolator") is { Event: 4, DataSize: 12 } pointer)
            {
                ulong id = graph.Integer(pointer);
                _references[id] = _references.GetValueOrDefault(id) + 1;
            }
    }

    public IReadOnlyList<CompiledElement> Generations(CompiledElement effect) =>
        Graph.Items(Graph.Field(effect, "m_Generations")).Select(Graph.Resolve).OfType<CompiledElement>().ToArray();
    public IReadOnlyList<CompiledElement> Operators(CompiledElement generation) =>
        Graph.Items(Graph.Field(generation, "m_Operators")).Select(Graph.Resolve).OfType<CompiledElement>().ToArray();
    public static string OperatorName(CompiledGraph graph, CompiledElement node)
    {
        var fields = graph.Fields(node);
        return fields.ContainsKey("m_Rate") ? "Emission" : fields.ContainsKey("m_OrientationType") ? "Orientation"
            : fields.ContainsKey("m_RegionU1") ? "Texture atlas" : fields.ContainsKey("m_Opacity") ? "Color / opacity"
            : fields.ContainsKey("m_Speed") ? "Speed" : fields.ContainsKey("m_MinVelocity") ? "Velocity"
            : fields.ContainsKey("m_Size") ? "Size" : fields.ContainsKey("m_Acceleration") ? "Acceleration"
            : fields.ContainsKey("m_Noise") ? "Wind" : $"Operator {node.TypeId:X8}";
    }

    /// <summary>Evaluates only verified native constant interpolators; other curves remain explicit.</summary>
    public ConstantParameter? Constant(CompiledElement owner, string field)
    {
        var parameter = Graph.Field(owner, field);
        var source = Graph.Resolve(Graph.Field(parameter, "m_Interpolator"));
        if (source is null || source.TypeId is not (0xA431F78B or 0xCFA090E0 or 0x5E8A135A or 0x351B7431)
            || Graph.Field(source, "m_ConstValue") is not { Event: 4 } value) return null;
        var data = Graph.Data(value);
        float F(int at) => BinaryPrimitives.ReadSingleLittleEndian(Graph.Data(value)[at..]);
        if (data.Length is not (4 or 12)) throw new InvalidDataException("Invalid effect constant dimension.");
        var result = data.Length == 4 ? new Vector3(F(0), 0, 0) : new Vector3(F(0), F(4), F(8));
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z))
            throw new InvalidDataException("Non-finite effect constant.");
        return new(source.ObjectId, data.Length / 4, result, _references.GetValueOrDefault(source.ObjectId));
    }
    public int ReferenceCount(ulong sourceId) => _references.GetValueOrDefault(sourceId);
    public sealed record ConstantParameter(ulong SourceId, int Dimensions, Vector3 Value, int SharedUsers);

}
