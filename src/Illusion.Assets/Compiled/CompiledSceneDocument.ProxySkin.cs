using System.Buffers.Binary;
using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    // This contract is deliberately independent of gameplay/animation components. Native palettes
    // remain local-to-global IDs; the immutable inverse bind and raw four-weight streams stay intact.
    internal bool PreparedSkinContract(CompiledFrameNode frame, out string? refusal)
    {
        refusal = null;
        try
        {
            var components = Components(frame.Element).ToArray();
            if (frame.Children.Count != 0 || components.Length != 2 || components.Count(c=>c.TypeId==0x0fa73185)!=1 || components.Count(c=>c.TypeId==0x2da22400)!=1)
                throw new NotSupportedException("Prepared skin requires a pure native Model/Skeleton leaf without gameplay, physics or morph components.");
            if(Graph.Field(frame.Element,"m_Components")is not{}componentList||CurrentItems(componentList).Count!=components.Length
                ||Graph.Field(frame.Element,"m_ComponentsMarkedForDelete")is{}deleted&&CurrentItems(deleted).Count!=0
                ||CurrentDescendants(frame.Element).Any(node=>!ReferenceEquals(node,frame.Element)&&node.Event==2&&node.TypeId==0x4403597e))
                throw new NotSupportedException("Unresolved, deleted or nested component ownership cannot be imported as a pure prepared skin.");
            var rig = VehicleRig(frame) ?? throw new InvalidDataException("Missing immutable skin skeleton.");
            var jointBoxes = Graph.Data(Graph.Field(rig.Immutable,"m_BoneAABBs")!); bool skinned=false;
            for (int bone=0;bone<rig.Count;bone++)
            {
                if (rig.Parent(bone)>=bone || !CompiledMeshBinding.IsRigidBind(rig.LocalRest(bone)) || !CompiledMeshBinding.IsRigidBind(rig.Skeleton.InverseBind[bone]))
                    throw new NotSupportedException("Skin needs finite rigid parent-first rest and inverse-bind matrices.");
                ValidateNativeBounds(new(CompiledMeshDecoder.Vector(jointBoxes[(4+bone*24)..]),CompiledMeshDecoder.Vector(jointBoxes[(16+bone*24)..])));
            }
            var skeleton = components.Single(c=>c.TypeId==0x0fa73185);
            if(Graph.Field(skeleton,"m_LocalAABB")is not{DataSize:24}||Graph.Field(skeleton,"m_WorldAABB")is not{DataSize:24})
                throw new NotSupportedException("The native skeleton lacks complete local and world envelope fields.");
            if (ProxyReferencePresent(Graph.Field(skeleton,"m_ImmutableOwningPtr")))
                throw new NotSupportedException("An inline owning skeleton requires a separate prepared-skin ownership rewrite.");
            if (ProxyReferencePresent(Graph.Field(skeleton,"m_VirtualBoneImmutableData")))
                throw new NotSupportedException("Virtual-bone dependencies cannot yet be authored as prepared skin.");
            var decoder=new CompiledMeshDecoder(Graph);
            foreach(var visual in ProxyVisuals(frame))
            {
                if(Graph.Field(visual,"m_Data")is not{DataSize:88} visualData || BinaryPrimitives.ReadUInt16LittleEndian(Graph.Data(visualData)[46..])>=rig.Count)
                    throw new NotSupportedException("Weighted prepared visuals require a valid native skeleton binding.");
                var set=Graph.Resolve(Graph.Field(visual,"m_MeshSet"))??throw new InvalidDataException("Missing skin mesh set.");
                if (Graph.Field(set,"m_MorphMapHash")is{}morph&&Graph.Integer(morph)!=0 || ProxyReferencePresent(Graph.Field(set,"m_MorphTextureData"))
                    || Graph.Field(set,"m_CorrectionMorphDataHash")is{}correction&&Graph.Integer(correction)!=0
                    || Graph.Field(set,"m_CorrectionMorphDataList")is{}corrections&&(corrections.DataSize<4||BinaryPrimitives.ReadUInt32LittleEndian(Graph.Data(corrections))!=0))
                    throw new NotSupportedException("Prepared skin cannot yet own morph atlas/correction dependencies.");
                var lods=Graph.Items(Graph.Field(set,"m_LODs"));var palettes=Graph.Items(Graph.Field(visual,"m_SkeletonBoneIDsPerLOD"));
                if(lods.Count==0||palettes.Count!=lods.Count)throw new NotSupportedException("Each skin level requires its own explicit native palette.");
                for(int level=0;level<lods.Count;level++)
                {
                    var mesh=Graph.Resolve(Graph.Field(lods[level],"m_Mesh"))??throw new InvalidDataException("Missing skin mesh.");
                    if(ProxyReferencePresent(Graph.Field(mesh,"m_InternalMorphMapTextureData"))||ProxyReferencePresent(Graph.Field(mesh,"m_InternalMorphTextureData")))
                        throw new NotSupportedException("Internal morph texture dependencies remain protected.");
                    var flags=Graph.Data(Graph.Field(mesh,"m_VFFlags")!);
                    if(flags.Length!=12)throw new InvalidDataException("Unknown skin vertex flags.");
                    int streams=0;
                    for(int stream=0;stream<3;stream++)
                    {
                        uint value=BinaryPrimitives.ReadUInt32LittleEndian(flags[(stream*4)..]);
                        if((value&0x200000)!=0)throw new NotSupportedException("Morph vertex layouts remain protected.");
                        if(CompiledMeshDecoder.Layout(value).Skin>=0)streams++;
                    }
                    if(streams!=1)throw new NotSupportedException("Prepared skin requires exactly one native four-weight stream per level.");
                    var decoded=DecodeVisual(frame,visual,decoder,level)??throw new InvalidDataException("Skin geometry could not be decoded.");
                    if(decoded.Skeleton is null||decoded.BoneIndices is null||decoded.BoneWeights is null)throw new InvalidDataException("Unresolved native skin palette.");
                    foreach(uint vertex in decoded.Indices.Distinct())
                    {
                        float sum=0;
                        for(int slot=0;slot<4;slot++)
                        {
                            int at=checked((int)vertex*4+slot);float weight=decoded.BoneWeights[at];sum+=weight;if(weight==0)continue;
                            int bone=decoded.BoneIndices[at];if(bone>=rig.Count||!float.IsFinite(weight)||weight<0)throw new InvalidDataException("Invalid weighted global bone ID.");
                            var point=Vector3.Transform(decoded.Positions[vertex],decoded.Skeleton.InverseBind[bone]);
                            var box=jointBoxes.Slice(4+bone*24,24);var min=CompiledMeshDecoder.Vector(box);var max=CompiledMeshDecoder.Vector(box[12..]);
                            if(point.X<min.X-1e-5f||point.Y<min.Y-1e-5f||point.Z<min.Z-1e-5f||point.X>max.X+1e-5f||point.Y>max.Y+1e-5f||point.Z>max.Z+1e-5f)
                                throw new NotSupportedException("Native joint envelopes do not contain every influenced vertex at every level.");
                        }
                        if(!float.IsFinite(sum)||Math.Abs(sum-1)>1e-6f)throw new NotSupportedException("Native skin weights must form a normalized positive blend.");
                    }
                    skinned=true;
                }
            }
            if(!skinned)throw new NotSupportedException("The source has no verified skinned visual.");
            return true;
        }
        catch(Exception exception)when(exception is InvalidDataException or NotSupportedException or InvalidOperationException or ArgumentException or OverflowException)
        {refusal=exception.Message;return false;}
    }

    private List<GeometryCopy> CopyPreparedSkinAssets(CompiledElement scene, CompiledResource destination)
    {
        var copies=new List<GeometryCopy>();
        try
        {
            var skeleton=Components(scene).Single(component=>component.TypeId==0x0fa73185);
            var reference=Graph.Field(skeleton,"m_ImmutableData")!;
            var immutable=Graph.Resolve(reference)??throw new InvalidDataException("Missing copied skin skeleton.");
            var copy=new GeometryCopy(this,destination:destination);copies.Add(copy);
            var clone=copy.CloneDefinition(immutable,false);copy.Finish();
            ReplaceReference(reference,clone.ObjectId,[]);
            copies.AddRange(CopyGeometry(scene,destination));return copies;
        }
        catch {foreach(var copy in copies.AsEnumerable().Reverse())copy.Detach();throw;}
    }

    private bool HasNativeSkin(CompiledFrameNode frame) => ProxyVisuals(frame).SelectMany(visual =>
        Graph.Items(Graph.Field(Graph.Resolve(Graph.Field(visual,"m_MeshSet")),"m_LODs")))
        .Select(lod=>Graph.Resolve(Graph.Field(lod,"m_Mesh"))).Any(mesh=>Graph.Field(mesh,"m_VFFlags")is{DataSize:12} flags
            && Enumerable.Range(0,3).Any(stream=>(BinaryPrimitives.ReadUInt32LittleEndian(Graph.Data(flags)[(stream*4)..])&0x40)!=0));

    private NativeLodBounds PreparedSkinLocalBounds(CompiledFrameNode frame)
    {
        if(!PreparedSkinContract(frame,out var refusal))throw new NotSupportedException(refusal);
        var rig=VehicleRig(frame)!;var data=Graph.Data(Graph.Field(rig.Immutable,"m_BoneAABBs")!);NativeLodBounds? aggregate=null;
        for(int bone=0;bone<rig.Count;bone++)
        {
            var raw=data.Slice(4+bone*24,24);
            var box=TransformNativeBounds(new(CompiledMeshDecoder.Vector(raw),CompiledMeshDecoder.Vector(raw[12..])),rig.ModelRest(bone));
            aggregate=aggregate is{}old?new(Vector3.Min(old.Min,box.Min),Vector3.Max(old.Max,box.Max)):box;
        }
        return aggregate??throw new InvalidDataException("Empty native joint envelope.");
    }
}
