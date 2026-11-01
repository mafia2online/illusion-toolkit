using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledNativeLod(Action<string, bool> check)
    {
        NativeLodGraph Pair(bool ready = true) => new([
            new(1, -1, [1], Vector3.Zero, new(10), 400, ready),
            new(2, 0, [], Vector3.Zero, new(10), 0, false)]);
        var pair = Pair(); var visible = new bool[2];
        pair.Evaluate(new(30, 5, 5), visible);
        check("native LOD inclusive nearest-AABB threshold draws only complete children", !visible[0] && visible[1]);
        pair.Evaluate(new(MathF.BitIncrement(30), 5, 5), visible);
        check("native LOD next float beyond threshold draws only coarse parent", visible[0] && !visible[1]);
        Pair(false).Evaluate(new(5), visible);
        check("native LOD incomplete children retain coarse representation inside bounds", visible[0] && !visible[1]);
        pair.Evaluate(new(5), visible, hasScene: false);
        check("native LOD missing render scene retains coarse parent", visible[0] && !visible[1]);
        pair.Evaluate(new(50, 5, 5), visible, switchScale: -2);
        check("native LOD switch scale is squared including negative scale", !visible[0] && visible[1]);
        var absorbed = new NativeLodGraph([new(1, -1, [1], Vector3.Zero, Vector3.One, 0, true), new(2, 0, [], new(100), new(101), 0, false)]);
        absorbed.Evaluate(new(100), visible);
        check("native LOD parent bounds recursively absorb child world bounds", !visible[0] && visible[1] && absorbed.Groups[0].WorldMax == new Vector3(101));
        bool cyclic = false;
        try { _ = new NativeLodGraph([new(1, 1, [1], Vector3.Zero, Vector3.One, 0, true), new(2, 0, [0], Vector3.Zero, Vector3.One, 0, true)]); }
        catch (ArgumentException) { cyclic = true; }
        check("native LOD cyclic metadata is refused rather than recursed", cyclic);
        bool mismatch = false;
        try { _ = new NativeLodGraph([new(1, -1, [1], Vector3.Zero, Vector3.One, 0, true), new(2, -1, [], Vector3.Zero, Vector3.One, 0, false)]); }
        catch (ArgumentException) { mismatch = true; }
        check("native LOD inconsistent ownership metadata is refused", mismatch);
        VerifyNativeLodOriginalDigest(check);
        foreach (var entry in new[] { ("bc_bayou_block_a_lod0.sds", 0x6072DDA1AE804E7Dul), ("bc_french_east_block_b_lod0.sds", 0x5B403B2A66404E6Bul) })
        {
            var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, entry.Item1)); if (!archive.Exists) continue;
            var document = new CompiledSceneDocument(archive); var graph = document.NativeLods;
            check(entry.Item1 + " typed native LOD graph loads all physical-archive metadata", document.NativeLodMetadataReady && document.NativeLodRefusal is null && graph?.Count == 4);
            if (graph is null) throw new InvalidDataException(document.NativeLodRefusal);
            int coarse = Enumerable.Range(0, graph.Count).Single(i => graph.Groups[i].Id == entry.Item2);
            var parent = graph.Groups[coarse]; int child = parent.Children.Single();
            check(entry.Item1 + " fine child completer enables its parent switch", parent.ChildrenReady && graph.Groups[child].Parent == coarse);
            var near = (parent.WorldMin + parent.WorldMax) * .5f; var far = parent.WorldMax + new Vector3(1000, 0, 0);
            var mask = new bool[graph.Count]; graph.Evaluate(near, mask);
            bool nearOnly = !mask[coarse] && mask[child]; graph.Evaluate(far, mask);
            check(entry.Item1 + " near/far native hierarchy has no coarse-fine overlap", nearOnly && mask[coarse] && !mask[child]);
            var fine = document.Meshes.Where(mesh => mesh.NativeLod?.Group == child && ReferenceEquals(mesh.NativeLod.Graph, graph)).ToArray();
            check(entry.Item1 + " actual fine visual meshes carry canonical Domain membership", fine.Length > 0 && fine.All(mesh => graph.IsVisible(child, near) && !graph.IsVisible(child, far)));
            var before = document.Graph.Resources.ToDictionary(resource => resource.Path, resource => File.ReadAllBytes(resource.Path));
            var completer = document.Graph.NodesOfType(0x7EDD00C4).Single(node => document.Graph.Resolve(document.Graph.Field(node, "m_LODGroup"))?.ObjectId == graph.Groups[child].Id);
            var owner = document.Graph.Owner(completer);
            var initialize = typeof(CompiledSceneDocument).GetMethod("InitializeNativeLods", BindingFlags.NonPublic | BindingFlags.Instance)!;
            try
            {
                // Simulate unavailable fine activation in this independent probe document's graph.
                // Keep the parent's own completer active: it must NOT authorize the near switch.
                document.Graph.SetSubtreeActive(owner, completer, false);
                initialize.Invoke(document, [document.Graph.Resources.ToHashSet()]);
                var incomplete = document.NativeLods!; int parentIndex = Enumerable.Range(0, incomplete.Count).Single(i => incomplete.Groups[i].Id == parent.Id);
                check(entry.Item1 + " parent completer alone cannot substitute for missing fine child completer", !incomplete.Groups[parentIndex].ChildrenReady
                    && incomplete.IsVisible(parentIndex, near));
            }
            finally
            {
                document.Graph.SetSubtreeActive(owner, completer, true);
                initialize.Invoke(document, [document.Graph.Resources.ToHashSet()]);
            }
            graph.Evaluate(near, mask); graph.Evaluate(far, mask);
            check(entry.Item1 + " LOD evaluation never mutates compiled resources", document.Graph.Resources.All(resource => !resource.IsDirty && resource.ToArray().SequenceEqual(before[resource.Path])));
        }
        var waterArchive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city_univers", "bc_city_univers.sds"));
        if (waterArchive.Exists)
        {
            var water = new CompiledSceneDocument(waterArchive);
            check("ungrouped native water preserves ordinary visibility", water.NativeLodMetadataReady
                && water.Meshes.Where(mesh => mesh.Name == "bc00_water_main").Any(mesh => mesh.NativeLod is null));
        }
    }

    private static void VerifyNativeLodOriginalDigest(Action<string, bool> check)
    {
        // Independent original machine-code CheckLODGroup102987350 +recursive
        // ProcessLODGroupObjects1029A6E00 oracle. Proprietary bytes remain in ignored scratch.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var random = new Random(92178); float F(float span) => (float)(random.NextDouble() * span - span * .5);
        for (int i = 0; i < 20000; i++)
        {
            var min = new Vector3(F(10000), F(10000), F(2000));
            var max = min + new Vector3(MathF.Abs(F(1000)), MathF.Abs(F(1000)), MathF.Abs(F(1000)));
            var eye = new Vector3(F(10000), F(10000), F(2000)); float scale = MathF.Abs(F(5));
            bool scene = i % 3 != 0, children = i % 5 != 0, ready = (i & 1) != 0;
            float distance = MathF.Abs(F(10000000));
            if (i % 4 == 0)
            {
                scale = 1; float d = NativeLodGraph.DistanceSquaredToBounds(eye, min, max);
                distance = i % 12 == 0 ? MathF.BitDecrement(d) : i % 12 == 4 ? d : MathF.BitIncrement(d);
            }
            var groups = new List<NativeLodGroup> { new(1, -1, children ? [1, 2] : [], min, max, distance, ready) };
            if (children) { groups.Add(new(2, 0, [], min, max, 0, false)); groups.Add(new(3, 0, [], min, max, 0, false)); }
            var graph = new NativeLodGraph(groups); var visible = new bool[graph.Count]; graph.Evaluate(eye, visible, scale, scene);
            hash.AppendData([(byte)(visible[0] ? 1 : 2)]);
        }
        var preorder = new List<int>();
        void Order(int i) { preorder.Add(i); if (i < 15) { Order(i * 2 + 1); Order(i * 2 + 2); } }
        Order(0);
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            var eye = new Vector3(F(1000), F(1000), F(1000)); bool scene = iteration % 3 != 0;
            var groups = new List<NativeLodGroup>();
            for (int i = 0; i < 31; i++)
            {
                bool ready = (random.Next() & 1) != 0; float distance = MathF.Abs(F(1000000));
                groups.Add(new((ulong)i + 1, i == 0 ? -1 : (i - 1) / 2, i < 15 ? [i * 2 + 1, i * 2 + 2] : [], new(-100), new(100), distance, ready));
            }
            var graph = new NativeLodGraph(groups); var visible = new bool[31]; graph.Evaluate(eye, visible, hasScene: scene);
            var selected = preorder.Where(i => visible[i]).ToArray(); hash.AppendData(BitConverter.GetBytes(selected.Length));
            foreach (int i in selected) hash.AppendData(BitConverter.GetBytes((long)((i + 1) * 256 + 48)));
            if (Enumerable.Range(0, 31).Any(i => graph.IsVisible(i, eye, hasScene: scene) != visible[i]))
                throw new InvalidDataException("Native LOD direct and batched visibility disagree.");
        }
        check("native LOD20000masks plus20000recursive hierarchies match original binary digest", Convert.ToHexString(hash.GetHashAndReset())
            == "9034DC702ECE80C01E75D1C926E9BD88598426071B653328C05F9872D35B0A43");
    }
}
