using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledNativeLodRendering(Action<string, bool> check)
    {
        var graph = new NativeLodGraph([
            new(1, -1, [1, 2], Vector3.Zero, Vector3.One, 100, true),
            new(2, 0, [], Vector3.Zero, Vector3.One, 0, false),
            new(3, 0, [], Vector3.Zero, Vector3.One, 0, false)]);
        var near = Vector3.Zero; var visible = new bool[3];
        graph.SetRenderCoverage([1, 2, 0]); graph.EvaluateRender(near, visible);
        check("renderer incomplete child coverage retains coarse without partial fine overlap", visible.SequenceEqual(new[] { true, false, false }));
        graph.Evaluate(near, visible);
        check("renderer fallback preserves exact native completer visibility", visible.SequenceEqual(new[] { false, true, true })
            && graph.Groups[0].ChildrenReady && !graph.IsVisible(0, near));
        check("renderer coverage exposes explicit unsupported subtree diagnostic", graph.RenderFallbackGroups.SequenceEqual(new[] { 0 }));
        graph.SetRenderCoverage([1, 2, 1]); graph.EvaluateRender(near, visible);
        check("renderer complete decoded children preserve native near selection", visible.SequenceEqual(new[] { false, true, true }) && graph.RenderFallbackGroups.Count == 0);
        graph.EvaluateRender(new(100), visible);
        check("renderer complete coverage preserves native far coarse selection", visible.SequenceEqual(new[] { true, false, false }));
        graph.SetRenderCoverage([0, 0, 0]); graph.EvaluateRender(near, visible);
        check("renderer all-unrenderable groups never falsely activate fine children", visible.SequenceEqual(new[] { true, false, false }) && graph.RenderCoverage!.All(count => count == 0));
        bool refused = false;
        try { graph.SetRenderCoverage([1, -1, 1]); } catch (ArgumentException) { refused = true; }
        check("renderer malformed coverage is refused atomically", refused && graph.RenderCoverage!.All(count => count == 0));
        refused = false;
        try { graph.SetRenderCoverage([1]); } catch (ArgumentException) { refused = true; }
        check("renderer incomplete coverage array is refused atomically", refused && graph.RenderCoverage!.Count == 3);
        graph.SetRenderCoverage([1, 1, 1]);
        check("renderer scalar and batch selection agree", Enumerable.Range(0, 3).All(i => {
            graph.EvaluateRender(near, visible); return graph.IsRenderVisible(i, near) == visible[i]; }));
        foreach (var (name, parentId, hasFine) in new[] {
            ("bc_bayou_block_b_lod0.sds", 0x673B5CF420204E81ul, false),
            ("bc_bridge_a_lod0.sds", 0x5428F74F12004E47ul, true) })
        {
            var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, name)); if (!archive.Exists) continue;
            var document = new CompiledSceneDocument(archive); document.RefreshNativeLodRenderCoverage();
            var actual = document.NativeLods ?? throw new InvalidDataException(document.NativeLodRefusal);
            int parent = Enumerable.Range(0, actual.Count).Single(i => actual.Groups[i].Id == parentId);
            var metadata = actual.Groups[parent]; var point = (metadata.WorldMin + metadata.WorldMax) * .5f;
            check(name + " native activation stays complete despite renderer capability fallback", metadata.ChildrenReady && !actual.IsVisible(parent, point));
            check(name + " decoded coverage chooses supported fine or explicit coarse fallback", actual.IsRenderVisible(parent, point) != hasFine
                && actual.RenderFallbackGroups.Contains(parent) != hasFine && (document.NativeLodRenderFallback is not null || hasFine));
            check(name + " fallback does not mutate native working resources", document.Graph.Resources.All(resource => !resource.IsDirty));
        }
        VerifyNativeLodOriginalDigest(check);
    }
}
