using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestPreparedProxyRigCollision(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_french_east_block_b_lod0.sds")); if (!archive.Exists) return;
        static IEnumerable<ISceneHierarchyNode> Walk(IEnumerable<ISceneHierarchyNode> rows)
        { foreach (var row in rows) { yield return row; foreach (var child in Walk(row.Children)) yield return child; } }
        static CompiledSceneDocument.CompiledFrameNode Find(CompiledSceneDocument document, string id) => Walk(document.Roots).Select(row => row.Source)
            .OfType<CompiledSceneDocument.CompiledFrameNode>().First(frame => frame.SceneIdentity == id);
        static CompiledElement[] Placements(CompiledSceneDocument document, CompiledSceneDocument.CompiledFrameNode frame) => document.Graph.Items(document.Graph.Field(document.Graph.Objects[Convert.ToUInt64(frame.SceneIdentity[5..],16)], "m_Components"))
            .Select(document.Graph.Resolve).OfType<CompiledElement>().Where(component => component.TypeId == 0x2b7b4775)
            .SelectMany(component => document.Graph.Items(document.Graph.Field(component, "m_ElementDefs"))).Select(entry => document.Graph.Field(entry, "m_Data")!).ToArray();
        static Matrix4x4 Matrix(ReadOnlySpan<byte> bytes)
        { var f = new float[12]; for (int i=0;i<f.Length;i++) f[i]=BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i*4,4))); return new(f[0],f[4],f[8],0,f[1],f[5],f[9],0,f[2],f[6],f[10],0,f[3],f[7],f[11],1); }
        var original = new CompiledSceneDocument(archive); if (original.PreparedLinkedProxyGroups.Count != 0) return;
        var dock = Find(original, "guid:0DCE12ED54CE1786");
        var originalBytes = original.Graph.Resources.ToDictionary(resource => resource.Path, resource => File.ReadAllBytes(resource.Path));
        IFrameDuplication? attached = null, sentinel = null; IEditAction? preparation = null, edit = null; CompiledSceneDocument? document = null;
        try
        {
            attached = original.AddCollision(dock, "box", Vector3.One, Matrix4x4.CreateTranslation(1, 2, 3), null, 103, out var refusal, parentBone: 1) ?? throw new InvalidDataException(refusal);
            sentinel = original.AddCollision(dock, "box", Vector3.One, Matrix4x4.CreateTranslation(-2, 0, 0), null, 103, out refusal) ?? throw new InvalidDataException(refusal);
            original.SaveWorkingCopy(); document = new CompiledSceneDocument(archive); dock = Find(document, dock.SceneIdentity);
            var owner = Find(document, "guid:0F947BBBC7BC8574");
            var plan = document.InspectLinkedProxyPreparation(owner, out refusal) ?? throw new InvalidDataException(refusal);
            preparation = document.PrepareLinkedProxyTransforms(plan, out refusal) ?? throw new InvalidDataException(refusal);
            var before = document.Graph.Resources.ToDictionary(resource => resource, resource => SHA256.HashData(resource.ToArray()));
            var fields = Placements(document, dock); var values = fields.ToDictionary(field => field, field => document.Graph.Data(field).ToArray());
            var rig = document.VehicleRig(dock)!; var previous = rig.ModelRest(1); Matrix4x4.Invert(previous, out var inverse);
            edit = document.SetPreparedLinkedProxyBoneTransform(dock, 1, rig.LocalRest(1) * Matrix4x4.CreateTranslation(.5f, .1f, 0), out refusal) ?? throw new InvalidDataException(refusal);
            var delta = inverse * document.VehicleRig(dock)!.ModelRest(1);
            check("prepared static collision follows its authored bind bone exactly once", fields.Where(field => BinaryPrimitives.ReadUInt16LittleEndian(values[field].AsSpan(14)) == 1)
                .All(field => Vector3.Distance(Matrix(document.Graph.Data(field).Slice(24,48)).Translation, (Matrix(values[field].AsSpan(24,48)) * delta).Translation) < 1e-5f));
            check("frame-attached collision and all nonmatrix native placement bytes remain unchanged", fields.All(field =>
            {
                var current = document.Graph.Data(field); var prior = values[field];
                return current[..24].SequenceEqual(prior.AsSpan(0,24)) && current[72..].SequenceEqual(prior.AsSpan(72))
                    && (BinaryPrimitives.ReadUInt16LittleEndian(prior.AsSpan(14)) != ushort.MaxValue || current.SequenceEqual(prior));
            }));
            document.SaveWorkingCopy(); var reopened = new CompiledSceneDocument(archive); var fresh = Find(reopened, dock.SceneIdentity);
            check("collision-aware rest edits independently reopen with matching physical placements and mapping", reopened.LinkedProxyMappingWarnings.Count == 0
                && Placements(reopened, fresh).Select(field => Convert.ToHexString(reopened.Graph.Data(field))).SequenceEqual(fields.Select(field => Convert.ToHexString(document.Graph.Data(field)))));
            edit.Undo(); check("collision-aware rig undo restores every native resource byte", before.All(pair => SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value)));
            edit.Redo(); edit.Undo(); edit = null; preparation.Undo(); preparation = null; document.SaveWorkingCopy();
            sentinel.Detach(); sentinel = null; attached.Detach(); attached = null; original.SaveWorkingCopy();
            check("collision-aware rig preparation and authored volume undo restore original compiled files", originalBytes.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)));
        }
        finally
        {
            edit?.Undo(); preparation?.Undo(); document?.SaveWorkingCopy(); sentinel?.Detach(); attached?.Detach(); original.SaveWorkingCopy();
        }
    }
}
