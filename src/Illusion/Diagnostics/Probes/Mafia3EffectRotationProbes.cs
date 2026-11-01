using System.Buffers.Binary;
using System.Security.Cryptography;
using Illusion.Assets.Compiled;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void VerifyCompiledEffectRotation(CompiledSceneDocument document, Action<string, bool> check)
    {
        var original = document.Graph.Owner(document.Graph.Objects[0x1065E033E7A21445]); byte[] before = original.ToArray();
        var resource = CompiledResource.FromBytes(before, "native-billboard-probe"); var graph = new CompiledGraph([resource]);
        var generation = graph.Objects[0x1065E033E7A21445];
        check("native billboard rotation generation is available", CompiledEffectPointTimeline.TryCreate(graph,generation,out var timeline,out _));
        string Digest()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var session = timeline!.CreateSession();
            for (int frame = 1; frame <= 1000; frame++)
            {
                session.AdvanceToFrame(frame,maxWorkMilliseconds:int.MaxValue); var snapshot = session.Snapshot;
                hash.AppendData(BitConverter.GetBytes(snapshot.Time)); hash.AppendData(BitConverter.GetBytes(snapshot.RandomState)); hash.AppendData(BitConverter.GetBytes(snapshot.Particles.Count));
                foreach (var p in snapshot.Particles)
                    foreach (float value in new[] { p.AgeSeconds,p.Lifetime,p.NormalizedAge,p.EmissionTime,p.HalfSize.X,p.Position.X,p.Position.Y,p.Position.Z,p.Velocity.X,p.Velocity.Y,p.Velocity.Z,p.Rotation }) hash.AppendData(BitConverter.GetBytes(value));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        // Original Tick/Init/Birth/Scale/Fast2DSize/Point/Speed/Rotation/Texture routines,
        // private array/identity services; all11460 live fields compared bit-for-bit.
        check("retail billboard matches1000 original native frames including angle", Digest() == "C257907EF2519B7D2D3DF3CB726C41BFE5956166221C2D8F0E242B7334FDC110");
        var first = timeline!.SampleFrames(61);
        check("native billboard initial degrees convert to radians", first.Particles.Count == 1 && first.Particles[0].Rotation == 180 * .01745329238474369f);
        var rotation = new CompiledEffectLibraryData(graph).Operators(generation).Single(op => op.TypeId == 0x2DBB6813); ulong next = 0xF123ABC000000001;
        void Bind(string field,float value)
        {
            var pointer = graph.Field(graph.Field(rotation,field),"m_Interpolator")!; var source = graph.Resolve(pointer)!;
            var clone = resource.CloneSubtree(source,new Dictionary<ulong,ulong> { [source.ObjectId] = next++ }); graph.RegisterSubtree(resource,clone);
            resource.Replace(graph.Field(clone,"m_ConstValue")!,BitConverter.GetBytes(value));
            var bytes = graph.Data(pointer).ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(bytes,clone.ObjectId); resource.Replace(pointer,bytes);
        }
        resource.Replace(graph.Field(rotation,"m_InitialRotation")!,BitConverter.GetBytes(45f)); resource.Replace(graph.Field(rotation,"m_InitialRotationVar")!,BitConverter.GetBytes(180f));
        Bind("m_Spin",1.25f); Bind("m_SpinVar",30); Bind("m_SpinDirection",-.25f);
        check("random initial angle, spin direction and seed variance match native1000 frames", Digest() == "8AA476AB28546BC708F280564927E48F8003BE5C9A484219F3DE911F98B5F693");
        var spun = timeline.SampleFrames(62);
        check("native billboard angle evolves and rewinds deterministically", spun.Particles.Count == 1 && spun.Particles[0].Rotation != timeline.SampleFrames(61).Particles[0].Rotation
            && spun.Particles.SequenceEqual(timeline.SampleFrames(62).Particles));
        var orientation = graph.Field(rotation,"m_OrientationType")!; resource.Replace(orientation,BitConverter.GetBytes(1));
        check("unverified motion alignment stays explicitly refused", !CompiledEffectPointTimeline.TryCreate(graph,generation,out _,out _)); resource.Replace(orientation,BitConverter.GetBytes(0));
        resource.Replace(graph.Field(rotation,"m_InitialRotationVar")!,BitConverter.GetBytes(float.NaN));
        check("native billboard invalid initial angles refuse", !CompiledEffectPointTimeline.TryCreate(graph,generation,out _,out _));
        check("billboard probes preserve every original document byte", original.ToArray().AsSpan().SequenceEqual(before));
    }
}
