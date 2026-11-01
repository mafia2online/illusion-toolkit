using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestPreparedProxySkin(Action<string,bool> check)
    {
        var archive=new FileInfo(Path.Combine(MafiaEnvironment.CityFolder,"bc_french_east_block_b_lod0.sds"));
        var sourceArchive=new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder,"city_crash","bc_city_crash.sds"));if(!archive.Exists||!sourceArchive.Exists)return;
        var sourceFolder=SdsMeshLoader.EnsureExtracted(sourceArchive);
        var catalog=CompiledSceneDocument.ReadPreparedSkinSources(sourceArchive);
        check("prepared skin source catalog lists actual verified retail cabinet and excludes incomplete envelope",catalog.Any(source=>source.ObjectId==0x06A9E8CFD6F580AA)
            &&catalog.All(source=>source.ObjectId!=0x0B22265B6A6B02EA));
        string cabinet=Path.Combine(sourceFolder,"Generic_4029_6E1E5C7CA4DCBFA3.ires.compiled"),banknote=Path.Combine(sourceFolder,"Generic_4802_CF0534639AC7546B.ires.compiled");if(!File.Exists(cabinet))return;
        var doc=new CompiledSceneDocument(archive);if(doc.PreparedLinkedProxyGroups.Count!=0)return;
        static IEnumerable<ISceneHierarchyNode> Walk(IEnumerable<ISceneHierarchyNode> rows)
        {foreach(var row in rows){yield return row;foreach(var child in Walk(row.Children))yield return child;}}
        var owner=Walk(doc.Roots).Select(row=>row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().First(frame=>frame.SceneIdentity=="guid:0F947BBBC7BC8574");
        var initial=doc.Graph.Resources.Select(resource=>Path.GetDirectoryName(resource.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(Directory.GetFiles)
            .ToDictionary(path=>path,path=>SHA256.HashData(File.ReadAllBytes(path)),StringComparer.OrdinalIgnoreCase);
        var plan=doc.InspectLinkedProxyPreparation(owner,out var refusal)??throw new InvalidDataException(refusal);
        var prep=doc.PrepareLinkedProxyTransforms(plan,out refusal)??throw new InvalidDataException(refusal);
        IFrameDuplication? group=null,skin=null;IEditAction? pose=null,append=null,reparent=null;
        string packed=Path.Combine(MafiaEnvironment.GameRoot,"skin-probe-"+Guid.NewGuid().ToString("N"),"city",Path.GetFileName(plan.DestinationArchive));
        try
        {
            group=doc.CreatePreparedLinkedProxyGroup(owner,"native_skin_probe",out refusal)??throw new InvalidDataException(refusal);var parent=(IFrameNode)group.Root.Source!;
            var before=doc.Graph.Resources.ToDictionary(resource=>resource,resource=>SHA256.HashData(resource.ToArray()));int count=doc.ObjectCount,meshes=doc.Meshes.Count;
            check("prepared skin import refuses an uncontained retail joint envelope before placement",!File.Exists(banknote)||doc.ImportPreparedSkinnedProp(banknote,0x0B22265B6A6B02EA,owner,parent,out _)is null
                &&doc.ObjectCount==count&&doc.Meshes.Count==meshes&&before.All(pair=>SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value)));
            skin=doc.ImportPreparedSkinnedProp(cabinet,0x06A9E8CFD6F580AA,owner,parent,out refusal)??throw new InvalidDataException(refusal);
            var fine=(CompiledSceneDocument.CompiledFrameNode)skin.Root.Source!;var far=(CompiledSceneDocument.CompiledFrameNode)skin.AdditionalRoots.Single().Source!;
            var nearBinding=((ICompiledMeshSource)fine).GeometryBinding!;var farBinding=((ICompiledMeshSource)far).GeometryBinding!;
            var near=nearBinding.DecodeLod(0)!;var coarse=farBinding.DecodeLod(0)!;
            check("authored prepared cabinet has native owning Kit and independent near far immutable skeletons",ReferenceEquals(fine.Parent,parent)&&fine.WorldTransform==owner.WorldTransform
                &&far.WorldTransform==fine.WorldTransform&&doc.VehicleRig(fine)!.Count==5&&doc.VehicleRig(far)!.Count==5
                &&doc.VehicleRig(fine)!.Immutable.ObjectId!=doc.VehicleRig(far)!.Immutable.ObjectId);
            check("authored prepared skin retains exact native geometry palettes and four-weight values",near.Positions.SequenceEqual(coarse.Positions)&&near.Indices.SequenceEqual(coarse.Indices)
                &&near.BoneIndices!.SequenceEqual(coarse.BoneIndices!)&&near.BoneWeights!.SequenceEqual(coarse.BoneWeights!)&&near.Skeleton!.InverseBind.SequenceEqual(coarse.Skeleton!.InverseBind));
            var sourceHashes=doc.Graph.Resources.Where(resource=>resource.Path.StartsWith(sourceFolder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                .ToDictionary(resource=>resource,resource=>SHA256.HashData(resource.ToArray()));
            var rig=doc.VehicleRig(fine)!;var rawIndices=(byte[])near.BoneIndices!.Clone();var rawWeights=(float[])near.BoneWeights!.Clone();
            pose=doc.SetPreparedLinkedProxyBoneTransform(fine,2,rig.LocalRest(2)*Matrix4x4.CreateTranslation(.2f,0,0),out refusal)??throw new InvalidDataException(refusal);
            var posed=nearBinding.DecodeLod(0)!;var farPosed=farBinding.DecodeLod(0)!;
            check("prepared weighted rest edit pairs every model matrix without rewriting influences",rawIndices.SequenceEqual(posed.BoneIndices!)&&rawWeights.SequenceEqual(posed.BoneWeights!)
                &&Enumerable.Range(0,5).All(index=>doc.VehicleRig(fine)!.ModelRest(index)==doc.VehicleRig(far)!.ModelRest(index)));
            var skeleton=doc.Graph.Items(doc.Graph.Field(doc.Graph.Objects[Convert.ToUInt64(fine.SceneIdentity[5..],16)],"m_Components")).Select(doc.Graph.Resolve).Single(component=>component?.TypeId==0x0fa73185)!;
            var box=doc.Graph.Data(doc.Graph.Field(skeleton,"m_LocalAABB")!);var min=new Vector3(BinaryPrimitives.ReadSingleLittleEndian(box),BinaryPrimitives.ReadSingleLittleEndian(box[4..]),BinaryPrimitives.ReadSingleLittleEndian(box[8..]));
            var max=new Vector3(BinaryPrimitives.ReadSingleLittleEndian(box[12..]),BinaryPrimitives.ReadSingleLittleEndian(box[16..]),BinaryPrimitives.ReadSingleLittleEndian(box[20..]));
            bool Contains(MeshData mesh)=>mesh.Indices.Distinct().All(vertex=>
            {
                var point=Vector3.Zero;for(int slot=0;slot<4;slot++){int at=checked((int)vertex*4+slot);int bone=mesh.BoneIndices![at];point+=Vector3.Transform(mesh.Positions[vertex],mesh.Skeleton!.InverseBind[bone]*mesh.LiveRest![bone])*mesh.BoneWeights![at];}
                return point.X>=min.X-1e-4f&&point.Y>=min.Y-1e-4f&&point.Z>=min.Z-1e-4f&&point.X<=max.X+1e-4f&&point.Y<=max.Y+1e-4f&&point.Z<=max.Z+1e-4f;
            });
            check("native joint envelope union contains every deformed near and far weighted vertex",Contains(posed)&&Contains(farPosed));
            append=doc.AddPreparedLinkedProxyBone(fine,"native_skin_unused_attachment",1,Matrix4x4.CreateTranslation(0,0,.1f),.1f,out refusal)??throw new InvalidDataException(refusal);
            check("prepared skin appends an unused bone without renumbering native palette influences",doc.VehicleRig(fine)!.Count==6&&doc.VehicleRig(far)!.Count==6
                &&nearBinding.DecodeLod(0)!.BoneIndices!.SequenceEqual(rawIndices));append.Undo();append=null;
            var model=doc.VehicleRig(fine)!.ModelRest(2);reparent=doc.ReparentPreparedLinkedProxyBone(fine,2,0,out refusal)??throw new InvalidDataException(refusal);
            check("prepared skin bone reparent preserves model bind and existing palette indices",Vector3.Distance(doc.VehicleRig(fine)!.ModelRest(2).Translation,model.Translation)<1e-5f
                &&nearBinding.DecodeLod(0)!.BoneIndices!.SequenceEqual(rawIndices));reparent.Undo();reparent=null;
            doc.SaveWorkingCopy();var fresh=new CompiledSceneDocument(archive);
            check("authored prepared skin saves and independently reopens a complete native family",fresh.LinkedProxyMappingWarnings.Count==0
                &&Walk(fresh.Roots).Select(row=>row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().Any(frame=>frame.SceneIdentity==fine.SceneIdentity));
            Directory.CreateDirectory(Path.GetDirectoryName(packed)!);using(var stream=File.Create(packed))SdsArchive.Pack(MafiaEnvironment.ExtractedDir(new FileInfo(plan.DestinationArchive)),MafiaEnvironment.Profile).Save(stream,new SdsWriteOptions{Compress=true});
            var farOnly=new CompiledSceneDocument(new FileInfo(packed));var independent=Walk(farOnly.Roots).Select(row=>row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().First(frame=>frame.SceneIdentity==far.SceneIdentity);
            var isolated=independent.GeometryBinding!.DecodeLod(0)!;
            check("compressed far-only skin independently resolves its skeleton palette buffers and authored bind",isolated.Positions.SequenceEqual(farPosed.Positions)&&isolated.BoneIndices!.SequenceEqual(farPosed.BoneIndices!)
                &&isolated.BoneWeights!.SequenceEqual(farPosed.BoneWeights!)&&Vector3.Distance(farOnly.VehicleRig(independent)!.ModelRest(2).Translation,doc.VehicleRig(far)!.ModelRest(2).Translation)<1e-5f);
            string replica=Path.Combine(Path.GetDirectoryName(packed)!,archive.Name);
            using(var stream=File.Create(replica))SdsArchive.Pack(MafiaEnvironment.ExtractedDir(archive),MafiaEnvironment.Profile).Save(stream,new SdsWriteOptions{Compress=true});
            string middle=Path.Combine(archive.DirectoryName!,archive.Name.Replace("_lod0.sds","_lod1.sds",StringComparison.OrdinalIgnoreCase));
            if(File.Exists(middle))File.Copy(middle,Path.Combine(Path.GetDirectoryName(packed)!,Path.GetFileName(middle)));
            var unrelatedCopy=new CompiledSceneDocument(new FileInfo(replica));
            check("copied complete family cannot inherit a mapping snapshot belonging to other resource paths",unrelatedCopy.PreparedLinkedProxyGroups.Count==0&&unrelatedCopy.LinkedProxyMappingWarnings.Count>0);
            pose.Undo();pose=null;skin.Detach();skin.Reattach();skin.Detach();skin=null;group.Detach();group=null;prep.Undo();doc.SaveWorkingCopy();
            check("prepared skin import and rig undo preserve original source templates and all prior working files",sourceHashes.All(pair=>SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value))
                &&initial.All(pair=>File.Exists(pair.Key)&&SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)));
        }
        finally
        {
            reparent?.Undo();append?.Undo();pose?.Undo();skin?.Detach();group?.Detach();if(doc.PreparedLinkedProxyGroups.Count>0)prep.Undo();doc.SaveWorkingCopy();
            // The probe writes a new compressed test archive only; original copied SDS archives are never overwritten.
        }
    }
}
