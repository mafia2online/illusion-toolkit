using System.Numerics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Illusion.Assets.Compiled;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void VerifyCompiledEffectAtlas(CompiledSceneDocument document,Action<string,bool> check)
    {
        var original = document.Graph.Owner(document.Graph.Objects[0x1065E033E7A21445]);var before = original.ToArray();
        var resource = CompiledResource.FromBytes(before,"native-static-atlas-probe");var graph = new CompiledGraph([resource]);
        var generation = graph.Objects[0x1065E033E7A21445];var texture = new CompiledEffectLibraryData(graph).Operators(generation).Single(op => op.TypeId == 0x21AF2099);
        var cycleCountProperty = new CompiledEffectLibrary(graph).Properties(generation,"generation").GetPropertyGroups().SelectMany(group => group.Properties)
            .Single(p => p.Id.EndsWith(".m_CyclesCount",StringComparison.Ordinal));
        check("native atlas cycle count requires structural authoring and stays read-only",cycleCountProperty.IsReadOnly && cycleCountProperty.Set is null);
        void Set(CompiledElement node,string field,int value) => resource.Replace(graph.Field(node,field)!,BitConverter.GetBytes(value));
        Set(texture,"m_Rows",3);Set(texture,"m_Cols",4);
        var cycle = graph.Items(graph.Field(texture,"m_Cycles")).Single();Set(cycle,"m_NumFrames",12);Set(cycle,"m_StartFrame",8);
        CompiledEffectPointTimeline.TryCreate(graph,generation,out var timeline,out _);var session = timeline!.CreateSession();session.AdvanceToFrame(80,maxWorkMilliseconds:int.MaxValue);
        var fixedParticle = session.Snapshot.Particles.Single();float dv = -1 / 3f;
        check("native static atlas selects authored one-based start and repeated-add reversed UV origin",fixedParticle.AtlasFrame == 7
            && fixedParticle.AtlasUv == new Vector4(.25f,1 + dv,0,1 + dv + dv));
        resource.Replace(graph.Field(texture,"m_RandomStartFrame")!,[1]);session.Reset();session.AdvanceToFrame(80,maxWorkMilliseconds:int.MaxValue);
        var randomSnapshot = session.Snapshot;
        check("native static atlas random start remains within its explicit cycle",randomSnapshot.Particles.Count > 0 && randomSnapshot.Particles.All(p => p.AtlasFrame is >= 0 and < 12));
        session.AdvanceToFrame(10,maxWorkMilliseconds:int.MaxValue);session.AdvanceToFrame(80,maxWorkMilliseconds:int.MaxValue);
        check("native static atlas backwards replay restores frames UVs and both random streams",session.Snapshot.Particles.SequenceEqual(randomSnapshot.Particles)
            && session.Snapshot.IntegerRandomStateX == randomSnapshot.IntegerRandomStateX && session.Snapshot.IntegerRandomStateY == randomSnapshot.IntegerRandomStateY);
        var birth = new CompiledEffectLibraryData(graph).Operators(generation).Single(op => op.TypeId == 0x3EBB96BE);
        var ratePointer = graph.Field(graph.Field(birth,"m_Rate"),"m_Interpolator")!;var rateSource = graph.Resolve(ratePointer)!;
        var rateClone = resource.CloneSubtree(rateSource,new Dictionary<ulong,ulong> { [rateSource.ObjectId] = 0xFABC12300000001 });graph.RegisterSubtree(resource,rateClone);
        resource.Replace(graph.Field(rateClone,"m_ConstValue")!,BitConverter.GetBytes(120f));var pointerBytes = graph.Data(ratePointer).ToArray();BinaryPrimitives.WriteUInt64LittleEndian(pointerBytes,rateClone.ObjectId);resource.Replace(ratePointer,pointerBytes);
        string Digest()
        {
            CompiledEffectPointTimeline.TryCreate(graph,generation,out var replay,out _);var native = replay!.CreateSession();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int frame = 1;frame <= 1000;frame++)
            {
                native.AdvanceToFrame(frame,maxWorkMilliseconds:int.MaxValue);var snapshot = native.Snapshot;
                hash.AppendData(BitConverter.GetBytes(snapshot.Time));hash.AppendData(BitConverter.GetBytes(snapshot.RandomState));hash.AppendData(BitConverter.GetBytes(snapshot.Particles.Count));
                foreach (var p in snapshot.Particles)
                {
                    hash.AppendData(BitConverter.GetBytes(p.AtlasFrame));
                    foreach (float value in new[] { p.AgeSeconds,p.Lifetime,p.NormalizedAge,p.EmissionTime,p.HalfSize.X,p.Position.X,p.Position.Y,p.Position.Z,p.Velocity.X,p.Velocity.Y,p.Velocity.Z,p.Rotation }) hash.AppendData(BitConverter.GetBytes(value));
                }
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        check("native random static atlas matches original ordered Tick over1408800 fields",Digest() == "7B7734D360330CA03BD0691F4E8A5C8E16099D3F1C6B323DDF2CA6C1FA8C696C");
        resource.Replace(graph.Field(texture,"m_RandomStartFrame")!,[0]);
        check("native fixed static atlas matches original ordered Tick over1408800 fields",Digest() == "6CA38A6CA5E07275E24E2EE3764EAB569FE4A247595840F3221036AF22C58D03");
        Set(texture,"m_Cols",0);check("native static atlas rejects invalid grid before replay",!CompiledEffectPointTimeline.TryCreate(graph,generation,out _,out _));Set(texture,"m_Cols",4);
        Set(cycle,"m_StartFrame",13);check("native static atlas rejects an out-of-grid start",!CompiledEffectPointTimeline.TryCreate(graph,generation,out _,out _));Set(cycle,"m_StartFrame",8);
        resource.Replace(graph.Field(texture,"m_Animated")!,[1]);Set(cycle,"m_FirstFrame",0);
        check("native atlas refuses nonpositive first-frame animated cycles",!CompiledEffectPointTimeline.TryCreate(graph,generation,out _,out _));
        check("native atlas probes preserve every original resource byte",original.ToArray().AsSpan().SequenceEqual(before));
        VerifyCompiledEffectAnimatedAtlas(document,check);
    }
}
