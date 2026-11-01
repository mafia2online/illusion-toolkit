using System.Numerics;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Read-only native emission/age parameter sampling over the document's current compiled data.</summary>
public sealed class CompiledEffectPreview
{
    public static bool Supports(CompiledSceneDocument document) => document.Graph.NodesOfType(CompiledEffectLibraryData.LibraryType).Count != 0;
    private readonly CompiledSceneDocument _document;
    private readonly CompiledEffectLibraryData _library;
    public IReadOnlyList<ulong> EffectIds { get; }
    public CompiledEffectPreview(CompiledSceneDocument document)
    {
        _document = document;
        _library = new(document.Graph);
        EffectIds = _library.Effects.Select(e => e.ObjectId).ToArray();
    }
    public IReadOnlyList<ulong> GenerationIds(int effect) => _library.Generations(_library.Effects[effect]).Select(n => n.ObjectId).ToArray();
    public IReadOnlyList<EffectParameter> Parameters(ulong generation)
    {
        if (!_document.Graph.Objects.TryGetValue(generation, out var node)) throw new ArgumentException("Unknown particle generation.");
        return _library.Operators(node).SelectMany(op => _document.Graph.Fields(op).Keys
            .Where(field => _document.Graph.Field(_document.Graph.Field(op, field), "m_Interpolator") is not null)
            .Select(field => new EffectParameter($"{CompiledEffectLibraryData.OperatorName(_document.Graph, op)}.{field} [{op.ObjectId:X16}]", op.ObjectId, field))).ToArray();
    }
    public EffectParameterSample Sample(EffectParameter parameter, float[] times, float[] ages, bool looping = true)
    {
        if (times.Length != ages.Length || times.Length > 4096) throw new ArgumentException("Give matching time/age arrays of at most 4096 samples.");
        var graph = _document.Graph;
        var curve = CompiledEffectCurve.FromParameter(graph, graph.Objects[parameter.Owner], parameter.Field);
        var values = times.Select((t, i) => curve.Sample(t, ages[i], looping)).ToArray();
        return new(curve.SourceId, curve.Dimensions, curve.IsConstant, values);
    }
}

public sealed record EffectParameter(string Label, ulong Owner, string Field);
public sealed record EffectParameterSample(ulong SourceId, int Dimensions, bool Constant, IReadOnlyList<Vector3> Values);
