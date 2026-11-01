using System.Buffers.Binary;
using System.Security.Cryptography;
using Illusion.Assets.Compiled;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void VerifyCompiledEffectMotion(CompiledSceneDocument document, Action<string, bool> check)
    {
        var original = document.Graph.Owner(document.Graph.Objects[0x1065E033E7A12ADD]);
        var before = original.ToArray();
        // Independent in-memory graph only: no authored operator/schema changes are saved.
        var resource = CompiledResource.FromBytes(before, "native-motion-probe");
        var graph = new CompiledGraph([resource]); var library = new CompiledEffectLibraryData(graph);
        var generation = graph.Objects[0x1065E033E7A12ADD]; ulong next = 0xF123000000000001;
        CompiledElement Clone(CompiledElement source)
        {
            var clone = resource.CloneSubtree(source, new Dictionary<ulong, ulong> { [source.ObjectId] = next++ });
            graph.RegisterSubtree(resource, clone); return clone;
        }
        CompiledElement Scalar(float value)
        {
            var clone = Clone(graph.Nodes.First(n => n.TypeId == 0xA431F78B));
            resource.Replace(graph.Field(clone, "m_ConstValue")!, BitConverter.GetBytes(value)); return clone;
        }
        void Bind(CompiledElement op, string field, CompiledElement source)
        {
            var pointer = graph.Field(graph.Field(op, field), "m_Interpolator")!;
            var bytes = graph.Data(pointer).ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(bytes, source.ObjectId);
            resource.Replace(pointer, bytes);
        }
        var originalAcceleration = graph.Nodes.First(n => n.TypeId == 0x16EA57CD);
        var originalRef = library.Effects.SelectMany(library.Generations).SelectMany(n => graph.Items(graph.Field(n, "m_Operators")))
            .First(n => graph.Resolve(n) == originalAcceleration);
        var added = resource.CloneSubtree(originalRef, new Dictionary<ulong, ulong> { [originalAcceleration.ObjectId] = next++ });
        graph.RegisterSubtree(resource, added); var acceleration = graph.Resolve(added)!;
        var vector = Clone(graph.Nodes.First(n => n.TypeId == 0x5E8A135A));
        resource.Replace(graph.Field(vector, "m_ConstValue")!, new[] { 10f, -3f, 2f }.SelectMany(BitConverter.GetBytes).ToArray());
        Bind(acceleration, "m_Acceleration", vector); Bind(acceleration, "m_DampingFactor", Scalar(2)); Bind(acceleration, "m_AccelerationMultiplier", Scalar(1));
        var array = graph.Field(generation, "m_Operators")!; var entries = graph.Items(array);
        var speedEntry = entries.First(n => graph.Resolve(n)?.TypeId == 0x45EA6434);
        array.Children.Insert(array.Children.IndexOf(speedEntry), added);
        resource.Replace(array.Values.First(), BitConverter.GetBytes(entries.Count + 1));
        check("native point acceleration operator stack is available", CompiledEffectPointTimeline.TryCreate(graph, generation, out var timeline, out _));
        string Digest()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int frame = 1; frame <= 1000; frame++)
            {
                var snapshot = timeline!.SampleFrames(frame);
                hash.AppendData(BitConverter.GetBytes(snapshot.Time)); hash.AppendData(BitConverter.GetBytes(snapshot.RandomState));
                hash.AppendData(BitConverter.GetBytes(snapshot.Particles.Count));
                foreach (var p in snapshot.Particles)
                    foreach (float value in new[] { p.AgeSeconds, p.Lifetime, p.NormalizedAge, p.EmissionTime, p.HalfSize.X,
                        p.Position.X, p.Position.Y, p.Position.Z, p.Velocity.X, p.Velocity.Y, p.Velocity.Z })
                        hash.AppendData(BitConverter.GetBytes(value));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        // Both expected digests execute original full Tick+Accel+Speed with these exact inputs.
        check("native acceleration damping and position match1000original frames", Digest() == "FE93A85A530CCC1A40DDCA7A6E3FE03B2874E29B1A7B49BFF03DA6057FE4F379");
        var moving = timeline!.SampleFrames(45).Particles.Single();
        check("native acceleration produces visible displacement and velocity", moving.Position.LengthSquared() > .0001f && moving.Velocity.LengthSquared() > .1f);
        var distance = graph.Field(graph.Resolve(speedEntry), "m_MaxDist")!;
        var originalDistance = graph.Data(distance).ToArray();
        resource.Replace(distance, BitConverter.GetBytes(.004f));
        check("native particle maximum-distance culling matches1000original frames", Digest() == "DE6433135F6CF299A0545CD74F5C429E8DAFF195F5BD0280CBE2BC8A504ACCF3");
        check("native motion radius removes particles before lifetime expiry", timeline.SampleFrames(43).Particles.Count == 1 && timeline.SampleFrames(44).Particles.Count == 0);
        resource.Replace(distance, originalDistance);
        var birth = library.Operators(generation).Single(n => n.TypeId == 0x3EBB96BE);
        Bind(birth, "m_Rate", Scalar(120));
        check("native multiple-birth cone displacement matches1000original frames", Digest() == "32E15644628971DFB9475DE659F8F8F1D8FC430505CB490D2EDC7BD0555A095A");
        check("native newborn subframe offsets remain visible before normalization", timeline.SampleFrames(3).Particles.Any(p => p.AgeSeconds > 0
            && p.NormalizedAge == 0 && p.Position.LengthSquared() > 0));
        var speed = graph.Resolve(speedEntry)!;
        Bind(speed, "m_Speed", Scalar(5)); Bind(speed, "m_SpeedVar", Scalar(2)); Bind(speed, "m_SpeedMultiplier", Scalar(1.5f));
        check("native cone speed variance and acceleration match1000original frames", Digest() == "EAF539DE17A47AA366133CB93DCEADA10D2BF4D3A9A6C57028D20A5805E8C95D");
        check("native seeded speed replay rewinds deterministically", timeline.SampleFrames(30).Particles.SequenceEqual(timeline.SampleFrames(30).Particles));
        var session = timeline.CreateSession();
        while (!session.AdvanceToFrame(45)) { }
        var originalVelocity = session.Snapshot.Particles[0].Velocity;
        Bind(speed, "m_Speed", Scalar(7));
        while (!session.AdvanceToFrame(45)) { }
        var editedReference = timeline.SampleFrames(45);
        check("native point session source edits reset cached curves and RNG", session.Snapshot.Particles.SequenceEqual(editedReference.Particles)
            && session.Snapshot.Particles[0].Velocity != originalVelocity);
        Bind(speed, "m_Speed", Scalar(5));
        while (!session.AdvanceToFrame(45)) { }
        check("native point session source restoration resets exactly", session.Snapshot.Particles[0].Velocity == originalVelocity);
        check("motion preview probes never change document resource bytes", original.ToArray().AsSpan().SequenceEqual(before));
    }
}
