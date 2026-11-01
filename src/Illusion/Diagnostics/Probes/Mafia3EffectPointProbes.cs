using System.Numerics;
using Illusion.Assets.Compiled;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void VerifyCompiledEffectPoints(CompiledSceneDocument document, Action<string, bool> check)
    {
        var graph = document.Graph;
        var library = new CompiledEffectLibraryData(graph);
        var generations = library.Effects.SelectMany(library.Generations).ToArray();
        int accepted = generations.Count(g => CompiledEffectPointPreview.TryCreate(graph, g, out _, out _));
        check("native stationary point burst preflight accepts13 verified generations", accepted == 13);
        var generation = graph.Objects[0x1065E033E7A05503];
        var resource = graph.Owner(generation); var before = resource.ToArray();
        check("native stationary flash generation is available", CompiledEffectPointPreview.TryCreate(graph, generation, out var preview, out _));
        var first = preview!.Sample(0);
        var p = first.Particles.Single();
        check("native point burst snapshot matches verified operator fields", first.MaterialGuid == 0x08134F7E060F29DF && p.Position == Vector3.Zero
            && p.HalfSize == new Vector2(.05f) && p.Color == new Vector4(205 / 255f, 200 / 255f, 181 / 255f, 1)
            && p.AtlasUv == new Vector4(.252f, 0, .5f, .25f) && p.Emissivity == Vector3.One && p.Lifetime == .1f);
        check("native point lifetime and deterministic rewind", preview.Sample(.05f).Particles.Single().NormalizedAge == .5f
            && preview.Sample(.1f).Particles.Count == 0 && preview.Sample(0).Particles.SequenceEqual(first.Particles));
        var birth = library.Operators(generation).Single(o => o.TypeId == 0x3EBB96BE);
        var life = graph.Resolve(graph.Field(graph.Field(birth, "m_Life"), "m_Interpolator"))!;
        var value = graph.Field(life, "m_ConstValue")!;
        var owner = graph.Owner(value); var original = graph.Data(value).ToArray();
        try
        {
            owner.Replace(value, BitConverter.GetBytes(.2f));
            check("point preview follows unsaved authored life edits", preview.Sample(.1f).Particles.Single().Lifetime == .2f);
            owner.Replace(value, BitConverter.GetBytes(0f));
            var infinite = preview.Sample(.05f).Particles.Single();
            check("point preview uses native seconds-age for tiny reciprocal lifetime", infinite.Lifetime == 100000000 && infinite.NormalizedAge == .05f);
            bool invalid = false;
            try { preview.Sample(float.NaN); } catch (ArgumentOutOfRangeException) { invalid = true; }
            check("point preview rejects non-finite time", invalid);
        }
        finally { owner.Replace(value, original); }
        check("point preview restores authored lifetime after undo", preview.Sample(.1f).Particles.Count == 0);
        var unsupported = generations.First(g => !CompiledEffectPointPreview.TryCreate(graph, g, out _, out _));
        check("unsupported particle runtime stacks have explicit preflight refusal", !CompiledEffectPointPreview.TryCreate(graph, unsupported, out _, out var reason)
            && !string.IsNullOrWhiteSpace(reason));
        check("point preview and edits leave every resource byte exact", resource.ToArray().AsSpan().SequenceEqual(before));
    }
}
