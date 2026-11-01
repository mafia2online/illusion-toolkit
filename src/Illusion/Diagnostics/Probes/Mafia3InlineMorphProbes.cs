using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledInlineMorph(Action<string, bool> check)
    {
        var fixture = ReadInlineMorphProbe(); if (fixture is null) return;
        var (document, binding, otherBinding) = fixture.Value; var graph = document.Graph;
        var original = binding.DecodeLod(0)!;
        var otherBefore = otherBinding.DecodeLod(0)!.Positions.ToArray();
        var hashes = graph.Resources.ToDictionary(resource => resource, resource => (SHA: SHA256.HashData(resource.ToArray()), resource.Revision));
        var channels = document.MorphChannels(binding, 0);
        check("inline retail barrel morph atlas decodes all four sparse native target channels", channels.TargetIds.SequenceEqual(new ulong[]
            { 0x4D0963FAAA7B39A8, 0x4D0963FAAA7B39AD, 0x4D0963FAAA7B39AE, 0x4D0963FAAA7B39AF })
            && channels.Vertices.Length == 666 && channels.Vertices.Sum(vertex => vertex.Length) == 1437);
        var full = document.PreviewMorph(binding, 0, new Dictionary<int, float> { [0] = 1 });
        var half = document.PreviewMorph(binding, 0, new Dictionary<int, float> { [0] = .5f });
        float error = original.Positions.Select((position, index) => Vector3.Distance(half.Positions[index], position + (full.Positions[index] - position) * .5f)).Max();
        check("inline morph position deltas preserve native additive weight scaling", error < 2e-6f && !full.Positions.SequenceEqual(original.Positions));
        var cleared = document.PreviewMorph(binding, 0, new Dictionary<int, float>());
        check("inline barrel morph preview restores zero weights and leaves the sibling visual untouched", cleared.Positions.SequenceEqual(original.Positions)
            && otherBinding.DecodeLod(0)!.Positions.SequenceEqual(otherBefore) && full.Indices.SequenceEqual(original.Indices));
        var set = graph.Resolve(graph.Field(binding.Visual, "m_MeshSet"))!;
        var texture = graph.Resolve(graph.Field(set, "m_MorphTextureData"))!; var format = graph.Field(texture, "m_Format")!;
        var resource = graph.Owner(format); byte[] before = graph.Data(format).ToArray(); var checkpoint = resource.CaptureRevertedMutationCheckpoint();
        bool refused = false;
        try { byte[] unknown = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(unknown, 1); resource.Replace(format, unknown); _ = document.MorphChannels(binding, 0); }
        catch (NotSupportedException) { refused = true; }
        finally { resource.Replace(format, before); checkpoint(); }
        check("inline morph schema preflight refuses unknown serialized formats despite a cached atlas", refused);
        check("inline morph preview preserves every opaque compiled byte and resource revision", hashes.All(pair => pair.Key.Revision == pair.Value.Revision
            && SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value.SHA)));
    }
    private static (CompiledSceneDocument Document, CompiledMeshBinding Main, CompiledMeshBinding Sibling)? ReadInlineMorphProbe()
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city_crash", "bc_city_crash.sds"));
        if (!archive.Exists) return null;
        var document = new CompiledSceneDocument(archive); var graph = document.Graph;
        // This native ModelCollection prototype is deliberately absent from active scene roots.
        // Build a read-only test adapter around its original element and use the document's real decoder.
        var frame = new CompiledSceneDocument.CompiledFrameNode(document, graph.Objects[0x155D0F95DB818728]);
        var decoder = new CompiledMeshDecoder(graph);
        var decode = typeof(CompiledSceneDocument).GetMethod("DecodeVisual", BindingFlags.Instance | BindingFlags.NonPublic)!;
        CompiledMeshBinding Binding(ulong visualId)
        {
            var visual = graph.Objects[visualId];
            return new(graph, visual, frame, frame.Name, lod => (Illusion.Domain.MeshData?)decode.Invoke(document, [frame, visual, decoder, lod, null, null]));
        }
        return (document, Binding(0x155D0F95DB8204D6), Binding(0x155D745197FC082A));
    }
}
