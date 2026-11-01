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
    internal static void TestPreparedProxyRigGlass(Action<string,bool> check)
    {
        var archive=new FileInfo(Path.Combine(MafiaEnvironment.CityFolder,"bc_french_east_block_b_lod0.sds"));if(!archive.Exists)return;
        static IEnumerable<ISceneHierarchyNode> Walk(IEnumerable<ISceneHierarchyNode> rows)
        {foreach(var row in rows){yield return row;foreach(var child in Walk(row.Children))yield return child;}}
        static CompiledSceneDocument.CompiledFrameNode Find(CompiledSceneDocument doc)=>Walk(doc.Roots).Select(row=>row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().First(frame=>frame.SceneIdentity=="guid:0F947BBBC7BC8574");
        static Matrix4x4 Matrix(ReadOnlySpan<byte> bytes)
        {var f=new float[12];for(int i=0;i<12;i++)f[i]=BinaryPrimitives.ReadSingleLittleEndian(bytes[(i*4)..]);return new(f[0],f[4],f[8],0,f[1],f[5],f[9],0,f[2],f[6],f[10],0,f[3],f[7],f[11],1);}
        var doc=new CompiledSceneDocument(archive);if(doc.PreparedLinkedProxyGroups.Count!=0)return;var owner=Find(doc);var graph=doc.Graph;
        var files=graph.Resources.ToDictionary(resource=>resource.Path,resource=>File.ReadAllBytes(resource.Path));
        var plan=doc.InspectLinkedProxyPreparation(owner,out var refusal)??throw new InvalidDataException(refusal);
        var prep=doc.PrepareLinkedProxyTransforms(plan,out refusal)??throw new InvalidDataException(refusal);IEditAction? edit=null;
        try
        {
            var element=graph.Objects[0x0F947BBBC7BC8574];
            var components=graph.Items(graph.Field(element,"m_Components")).Select(graph.Resolve).OfType<CompiledElement>().ToArray();
            var glass=components.Single(component=>component.TypeId==0xe27be3b3);var table=graph.Field(glass,"m_GlassElements")!;var glassBytes=graph.Data(table).ToArray();
            ushort bone=BinaryPrimitives.ReadUInt16LittleEndian(glassBytes.AsSpan(24));
            var cc=components.Single(component=>component.TypeId==0x2b7b4775);
            var placements=graph.Items(graph.Field(cc,"m_ElementDefs")).Select(entry=>graph.Field(entry,"m_Data")!).ToDictionary(field=>field,field=>graph.Data(field).ToArray());
            var before=graph.Resources.ToDictionary(resource=>resource,resource=>SHA256.HashData(resource.ToArray()));
            var rig=doc.VehicleRig(owner)!;var oldModels=Enumerable.Range(0,rig.Count).Select(rig.ModelRest).ToArray();
            check("retail static glass family exposes its verified existing immutable bones",doc.InspectPreparedLinkedProxyBones(owner,out refusal).Count==171&&refusal is null&&placements.Count==166);
            edit=doc.SetPreparedLinkedProxyBoneTransform(owner,bone,rig.LocalRest(bone)*Matrix4x4.CreateTranslation(.2f,0,0),out refusal)??throw new InvalidDataException(refusal);
            var current=doc.VehicleRig(owner)!;Matrix4x4.Invert(oldModels[bone],out var inverse);var delta=inverse*current.ModelRest(bone);
            check("retail pane rest edit retains all native glass IDs state and UV records",graph.Data(table).SequenceEqual(glassBytes)&&current.Count==rig.Count);
            check("retail pane collision follows changed bind while unrelated pane placements stay exact",placements.All(pair=>
            {
                var bytes=graph.Data(pair.Key);ushort attached=BinaryPrimitives.ReadUInt16LittleEndian(pair.Value.AsSpan(14));
                return attached==bone?Vector3.Distance(Matrix(bytes.Slice(24,48)).Translation,(Matrix(pair.Value.AsSpan(24,48))*delta).Translation)<1e-5f:bytes.SequenceEqual(pair.Value);
            }));
            var far=(CompiledSceneDocument.CompiledFrameNode)doc.GetLinkedProxyTransformTargets(owner).Single();
            check("retail glass pane fine and generated far immutable binds remain identical",Enumerable.Range(0,current.Count).All(index=>current.ModelRest(index)==doc.VehicleRig(far)!.ModelRest(index)));
            doc.SaveWorkingCopy();var fresh=new CompiledSceneDocument(archive);var freshOwner=Find(fresh);
            check("retail static glass authoring independently reopens with physical and mapping correspondence",fresh.LinkedProxyMappingWarnings.Count==0&&fresh.InspectPreparedLinkedProxyBones(freshOwner,out refusal).Count==171
                &&Vector3.Distance(fresh.VehicleRig(freshOwner)!.ModelRest(bone).Translation,current.ModelRest(bone).Translation)<1e-5f);
            edit.Undo();check("retail glass rig undo restores every native resource byte",before.All(pair=>SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value)));
            edit.Redo();edit.Undo();edit=null;prep.Undo();doc.SaveWorkingCopy();
            check("retail glass preparation and rest edit undo restore original compiled files",files.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)));
        }
        finally{edit?.Undo();if(doc.PreparedLinkedProxyGroups.Count>0)prep.Undo();doc.SaveWorkingCopy();}
    }
}
