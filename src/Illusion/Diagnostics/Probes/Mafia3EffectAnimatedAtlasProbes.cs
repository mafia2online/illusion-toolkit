using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Illusion.Assets.Compiled;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void VerifyCompiledEffectAnimatedAtlas(CompiledSceneDocument document,Action<string,bool> check)
    {
        var original = document.Graph.Owner(document.Graph.Objects[0x1065E033E7A21445]);var before = original.ToArray();
        var resource = CompiledResource.FromBytes(before,"native-animated-atlas-probe");var graph = new CompiledGraph([resource]);
        var generation = graph.Objects[0x1065E033E7A21445];var operators = new CompiledEffectLibraryData(graph).Operators(generation);
        var texture = operators.Single(op => op.TypeId == 0x21AF2099);var cycle = graph.Items(graph.Field(texture,"m_Cycles")).Single();
        void Int(CompiledElement owner,string field,int value) => resource.Replace(graph.Field(owner,field)!,BitConverter.GetBytes(value));
        void Float(CompiledElement owner,string field,float value) => resource.Replace(graph.Field(owner,field)!,BitConverter.GetBytes(value));
        void Bool(string field,bool value) => resource.Replace(graph.Field(texture,field)!,[value ? (byte)1 : (byte)0]);
        Int(texture,"m_Rows",3);Int(texture,"m_Cols",4);Int(cycle,"m_NumFrames",12);Int(cycle,"m_StartFrame",1);Int(cycle,"m_FirstFrame",1);
        Float(cycle,"m_PlayTime",.7f);Bool("m_Animated",true);Bool("m_BlendedTexture",true);Bool("m_RandomStartFrame",true);Float(texture,"m_AnimPlayDir",.35f);
        var data = graph.Field(generation,"m_Data")!;var bytes = graph.Data(data).ToArray();BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(24),.125f);resource.Replace(data,bytes);
        var birth = operators.Single(op => op.TypeId == 0x3EBB96BE);var ratePointer = graph.Field(graph.Field(birth,"m_Rate"),"m_Interpolator")!;var rateSource = graph.Resolve(ratePointer)!;
        var rateClone = resource.CloneSubtree(rateSource,new Dictionary<ulong,ulong> { [rateSource.ObjectId] = 0xFABC000000001UL });graph.RegisterSubtree(resource,rateClone);
        Float(rateClone,"m_ConstValue",120);bytes = graph.Data(ratePointer).ToArray();BinaryPrimitives.WriteUInt64LittleEndian(bytes,rateClone.ObjectId);resource.Replace(ratePointer,bytes);
        // Independent original Tick + Init + Texture birth/cache/Init/Render, with private
        // memory services, produced these digests over 1,000 ordered frames per case.
        // Each includes clock, float random state, count, both atlas frames, blend byte,
        // and twelve native particle fields (1,408,800 motion components per case).
        string[] expected =
        [
            "6ABCAEB674388548E42992BCC9B0CDB2E8A3ECAAE0B4E3561F0AF470A6C82501",
            "EBA5D42829BA54AF48E9CE76DEBCD5DE4B2914283B3B5B0A1CE5EBF297C9FEFC",
            "F846428BC746B0E770D9790620833E2DDBFBBC420A8C5DAF6F7771E13DF82117",
            "0A050EB38E0E436ED2AD4A5263F53632034D64042492D00A870BB9580A8C11E6",
            "12D94FAC9B1ECCAAE737732A200ABCD9A0A1D0566049E4F72574A97A16873954",
            "5A0A02CC75C85F1DC9A92CA2B578CDBB08450C93BBE00255A714130BFFE45BCD",
            "9C1C072E7D2C287965784CD793F81256E2F8A0F8DF041ACF265F20006BF224FA",
            "ED021B16C95F1DE6BDDDA9FD0545FF61A4194CD344CD2D25989D7E24B7371C96",
            "E68621DA5DD5A3BF8BBF4AA9668C583D39257D20FF54D09AC62A77A15458732B",
            "CC2750520BC9DC5487DCA2E2F9AE6609953EE637BAF61FA83F122F8F55158359",
            "E4B4979181FA069ABC018F8FD1319D02E3AB1FFC9A232438BA44298E1D5A572F",
            "E81B10B747330B230AD3DC80B1D53602FC333FD66B2D7600C87A41429A5E6F1F"
        ];
        int caseIndex = 0;
        for (int end = 0;end < 3;end++) for (int repeated = 0;repeated < 2;repeated++) for (int preload = 0;preload < 2;preload++)
        {
            Int(cycle,"m_EndCondition",end);Int(cycle,"m_RepeatPerCycle",repeated * 3);Bool("m_RespectPreload",preload != 0);
            if (!CompiledEffectPointTimeline.TryCreate(graph,generation,out var timeline,out var refusal)) throw new InvalidDataException(refusal);
            var session = timeline!.CreateSession();using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int frame = 1;frame <= 1000;frame++)
            {
                session.AdvanceToFrame(frame,maxWorkMilliseconds:int.MaxValue);var snapshot = session.Snapshot;
                hash.AppendData(BitConverter.GetBytes(snapshot.Time));hash.AppendData(BitConverter.GetBytes(snapshot.RandomState));hash.AppendData(BitConverter.GetBytes(snapshot.Particles.Count));
                foreach (var p in snapshot.Particles)
                {
                    hash.AppendData(BitConverter.GetBytes(p.AtlasFrame));hash.AppendData(BitConverter.GetBytes(p.AtlasNextFrame));hash.AppendData(BitConverter.GetBytes((int)MathF.Round(p.AtlasBlend * 255)));
                    foreach (float value in new[] { p.AgeSeconds,p.Lifetime,p.NormalizedAge,p.EmissionTime,p.HalfSize.X,p.Position.X,p.Position.Y,p.Position.Z,p.Velocity.X,p.Velocity.Y,p.Velocity.Z,p.Rotation }) hash.AppendData(BitConverter.GetBytes(value));
                }
            }
            check($"native animated atlas original Tick/render1000frames mode{end} repeated{repeated} preload{preload}",Convert.ToHexString(hash.GetHashAndReset()) == expected[caseIndex++]);
            var final = session.Snapshot;session.AdvanceToFrame(13,maxWorkMilliseconds:int.MaxValue);
            while (!session.AdvanceToFrame(1000,maxWorkMilliseconds:int.MaxValue)) { }
            check($"native animated atlas exact rewind mode{end} repeated{repeated} preload{preload}",session.Snapshot.Particles.SequenceEqual(final.Particles)
                && session.Snapshot.RandomState == final.RandomState && session.Snapshot.IntegerRandomStateX == final.IntegerRandomStateX && session.Snapshot.IntegerRandomStateY == final.IntegerRandomStateY);
        }
        Int(cycle,"m_RepeatPerCycle",10000);check("native animated atlas bounds expanded sequence before replay",!CompiledEffectPointTimeline.TryCreate(graph,generation,out _,out _));
        Int(cycle,"m_RepeatPerCycle",0);Float(cycle,"m_PlayTime",0);check("native animated atlas refuses zero timed cycle before replay",!CompiledEffectPointTimeline.TryCreate(graph,generation,out _,out _));
        Int(cycle,"m_RepeatPerCycle",3);Float(cycle,"m_PlayTime",.7f);
        string[] mirrored = ["C37ADE8D6CDA8D91EEDD50436CB78B3356DA9729F009E0F8154D61ECB37F8509",
            "47ABA97D5D346728F8702E4D3A994A303BBEA39F2C104281F025D634C65C82ED",
            "03382E4126356E54CDA2D23B1981E5A60EEE45E250710D1DB96030A29A18E9FC"];
        for (int mirror = 1;mirror <= 3;mirror++)
        {
            Int(texture,"m_RandomMirror",mirror);
            if (!CompiledEffectPointTimeline.TryCreate(graph,generation,out var timeline,out var refusal)) throw new InvalidDataException(refusal);
            var session = timeline!.CreateSession();using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);bool uvValid = true;
            for (int frame = 1;frame <= 1000;frame++)
            {
                session.AdvanceToFrame(frame,maxWorkMilliseconds:int.MaxValue);var snapshot = session.Snapshot;
                hash.AppendData(BitConverter.GetBytes(snapshot.Time));hash.AppendData(BitConverter.GetBytes(snapshot.RandomState));hash.AppendData(BitConverter.GetBytes(snapshot.Particles.Count));
                foreach (var p in snapshot.Particles)
                {
                    hash.AppendData(BitConverter.GetBytes(p.AtlasMirror));hash.AppendData(BitConverter.GetBytes(p.AtlasFrame));hash.AppendData(BitConverter.GetBytes(p.AtlasNextFrame));hash.AppendData(BitConverter.GetBytes((int)MathF.Round(p.AtlasBlend * 255)));
                    foreach (float value in new[] { p.AgeSeconds,p.Lifetime,p.NormalizedAge,p.EmissionTime,p.HalfSize.X,p.Position.X,p.Position.Y,p.Position.Z,p.Velocity.X,p.Velocity.Y,p.Velocity.Z,p.Rotation }) hash.AppendData(BitConverter.GetBytes(value));
                    uvValid &= p.AtlasMirror is >= 0 and <= 3 && ((p.AtlasUv.X < p.AtlasUv.Z) == ((p.AtlasMirror & 1) != 0))
                        && ((p.AtlasUv.Y < p.AtlasUv.W) == ((p.AtlasMirror & 2) != 0)) && p.NextAtlasUv is { } next
                        && ((next.X < next.Z) == ((p.AtlasMirror & 1) != 0)) && ((next.Y < next.W) == ((p.AtlasMirror & 2) != 0));
                }
            }
            check($"native atlas mirror{mirror} matches original birth/render1000frames",Convert.ToHexString(hash.GetHashAndReset()) == mirrored[mirror - 1]);
            check($"native atlas mirror{mirror} applies both native UV endpoint swaps",uvValid);
        }
        Int(texture,"m_RandomMirror",4);check("native atlas refuses unverified mirror modes",!CompiledEffectPointTimeline.TryCreate(graph,generation,out _,out _));
        Int(texture,"m_RandomMirror",0);Int(cycle,"m_FirstFrame",5);Int(cycle,"m_NumFrames",6);
        string[] shifted = ["A3B905E72CF893D54C2685F4C5BDAFA52FCAA2B5DFAC36F43A6E62D43DD4FDED",
            "35B579BB7CF4F86D55F8C5E27C442DB6970858CF2C6171B2870ED443A300B592",
            "CA07CFFE1951A8E08B19EF0068646E028A3533142A7804CEB065D06E044FB482"];
        for (int end = 0;end < 3;end++)
        {
            Int(cycle,"m_EndCondition",end);
            if (!CompiledEffectPointTimeline.TryCreate(graph,generation,out var timeline,out var refusal)) throw new InvalidDataException(refusal);
            var session = timeline!.CreateSession();using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int frame = 1;frame <= 1000;frame++)
            {
                session.AdvanceToFrame(frame,maxWorkMilliseconds:int.MaxValue);var snapshot = session.Snapshot;
                hash.AppendData(BitConverter.GetBytes(snapshot.Time));hash.AppendData(BitConverter.GetBytes(snapshot.RandomState));hash.AppendData(BitConverter.GetBytes(snapshot.Particles.Count));
                foreach (var p in snapshot.Particles)
                {
                    hash.AppendData(BitConverter.GetBytes(p.AtlasFrame));hash.AppendData(BitConverter.GetBytes(p.AtlasNextFrame));hash.AppendData(BitConverter.GetBytes((int)MathF.Round(p.AtlasBlend * 255)));
                    foreach (float value in new[] { p.AgeSeconds,p.Lifetime,p.NormalizedAge,p.EmissionTime,p.HalfSize.X,p.Position.X,p.Position.Y,p.Position.Z,p.Velocity.X,p.Velocity.Y,p.Velocity.Z,p.Rotation }) hash.AppendData(BitConverter.GetBytes(value));
                }
            }
            check($"native shifted atlas interval mode{end} matches original Tick/render1000frames",Convert.ToHexString(hash.GetHashAndReset()) == shifted[end]);
        }
        Int(cycle,"m_FirstFrame",8);check("native atlas refuses frame intervals beyond the authored grid",!CompiledEffectPointTimeline.TryCreate(graph,generation,out _,out _));
        check("animated atlas probes preserve original compiled resource bytes",original.ToArray().AsSpan().SequenceEqual(before));
    }
}
