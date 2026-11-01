using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets.Compiled;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void VerifyCompiledEffectPlane(CompiledSceneDocument document, Action<string, bool> check)
    {
        ulong[] ids = [0x1065E033E7A08ED1, 0x1065E033E7A08F5B, 0x1065E033E7A0A871,
            0x1065E033E7A0D995, 0x1065E033E7A0E4E7, 0x1065E033E7A1C8DD];
        var original = document.Graph.Owner(document.Graph.Objects[ids[0]]); byte[] before = original.ToArray();
        var resource = CompiledResource.FromBytes(before, "native-plane-probe"); var graph = new CompiledGraph([resource]);
        check("six retail stationary interior-plane timelines expose their native dimensions", ids.All(id =>
            CompiledEffectPointTimeline.TryCreate(graph, graph.Objects[id], out var preview, out _)
            && preview!.EmitterShape == "plane" && preview.EmitterSize == Vector2.One));
        var generation = graph.Objects[ids[0]];
        CompiledEffectPointTimeline.TryCreate(graph, generation, out var timeline, out _);
        var session = timeline!.CreateSession();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int frame = 1; frame <= 1000; frame++)
        {
            session.AdvanceToFrame(frame, maxWorkMilliseconds: int.MaxValue); var snapshot = session.Snapshot;
            hash.AppendData(BitConverter.GetBytes(snapshot.Time)); hash.AppendData(BitConverter.GetBytes(snapshot.RandomState));
            hash.AppendData(BitConverter.GetBytes(snapshot.Particles.Count));
            foreach (var p in snapshot.Particles)
                foreach (float value in new[] { p.AgeSeconds, p.Lifetime, p.NormalizedAge, p.EmissionTime, p.HalfSize.X, p.Position.X, p.Position.Y, p.Position.Z })
                    hash.AppendData(BitConverter.GetBytes(value));
        }
        // Original Tick1018B3FE0, Init1018B70A0, Birth1019DD750, Scale1019D85E0,
        // Position1019DBB70 and the real retail stack: 1000 frames, 768 live fields.
        check("plane timeline matches original ordered native Tick at1000frames", Convert.ToHexString(hash.GetHashAndReset())
            == "BF68DDCC8FCA65FF370664F202C264CD10EF157B172016883F82F6FBA3646ECB");
        var first = timeline.SampleFrames(61); var point = first.Particles.Single();
        check("plane native birth consumes ordered random XY after life and size", first.RandomState == 3697797142
            && point.Position == new Vector3(.18224126f, .25585192f, 0) && point.AgeSeconds == 0 && point.Lifetime == .1f);
        while (!session.AdvanceToFrame(61)) { }
        check("plane incremental backwards seek restores exact native birth", session.Snapshot.RandomState == first.RandomState
            && session.Snapshot.Particles.SequenceEqual(first.Particles));
        var movingGeneration = graph.Objects[0x1065E033E7A0E4E7];
        CompiledEffectPointTimeline.TryCreate(graph, movingGeneration, out var movingTimeline, out _);
        string MotionDigest(bool velocity)
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var replay = movingTimeline!.CreateSession();
            for (int frame = 1; frame <= 1000; frame++)
            {
                replay.AdvanceToFrame(frame, maxWorkMilliseconds: int.MaxValue); var snapshot = replay.Snapshot;
                digest.AppendData(BitConverter.GetBytes(snapshot.Time)); digest.AppendData(BitConverter.GetBytes(snapshot.RandomState));
                digest.AppendData(BitConverter.GetBytes(snapshot.Particles.Count));
                foreach (var p in snapshot.Particles)
                {
                    foreach (float value in new[] { p.AgeSeconds, p.Lifetime, p.NormalizedAge, p.EmissionTime, p.HalfSize.X, p.Position.X, p.Position.Y, p.Position.Z })
                        digest.AppendData(BitConverter.GetBytes(value));
                    if (velocity)
                        foreach (float value in new[] { p.Velocity.X, p.Velocity.Y, p.Velocity.Z }) digest.AppendData(BitConverter.GetBytes(value));
                }
            }
            return Convert.ToHexString(digest.GetHashAndReset());
        }
        check("retail plane cone-speed stack matches1000original native frames", MotionDigest(false)
            == "544E433D377C4D0C8D3A41AF5CAB6CD724A5641DC7477323A5C4BBE0776C2086");
        var pointerUndo = new Dictionary<CompiledElement, byte[]>(); ulong nextId = 0xF123ABC000000001;
        void Constant(CompiledElement owner, string parameter, float value)
        {
            var pointer = graph.Field(graph.Field(owner, parameter), "m_Interpolator")!;
            pointerUndo.TryAdd(pointer, graph.Data(pointer).ToArray());
            var source = graph.Resolve(pointer)!;
            var clone = resource.CloneSubtree(source, new Dictionary<ulong, ulong> { [source.ObjectId] = nextId++ });
            graph.RegisterSubtree(resource, clone);
            resource.Replace(graph.Field(clone, "m_ConstValue")!, BitConverter.GetBytes(value));
            byte[] reference = graph.Data(pointer).ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(reference, clone.ObjectId); resource.Replace(pointer, reference);
        }
        var movingOps = new CompiledEffectLibraryData(graph).Operators(movingGeneration);
        var movingBirth = movingOps.Single(op => op.TypeId == 0x3EBB96BE);
        var movingSize = movingOps.Single(op => op.TypeId == 0x3040651E);
        var speed = movingOps.Single(op => op.TypeId == 0x45EA6434);
        try
        {
            Constant(movingBirth, "m_Rate", 120); Constant(movingBirth, "m_Amount", 1); Constant(movingBirth, "m_Life", .1f);
            Constant(movingSize, "m_Size", .1f); Constant(speed, "m_Speed", 5); Constant(speed, "m_SpeedVar", 2); Constant(speed, "m_SpeedMultiplier", 1.5f);
            check("plane XY plus cone subframe offsets and velocity match131736original native fields", MotionDigest(true)
                == "5B70A1DC2F664C4C690735A534F993E6346682EA09B409FED2DAE6A0C7BC9083");
            check("plane multiple-birth motion retains native XY and subframe Z", movingTimeline!.SampleFrames(1).Particles.Any(p => p.AgeSeconds > 0 && p.Position.Z > 0 && p.Position.X != 0));
        }
        finally { foreach (var pair in pointerUndo) resource.Replace(pair.Key, pair.Value); }
        var coneSource = graph.Resolve(graph.Field(graph.Field(speed, "m_DivergencyY"), "m_Interpolator"));
        var coneValue = graph.Field(coneSource, "m_ConstValue")!; byte[] oldCone = graph.Data(coneValue).ToArray();
        try
        {
            resource.Replace(coneValue, BitConverter.GetBytes(0f));
            check("plane unverified rectangular speed remains explicitly refused", !CompiledEffectPointTimeline.TryCreate(graph, movingGeneration, out _, out _));
        }
        finally { resource.Replace(coneValue, oldCone); }
        var emitter = graph.Resolve(graph.Field(generation, "m_EmitterNode"));
        var scene = graph.Resolve(graph.Field(emitter, "m_RootSceneObject"));
        var component = graph.Items(graph.Field(scene, "m_Components")).Select(n => graph.Resolve(graph.Field(n, "m_CPtr")) ?? graph.Resolve(n))
            .Single(n => n?.TypeId == 0x230314B5)!;
        var field = graph.Field(component, "m_Data")!; byte[] data = graph.Data(field).ToArray();
        var library = new CompiledEffectLibraryData(graph); var ops = library.Operators(generation);
        var position = ops.Single(op => op.TypeId == 0x51491D77); var distribution = graph.Field(position, "m_Distribution")!;
        byte[] oldDistribution = graph.Data(distribution).ToArray();
        try
        {
            byte[] wider = data.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(wider.AsSpan(60), 2);
            BinaryPrimitives.WriteSingleLittleEndian(wider.AsSpan(64), 6); resource.Replace(field, wider);
            session.AdvanceToFrame(61, maxWorkMilliseconds: int.MaxValue);
            var scaled = session.Snapshot;
            check("plane current-source dimension edit resets incremental state without changing random draws", scaled.RandomState == first.RandomState
                && scaled.EmitterShape == "plane" && scaled.EmitterSize == new Vector2(2, 6)
                && scaled.Particles.Single().Position == new Vector3(point.Position.X * 2, point.Position.Y * 6, 0));
            resource.Replace(field, data); session.AdvanceToFrame(61, maxWorkMilliseconds: int.MaxValue);
            check("plane dimension undo restores exact native session", session.Snapshot.Particles.SequenceEqual(first.Particles) && session.Snapshot.RandomState == first.RandomState);
            resource.Replace(distribution, BitConverter.GetBytes(3));
            check("native plane distribution3 shares the verified interior path", CompiledEffectPointTimeline.TryCreate(graph, generation, out var mode3, out _)
                && mode3!.SampleFrames(61).Particles.SequenceEqual(first.Particles));
            resource.Replace(distribution, BitConverter.GetBytes(1));
            check("plane perimeter distribution remains explicitly refused", !CompiledEffectPointTimeline.TryCreate(graph, generation, out _, out _));
            resource.Replace(distribution, oldDistribution);
            byte[] invalid = data.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(invalid.AsSpan(60), float.NaN); resource.Replace(field, invalid);
            check("plane nonfinite dimensions remain explicitly refused", !CompiledEffectPointTimeline.TryCreate(graph, generation, out _, out _));
            invalid = data.ToArray(); invalid[74] = 0; resource.Replace(field, invalid);
            check("plane unverified world-space transform contract remains refused", !CompiledEffectPointTimeline.TryCreate(graph, generation, out _, out _));
            invalid = data.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(invalid.AsSpan(48), .1f); resource.Replace(field, invalid);
            check("plane random shape scaling remains explicitly refused", !CompiledEffectPointTimeline.TryCreate(graph, generation, out _, out _));
        }
        finally { resource.Replace(field, data); resource.Replace(distribution, oldDistribution); }
        check("plane preview edits and undo leave complete compiled resources byte-exact", resource.ToArray().SequenceEqual(before) && original.ToArray().SequenceEqual(before));
    }
}
