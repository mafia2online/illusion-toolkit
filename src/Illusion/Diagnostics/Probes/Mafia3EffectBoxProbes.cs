using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets.Compiled;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void VerifyCompiledEffectBox(CompiledSceneDocument document, Action<string, bool> check)
    {
        var original = document.Graph.Owner(document.Graph.Objects[0x1065E033E7A21445]); byte[] before = original.ToArray();
        var resource = CompiledResource.FromBytes(before,"native-box-probe"); var graph = new CompiledGraph([resource]);
        var generation = graph.Objects[0x1065E033E7A21445]; var ops = new CompiledEffectLibraryData(graph).Operators(generation);
        var emitter = graph.Resolve(graph.Field(generation,"m_EmitterNode")); var scene = graph.Resolve(graph.Field(emitter,"m_RootSceneObject"));
        var component = graph.Items(graph.Field(scene,"m_Components")).Select(n => graph.Resolve(graph.Field(n,"m_CPtr")) ?? graph.Resolve(n)).Single(n => n?.TypeId == 0x230314B5)!;
        var field = graph.Field(component,"m_Data")!; var data = graph.Data(field).ToArray(); data[72] = 4; data[74] = 1;
        var properties = new CompiledEffectLibrary(graph).Properties(generation,"generation").GetPropertyGroups()
            .SelectMany(group => group.Properties).Where(p => p.Id.StartsWith("emitter:")).ToArray();
        var baseline = resource.ToArray();
        var shape = properties.Single(p => p.Label == "Shape"); var dimensions = properties.Single(p => p.Label == "Dimensions");
        var variation = properties.Single(p => p.Label == "ShapeScaleVariation");
        object? oldShape = shape.Get(), oldDimensions = dimensions.Get(), oldVariation = variation.Get();
        shape.Set!(4L); dimensions.Set!(new Vector3(2,3,5)); variation.Set!(new Vector3(.1f,.2f,.3f));
        check("native emitter authoring exposes distinct dimensions and variation", properties.Length == 4 && (long)shape.Get()! == 4
            && (Vector3)dimensions.Get()! == new Vector3(2,3,5) && (Vector3)variation.Get()! == new Vector3(.1f,.2f,.3f));
        bool invalidShape = false, invalidVector = false;
        try { shape.Set!(6L); } catch (ArgumentOutOfRangeException) { invalidShape = true; }
        try { dimensions.Set!(new Vector3(float.NaN)); } catch (ArgumentOutOfRangeException) { invalidVector = true; }
        check("native emitter rejects invalid fields atomically", invalidShape && invalidVector && (long)shape.Get()! == 4 && (Vector3)dimensions.Get()! == new Vector3(2,3,5));
        shape.Set!(oldShape); dimensions.Set!(oldDimensions); variation.Set!(oldVariation);
        check("native emitter property undo restores all compiled bytes", resource.ToArray().AsSpan().SequenceEqual(baseline));
        var scheduling = new CompiledEffectLibrary(graph).Properties(generation,"generation").GetPropertyGroups()
            .SelectMany(group => group.Properties).Where(p => p.Id.Contains(".nativeData.")).ToArray();
        var oldScheduling = scheduling.Select(p => p.Get()).ToArray();
        foreach (var property in scheduling.Where(p => p.Set is not null)) property.Set!(property.Kind == Illusion.Domain.Properties.PropertyKind.Bool ? (object)true
            : property.Kind == Illusion.Domain.Properties.PropertyKind.Int ? (object)2L : .25f);
        check("native generation data exposes verified fields and protects unproven ribbon allocation", scheduling.Length == 8
            && scheduling.Single(p => p.Label == "IsRibbon") is { IsReadOnly: true,Set: null }
            && BinaryPrimitives.ReadSingleLittleEndian(graph.Data(graph.Field(generation,"m_Data")!)[20..]) == .25f);
        for (int i = 0; i < scheduling.Length; i++) scheduling[i].Set?.Invoke(oldScheduling[i]);
        check("native scheduling field reversal preserves all compiled bytes", resource.ToArray().AsSpan().SequenceEqual(baseline));
        var colorOperator = ops.Single(op => op.TypeId == 0x35C27036); var variationField = graph.Field(colorOperator,"m_ColorVariation")!;
        var oldRgbVariation = graph.Data(variationField).ToArray(); var rgbVariation = new Vector3(10,25,60);
        byte[] rgbBytes = new byte[12];
        for (int i = 0; i < 3; i++) BinaryPrimitives.WriteSingleLittleEndian(rgbBytes.AsSpan(i*4),rgbVariation[i]);
        resource.Replace(variationField,rgbBytes);
        CompiledEffectPointTimeline.TryCreate(graph,generation,out var colored,out _); var coloredSession = colored!.CreateSession();
        coloredSession.AdvanceToFrame(80,maxWorkMilliseconds:int.MaxValue);
        var colorCurve = CompiledEffectCurve.FromParameter(graph,colorOperator,"m_Color");
        bool exactColors = coloredSession.Snapshot.Particles.Count > 0;
        foreach (var particle in coloredSession.Snapshot.Particles)
        {
            var expected = Vector3.Clamp(colorCurve.Sample(particle.EmissionTime,particle.NormalizedAge) * (Vector3.One - rgbVariation*.01f),Vector3.Zero,Vector3.One);
            expected = new((int)(expected.X*255)/255f,(int)(expected.Y*255)/255f,(int)(expected.Z*255)/255f);
            exactColors &= new Vector3(particle.Color.X,particle.Color.Y,particle.Color.Z) == expected;
        }
        check("native timeline RGB variation preserves original bitcast and packed channels",exactColors);
        var colorsBefore = coloredSession.Snapshot.Particles.Select(p => p.Color).ToArray();
        coloredSession.AdvanceToFrame(10,maxWorkMilliseconds:int.MaxValue); coloredSession.AdvanceToFrame(80,maxWorkMilliseconds:int.MaxValue);
        check("native varied RGB replay rewinds exactly",coloredSession.Snapshot.Particles.Select(p => p.Color).SequenceEqual(colorsBefore));
        resource.Replace(variationField,oldRgbVariation);
        check("native RGB authoring reversal preserves every compiled byte",resource.ToArray().AsSpan().SequenceEqual(baseline));
        for (int i = 0; i < 3; i++) { BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(60 + i * 4),new float[] { 2,3,5 }[i]); BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(48 + i * 4),0); }
        resource.Replace(field,data); var position = ops.Single(op => op.TypeId == 0x51491D77); var distribution = graph.Field(position,"m_Distribution")!;
        resource.Replace(distribution,BitConverter.GetBytes(0));
        check("native box surface exposes full authored dimensions", CompiledEffectPointTimeline.TryCreate(graph,generation,out var timeline,out _)
            && timeline!.EmitterShape == "box" && timeline.EmitterDimensions == new Vector3(2,3,5));
        string Digest()
        {
            CompiledEffectPointTimeline.TryCreate(graph,generation,out timeline,out _); var session = timeline!.CreateSession();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int frame = 1; frame <= 1000; frame++)
            {
                session.AdvanceToFrame(frame,maxWorkMilliseconds:int.MaxValue); var snapshot = session.Snapshot;
                hash.AppendData(BitConverter.GetBytes(snapshot.Time)); hash.AppendData(BitConverter.GetBytes(snapshot.RandomState)); hash.AppendData(BitConverter.GetBytes(snapshot.Particles.Count));
                hash.AppendData(BitConverter.GetBytes(snapshot.IntegerRandomStateX)); hash.AppendData(BitConverter.GetBytes(snapshot.IntegerRandomStateY));
                foreach (var p in snapshot.Particles)
                    foreach (float value in new[] { p.AgeSeconds,p.Lifetime,p.NormalizedAge,p.EmissionTime,p.HalfSize.X,p.Position.X,p.Position.Y,p.Position.Z,p.Velocity.X,p.Velocity.Y,p.Velocity.Z,p.Rotation }) hash.AppendData(BitConverter.GetBytes(value));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        check("native box six-face surface and both random streams match original1000 frames", Digest() == "DC6449519C3E430D1D9B1B1E67B6D82789B760B2E24B3445A5B3B5A263C09630");
        resource.Replace(distribution,BitConverter.GetBytes(3));
        check("native box interior matches original1000 frames", Digest() == "8338AC282D6A42425252F05B98B70D420F85FD15D55AE080FE29A83ABA5738E9");
        var birth = ops.Single(op => op.TypeId == 0x3EBB96BE); var pointer = graph.Field(graph.Field(birth,"m_Rate"),"m_Interpolator")!; var source = graph.Resolve(pointer)!;
        var clone = resource.CloneSubtree(source,new Dictionary<ulong,ulong> { [source.ObjectId] = 0xFABC000000001 }); graph.RegisterSubtree(resource,clone); resource.Replace(graph.Field(clone,"m_ConstValue")!,BitConverter.GetBytes(120f));
        var bytes = graph.Data(pointer).ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(bytes,clone.ObjectId); resource.Replace(pointer,bytes);
        resource.Replace(distribution,BitConverter.GetBytes(0));
        check("dense native box surface, cone offsets and texture integer draws match original", Digest() == "FE55A6519ED33E9CE00C4B5C0179ADFDCF650925C3D87135EED1836C8A59BB8B");
        resource.Replace(distribution,BitConverter.GetBytes(3));
        check("dense native box interior and live motion match original", Digest() == "35F0BE6724E0451FFC85CEF95FC18019445B94A4A2DE6173A2848D75E8D8C511");
        foreach (int mode in new[] { 1,2 }) { resource.Replace(distribution,BitConverter.GetBytes(mode)); check("unverified native box distribution stays refused " + mode,!CompiledEffectPointTimeline.TryCreate(graph,generation,out _,out _)); }
        resource.Replace(distribution,BitConverter.GetBytes(3)); var invalid = data.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(invalid.AsSpan(68),float.NaN); resource.Replace(field,invalid);
        check("native box invalid depth refuses before replay",!CompiledEffectPointTimeline.TryCreate(graph,generation,out _,out _));
        check("box probes preserve every original native resource byte",original.ToArray().AsSpan().SequenceEqual(before));
    }
}
