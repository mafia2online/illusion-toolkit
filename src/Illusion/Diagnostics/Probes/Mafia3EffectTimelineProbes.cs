using System.Security.Cryptography;
using Illusion.Assets.Compiled;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void VerifyCompiledEffectTimeline(CompiledSceneDocument document, Action<string, bool> check)
    {
        var graph = document.Graph;
        var library = new CompiledEffectLibraryData(graph);
        check("native point/plane timeline preflight accepts21 verified generations", library.Effects.SelectMany(library.Generations)
            .Count(g => CompiledEffectPointTimeline.TryCreate(graph, g, out _, out _)) == 21);
        var generation = graph.Objects[0x1065E033E7A12ADD];
        var resource = graph.Owner(generation); var before = resource.ToArray();
        check("native continuous random-rate point generation is available", CompiledEffectPointTimeline.TryCreate(graph, generation, out var timeline, out _));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int frame = 1; frame <= 1000; frame++)
        {
            var snapshot = timeline!.SampleFrames(frame);
            hash.AppendData(BitConverter.GetBytes(snapshot.Time)); hash.AppendData(BitConverter.GetBytes(snapshot.RandomState));
            hash.AppendData(BitConverter.GetBytes(snapshot.Particles.Count));
            foreach (var p in snapshot.Particles)
            {
                hash.AppendData(BitConverter.GetBytes(p.AgeSeconds)); hash.AppendData(BitConverter.GetBytes(p.Lifetime));
                hash.AppendData(BitConverter.GetBytes(p.NormalizedAge)); hash.AppendData(BitConverter.GetBytes(p.EmissionTime));
                hash.AppendData(BitConverter.GetBytes(p.HalfSize.X));
                hash.AppendData(BitConverter.GetBytes(p.Position.X)); hash.AppendData(BitConverter.GetBytes(p.Position.Y)); hash.AppendData(BitConverter.GetBytes(p.Position.Z));
            }
        }
        // Original Tick, Init, Birth, Scale, Point, zero-Speed and Texture birth routines, with private
        // array memory and identity-emitter services. Native fast2D size interpolation.
        check("continuous point replay matches original native Tick at1000frames", Convert.ToHexString(hash.GetHashAndReset())
            == "008E730E3BB237D4D6AD94DF0FF945524D14C16D4DE2027A989F9059F8D85EC1");
        var first = timeline!.SampleFrames(42);
        check("continuous point native stochastic birth expiry and rewind", first.Particles.Count == 1 && timeline.SampleFrames(46).Particles.Count == 0
            && timeline.SampleFrames(42).Particles.SequenceEqual(first.Particles) && timeline.SampleFrames(0).Particles.Count == 0);
        var session = timeline.CreateSession();
        bool firstChunk = session.AdvanceToFrame(1000, 7);
        check("native point session bounds forward seek work", !firstChunk && session.Frame > 0 && session.Frame <= 7);
        while (!session.AdvanceToFrame(1000, 100)) { }
        var reference = timeline.SampleFrames(1000);
        check("native point incremental replay equals fresh replay", session.Snapshot.Time == reference.Time
            && session.Snapshot.RandomState == reference.RandomState && session.Snapshot.Particles.SequenceEqual(reference.Particles));
        while (!session.AdvanceToFrame(42)) { }
        check("native point session backwards seek restores exact state", session.Frame == 42 && session.Snapshot.Particles.SequenceEqual(first.Particles));
        var cached = session.Snapshot;
        check("native point repeated snapshot reuses immutable result", ReferenceEquals(cached, session.Snapshot));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool canceledSeek = false;
        try { session.AdvanceFrames(1, cancellationToken: canceled.Token); } catch (OperationCanceledException) { canceledSeek = true; }
        check("native point canceled seek preserves completed frame", canceledSeek && session.Frame == 42 && ReferenceEquals(cached, session.Snapshot));
        session.Reset();
        check("native point session reset returns empty native clock", session.Frame == 0 && session.Snapshot.Time == 0 && session.Snapshot.Particles.Count == 0);
        bool refused = false;
        try { timeline.SampleFrames(1, float.NaN); } catch (ArgumentOutOfRangeException) { refused = true; }
        check("continuous point timeline rejects invalid step values", refused);
        check("continuous point timeline never changes compiled bytes", resource.ToArray().AsSpan().SequenceEqual(before));
        VerifyCompiledEffectRotation(document, check);
        VerifyCompiledEffectBox(document, check);
        VerifyCompiledEffectAtlas(document, check);
        VerifyCompiledEffectMotion(document, check);
        VerifyCompiledEffectPlane(document, check);
    }
}
