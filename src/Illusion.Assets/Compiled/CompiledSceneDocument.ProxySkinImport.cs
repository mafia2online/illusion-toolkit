using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Security.Cryptography;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Imports a verified pure Model/Skeleton stock skin into an owning prepared Kit,
    /// placing it at the family's selected fine owner. Native LOWEST skin is independently copied
    /// into the coarse archive; palettes, inverse binds and positive-weight joint envelopes persist.</summary>
    public IFrameDuplication? ImportPreparedSkinnedProp(string sourceResourcePath, ulong sourceObjectId,
        IFrameNode familyOwner, IFrameNode target, out string? refusal)
        => ImportPreparedDeformedProp(sourceResourcePath, sourceObjectId, familyOwner, target, out refusal, false);

    /// <summary>Imports verified rigid sparse-morph props with independent native inline atlases
    /// and conservative copied envelopes for manual target weights in [0,1].</summary>
    public IFrameDuplication? ImportPreparedMorphProp(string sourceResourcePath, ulong sourceObjectId,
        IFrameNode familyOwner, IFrameNode target, out string? refusal)
        => ImportPreparedDeformedProp(sourceResourcePath, sourceObjectId, familyOwner, target, out refusal, true);

    private IFrameDuplication? ImportPreparedDeformedProp(string sourceResourcePath, ulong sourceObjectId,
        IFrameNode familyOwner, IFrameNode target, out string? refusal, bool morph)
    {
        refusal=null;
        var canonical=familyOwner is ICompiledMeshSource visual?visual.CanonicalFrame:familyOwner;
        if(canonical is not CompiledFrameNode owner || target is not CompiledFrameNode parent || !_frames.ContainsValue(parent)
            || Components(parent.Element).Count()!=1 || Hierarchy(parent)is null || Components(parent.Element).Single().TypeId!=0x360b668d)
        {refusal="Choose an existing prepared fine owner and a pure owning Kit.";return null;}
        var state=_linkedProxyStates.Values.SingleOrDefault(candidate=>candidate.Pairs.ContainsKey(owner));
        if(state is null || !ReferenceEquals(Graph.Owner(owner.Element),Graph.Owner(parent.Element)))
        {refusal="The prepared owner and Kit must belong to the same fine resource.";return null;}
        if((refusal=ValidateProxyMaterialSignatures(state)??ValidateProxyInvariants(state))is not null)return null;
        var pins=CaptureProxyStatePins(state);var resources=Graph.Resources.Select(resource=>resource.CaptureRevertedMutationCheckpoint()).ToArray();
        var filesBefore=state.Files;var hashesBefore=new Dictionary<string,byte[]>(state.FileHashes,StringComparer.OrdinalIgnoreCase);
        var boundsCheckpoint=NativeLods?.CaptureRevertedBoundsCheckpoint();var carryCheckpoint=CaptureRevertedCarryCheckpoint();
        IFrameDuplication? near=null;GeometryCopy? farCopy=null;CompiledFrameNode? fine=null,far=null;SdsFrameNode? farRow=null;
        var farMeshes=new List<MeshData>();var carries=new List<IEditAction>();var textures=new List<ProxySlotTextureCarry>();IEditAction? bounds=null;
        try
        {
            if(!Matrix4x4.Invert(parent.WorldTransform,out var parentInverse))throw new NotSupportedException("The owning Kit transform is singular.");
            near=ImportFrameCore(sourceResourcePath,sourceObjectId,parent,out refusal,preparedSkin:!morph,preparedSkinPlacement:owner.WorldTransform*parentInverse,preparedMorph:morph)??throw new NotSupportedException(refusal);
            fine=(CompiledFrameNode)near.Root.Source!;
            if(morph)ExpandPreparedMorphEnvelopes(fine);
            var child=Graph.Objects[state.Child];var coarseGroup=Graph.Objects[state.Parent];
            var destination=Graph.Owner(state.Pairs[owner].Element);
            void Group(CompiledFrameNode frame,ulong id)
            {
                var model=Components(frame.Element).Single(component=>component.TypeId==0x2da22400);
                var field=Graph.Field(model,"m_LODGroup")??throw new InvalidDataException("The native model lacks its LOD group reference.");
                if(field.DataSize!=12)throw new NotSupportedException("Unknown native model group reference.");
                byte[] bytes=Graph.Data(field).ToArray();BinaryPrimitives.WriteUInt64LittleEndian(bytes,id);Graph.Owner(field).Replace(field,bytes);
            }
            Group(fine,state.Child);
            var selection=SelectProxyLowestGeometry([fine]);
            farCopy=new GeometryCopy(this,destination:destination);farCopy.CopyProxyRenderClosure([fine],selection,child,coarseGroup);farCopy.Finish();
            var element=farCopy.Copied(fine.Element)!;far=new CompiledFrameNode(this,element);
            string name=fine.Name+"__itk_far";destination.SetClonedValue(Graph.Field(element,"m_Name")!,Encoding.UTF8.GetBytes(name+'\0'));
            destination.Replace(Graph.Field(element,"m_HashName")!,BitConverter.GetBytes(Fnv64.Hash(name,Encoding.UTF8)));
            destination.Replace(Graph.Field(element,"m_Transform")!,WriteMatrix(fine.WorldTransform));Group(far,state.Parent);
            float distance=ProxyVisuals(state.Pairs[owner]).Select(v=>Graph.Resolve(Graph.Field(v,"m_MeshSet")))
                .SelectMany(set=>Graph.Items(Graph.Field(set,"m_LODs"))).Select(lod=>Graph.Resolve(Graph.Field(lod,"m_Mesh")))
                .Select(mesh=>BinaryPrimitives.ReadSingleLittleEndian(Graph.Data(Graph.Field(mesh,"m_LODdistance2")!))).Distinct().Single();
            if(!float.IsFinite(distance)||distance<=0)throw new NotSupportedException("The native coarse cutoff is invalid.");
            foreach(var mesh in selection.Meshes)destination.Replace(Graph.Field(farCopy.Copied(mesh),"m_LODdistance2")!,BitConverter.GetBytes(distance));
            _frames.Add(element,far);_sceneCollectionAuthoringOrigins.Add(element);_sceneCollectionAuthoringOrigins.Add(fine.Element);
            AttachProxyCopiedMembership(fine,state.Child);AttachProxyCopiedMembership(far,state.Parent);
            static IEnumerable<ISceneHierarchyNode> Walk(ISceneHierarchyNode row)
            {yield return row;foreach(var childRow in row.Children)foreach(var descendant in Walk(childRow))yield return descendant;}
            var membership=_nativeLodVisuals.First(pair=>ReferenceEquals(pair.Key.Frame,fine.Element)).Value;
            foreach(var row in Walk(near.Root))
            {
                if(row.Mesh is{}mesh)mesh.NativeLod=membership;
                foreach(var lod in row.LodMeshes)lod.NativeLod=membership;
            }
            int first=Meshes.Count;farRow=Build(far,new CompiledMeshDecoder(Graph),[],0);farMeshes.AddRange(Meshes.Skip(first));Roots.Add(farRow);
            state.Pairs.Add(fine,far);
            string fineFolder=Path.GetDirectoryName(Graph.Owner(fine.Element).Path)!,farFolder=Path.GetDirectoryName(destination.Path)!;
            var low=ProxyMaterialHashes([fine],lowest:true);
            foreach(ulong hash in ProxyMaterialHashes([fine],lowest:false,allLevels:true))
                foreach(string folder in new[]{fineFolder}.Concat(low.Contains(hash)?new[]{farFolder}:[]).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if(_proxySlotCarries.TryGetValue((state.Parent,hash,folder),out var existing))
                    {
                        if(!existing.Applied)throw new NotSupportedException("Restore the existing material dependency action before importing this skin.");
                        textures.Add(existing);
                    }
                    else {var texture=CarryProxySlotMaterial(state,hash,folder);textures.Add(texture);carries.Add(texture.Edit);}
                }
            foreach(var resource in new[]{Graph.Owner(fine.Element),destination}.Distinct())
            {
                string folder=Path.GetDirectoryName(resource.Path)!;
                if(!_vehicleResources.TryGetValue(folder,out var manager))_vehicleResources.Add(folder,manager=new(folder));
                carries.Add(manager.AddGenericBudget(Path.GetFileName(resource.Path),new[]{fineFolder,farFolder},out refusal)??throw new NotSupportedException(refusal));
            }
            AcceptProxySlotSourceFiles(state,textures);bounds=RefreshNativeLodBounds();ResetProxyInvariants(state);RefreshNativeLodRenderCoverage();
            return new PreparedSkinImport(this,state.Parent,state.Child,parent,near,farCopy,fine,far,farRow,farMeshes.ToArray(),carries,bounds,textures);
        }
        catch(Exception exception)when(exception is InvalidDataException or NotSupportedException or InvalidOperationException or ArgumentException or IOException or OverflowException)
        {
            bounds?.Undo();if(fine is not null)state.Pairs.Remove(fine);
            if(farRow is not null)Roots.Remove(farRow);foreach(var mesh in farMeshes)Meshes.Remove(mesh);
            if(far is not null){_frames.Remove(far.Element);RemoveProxyCopiedMembership(far);_sceneCollectionAuthoringOrigins.Remove(far.Element);}
            if(fine is not null){RemoveProxyCopiedMembership(fine);_sceneCollectionAuthoringOrigins.Remove(fine.Element);}
            foreach(var carry in carries.AsEnumerable().Reverse())carry.Undo();farCopy?.Detach();near?.Detach();
            state.Files=filesBefore;state.FileHashes=hashesBefore;RestoreProxyStatePins(state,pins);foreach(var restore in resources)restore();boundsCheckpoint?.Invoke();carryCheckpoint();RefreshNativeLodRenderCoverage();
            refusal=exception.Message;return null;
        }
    }

    private sealed class PreparedSkinImport(CompiledSceneDocument document,ulong parentId,ulong childId,CompiledFrameNode parent,
        IFrameDuplication near,GeometryCopy geometry,CompiledFrameNode fine,CompiledFrameNode far,SdsFrameNode farRow,
        MeshData[] meshes,IReadOnlyList<IEditAction> carries,IEditAction bounds,IReadOnlyList<ProxySlotTextureCarry> textures):IFrameDuplication
    {
        private bool _attached=true;
        private readonly Dictionary<CompiledElement,byte[]> _data=CaptureNodes(document,fine,far).Where(node=>node.Event==4).ToDictionary(node=>node,document.ProxyCurrentData);
        private readonly Dictionary<CompiledElement,CompiledElement[]> _children=CaptureNodes(document,fine,far).Where(node=>node.Event==2).ToDictionary(node=>node,document.ProxyCurrentChildren);
        private readonly Dictionary<ulong,string> _materials=document.ProxyMaterialHashes([fine],lowest:false,allLevels:true)
            .ToDictionary(hash=>hash,hash=>CompiledProxyMaterialGuard.TextureSignature(hash)??throw new InvalidDataException("Missing skin material."));
        private readonly Dictionary<string,byte[]> _sourceFiles=textures.SelectMany(texture=>texture.SourceFiles).Where(path=>!Path.GetFileName(path).Equals("SDSContent.xml",StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path=>path,path=>SHA256.HashData(File.ReadAllBytes(path)),StringComparer.OrdinalIgnoreCase);
        private static CompiledElement[] CaptureNodes(CompiledSceneDocument document,CompiledFrameNode fine,CompiledFrameNode far)
        {
            var nodes=document.SelectProxyLowestGeometry([fine,far]).Closure.SelectMany(root=>ProxyNodes(root));
            if(document._links.TryGetValue(fine,out var link))nodes=nodes.Concat(document.CurrentDescendants(link.Entry));
            return nodes.Distinct().ToArray();
        }
        public ISceneHierarchyNode Root=>near.Root;
        public IReadOnlyList<ISceneHierarchyNode> AdditionalRoots=>[farRow];
        private ProxyRuntimeState Validate(bool expected)
        {
            if(!document._linkedProxyStates.TryGetValue(parentId,out var state)||state.Child!=childId||!document._frames.ContainsValue(parent)
                ||state.Pairs.ContainsKey(fine)!=expected||expected&&!ReferenceEquals(state.Pairs[fine],far))
                throw new EditRefusedException("The authored skin's prepared family or Kit changed.");
            if(ValidateProxyMaterialSignatures(state)is{}material)throw new EditRefusedException(material);
            if(document.ValidateProxyInvariants(state)is{}invariant)throw new EditRefusedException(invariant);
            if(_data.Any(pair=>!document.ProxyCurrentData(pair.Key).AsSpan().SequenceEqual(pair.Value))
                ||_children.Any(pair=>!document.ProxyCurrentChildren(pair.Key).SequenceEqual(pair.Value))
                ||_materials.Any(pair=>CompiledProxyMaterialGuard.TextureSignature(pair.Key)!=pair.Value)
                ||_sourceFiles.Any(pair=>!File.Exists(pair.Key)||!SHA256.HashData(File.ReadAllBytes(pair.Key)).AsSpan().SequenceEqual(pair.Value)))
                throw new EditRefusedException("Restore the authored skin's geometry, rig and material dependencies before changing its registration.");
            return state;
        }
        public void ValidateDetach(){if(!_attached)return;_=Validate(true);near.ValidateDetach();bounds.ValidateUndo();}
        public void ValidateReattach(){if(_attached)return;_=Validate(false);near.ValidateReattach();bounds.ValidateRedo();}
        public void Detach()
        {
            if(!_attached)return;ValidateDetach();var state=Validate(true);
            bounds.Undo();state.Pairs.Remove(fine);document.Roots.Remove(farRow);foreach(var mesh in meshes)document.Meshes.Remove(mesh);
            document._frames.Remove(far.Element);document.RemoveProxyCopiedMembership(fine);document.RemoveProxyCopiedMembership(far);
            document._sceneCollectionAuthoringOrigins.Remove(fine.Element);document._sceneCollectionAuthoringOrigins.Remove(far.Element);
            foreach(var carry in carries.Reverse())carry.Undo();geometry.Detach();near.Detach();
            document.ResetProxyInvariants(state);document.RefreshNativeLodRenderCoverage();_attached=false;
        }
        public void Reattach()
        {
            if(_attached)return;ValidateReattach();var state=Validate(false);
            var pins=CaptureProxyStatePins(state);var checkpoints=document.Graph.Resources.Select(resource=>resource.CaptureRevertedMutationCheckpoint()).ToArray();
            var boundsCheckpoint=document.NativeLods?.CaptureRevertedBoundsCheckpoint();var carryCheckpoint=document.CaptureRevertedCarryCheckpoint();bool boundsApplied=false;
            try
            {
                near.Reattach();geometry.Reattach();document._frames.Add(far.Element,far);document.Roots.Add(farRow);document.Meshes.AddRange(meshes);
                document._sceneCollectionAuthoringOrigins.Add(fine.Element);document._sceneCollectionAuthoringOrigins.Add(far.Element);
                state.Pairs.Add(fine,far);document.AttachProxyCopiedMembership(fine,childId);document.AttachProxyCopiedMembership(far,parentId);
                foreach(var carry in carries)carry.Redo();bounds.Redo();boundsApplied=true;document.ResetProxyInvariants(state);document.RefreshNativeLodRenderCoverage();_attached=true;
            }
            catch
            {
                if(boundsApplied)bounds.Undo();state.Pairs.Remove(fine);document.Roots.Remove(farRow);foreach(var mesh in meshes)document.Meshes.Remove(mesh);
                document._frames.Remove(far.Element);document.RemoveProxyCopiedMembership(fine);document.RemoveProxyCopiedMembership(far);
                document._sceneCollectionAuthoringOrigins.Remove(fine.Element);document._sceneCollectionAuthoringOrigins.Remove(far.Element);
                foreach(var carry in carries.Reverse())carry.Undo();geometry.Detach();near.Detach();
                document.RestoreProxyStatePins(state,pins);foreach(var checkpoint in checkpoints)checkpoint();boundsCheckpoint?.Invoke();carryCheckpoint();document.RefreshNativeLodRenderCoverage();throw;
            }
        }
    }
}
