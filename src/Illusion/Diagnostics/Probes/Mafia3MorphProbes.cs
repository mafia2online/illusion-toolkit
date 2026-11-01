using System.IO;
using Illusion.Assets;
using Illusion.Assets.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledMorphPreview(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder,
            "combinables", "auto", "fe_head_004_cassandra_fe_head_004_cassandra.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        var binding = EntityCreationRows(document.Roots).Select(n => n.Source)
            .OfType<CompiledSceneDocument.CompiledFrameNode>().Select(frame => frame.GeometryBinding).First(b => b is not null)!;
        var baseline = document.Graph.Resources.ToDictionary(resource => resource, resource => resource.ToArray());
        var original = binding.DecodeLod(0)!;
        var channels = document.MorphChannels(binding, 0);
        check("native sparse morph atlas resolves resource-local dependency and complete vertex spans",
            channels.TextureName == "fe_head_004_cassandra_fe_head_004_cassandra_morph_lod0.dds"
            && channels.TargetIds.Length == 201 && channels.Vertices.Length == 9500
            && channels.Vertices.Count(vertex => vertex.Length > 0) == 7991 && channels.Vertices.Sum(vertex => vertex.Length) == 133142);
        var preview = document.PreviewMorph(binding, 0, new Dictionary<int, float> { [84] = 1 });
        check("native expression position deltas reach skinned geometry without changing topology or rig",
            preview.Positions.Where((position, index) => position != original.Positions[index]).Count() == 122
            && preview.IsSkinned && preview.Skeleton!.InverseBind.SequenceEqual(original.Skeleton!.InverseBind)
            && preview.Indices.SequenceEqual(original.Indices) && preview.BoneIndices!.SequenceEqual(original.BoneIndices!));
        var complete = document.PreviewMorph(binding, 0, channels.Vertices.SelectMany(vertex => vertex)
            .Select(influence => influence.Target).Distinct().ToDictionary(index => index, _ => 1f));
        check("all sparse morph target weights produce finite position and tangent frames",
            complete.Positions.Concat(complete.Normals).Concat(complete.Tangents!).Concat(complete.Binormals!)
                .All(vector => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z)));
        var cleared = document.PreviewMorph(binding, 0, new Dictionary<int, float>());
        check("zero expression preview restores exact native geometry channels",
            cleared.Positions.SequenceEqual(original.Positions) && cleared.Normals.SequenceEqual(original.Normals)
            && cleared.Tangents!.SequenceEqual(original.Tangents!) && cleared.Binormals!.SequenceEqual(original.Binormals!)
            && cleared.DecalUVs?.Length == original.DecalUVs?.Length);
        bool badTarget = false, badWeight = false;
        try { document.PreviewMorph(binding, 0, new Dictionary<int, float> { [201] = 1 }); }
        catch (ArgumentOutOfRangeException) { badTarget = true; }
        try { document.PreviewMorph(binding, 0, new Dictionary<int, float> { [84] = float.NaN }); }
        catch (ArgumentOutOfRangeException) { badWeight = true; }
        check("native morph preview refuses invalid target indices and nonfinite weights", badTarget && badWeight);
        check("native morph preview leaves every compiled resource byte unchanged",
            baseline.All(pair => pair.Key.ToArray().AsSpan().SequenceEqual(pair.Value)));
    }
}
