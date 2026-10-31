using System.Buffers.Binary;
using System.Numerics;
using Illusion.Assets.Collisions;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Adds an independent physical volume under an existing frame. Applied on success.</summary>
    public IFrameDuplication? AddCollision(IFrameNode target, string kind, Vector3 size, Matrix4x4 local,
        IReadOnlyList<Vector3>? points, ushort material, out string? refusal, ushort parentBone = ushort.MaxValue, IReadOnlyList<uint>? triangleIndices = null)
    {
        refusal = null;
        if (target is not CompiledFrameNode frame || !_frames.ContainsValue(frame) || _deleted.Contains(frame))
        { refusal = "The destination must be an active compiled frame."; return null; }
        if (parentBone != ushort.MaxValue)
        {
            var rig = frame.VehicleRig;
            if (rig is null)
            { refusal = "An unrigged collision owner requires the frame attachment sentinel 65535."; return null; }
            if (parentBone >= rig.Count)
            { refusal = "The collision parent bone is outside the owner's skeleton."; return null; }
        }
        uint type = kind.ToLowerInvariant() switch { "box" => 0x63ee7798, "convex" => 0x48430eeb, "capsule" => 0x4fd32ee0, "triangle" => 0x7a0b5196, _ => 0 };
        if (type == 0) { refusal = "Collision kind must be box, convex, capsule or triangle."; return null; }
        if (!Finite(size) || size.X <= 0 || (type == 0x4fd32ee0 ? size.Z < 0 : size.Y <= 0 || size.Z <= 0) || !Matrix4x4.Invert(local, out _))
        { refusal = "Collision dimensions must be finite and positive, and placement must be invertible."; return null; }
        // Read-only dependencies may stay cached after refusal, just as they do for normal scene loading.
        // Capture mutation state only after loading them so rollback cannot deactivate their definitions.
        try { LoadCollisionTemplates(type); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException or IOException)
        { refusal = ex.Message; return null; }
        using var transaction = new ImportTransaction(this, null);
        var copies = new GeometryCopy(this, destination: Graph.Owner(frame.Element));
        IEditAction? componentEdit = null;
        try
        {
            var shapeTemplate = Graph.Nodes.FirstOrDefault(n => n.TypeId == type)
                ?? throw new NotSupportedException("No compatible collision shape template is available.");
            var componentTemplate = Graph.Nodes.First(n => n.TypeId == 0x2b7b4775 && Graph.Field(n, "m_ElementDefs") is { } elements && CurrentItems(elements).Count > 0);
            var entryTemplate = CurrentItems(Graph.Field(componentTemplate, "m_ElementDefs")!).First();
            var helperTemplate = Graph.Nodes.Select(n => Graph.Field(n, "m_BoneCollisionHelperMap")).OfType<CompiledElement>()
                .SelectMany(CurrentItems).FirstOrDefault() ?? throw new NotSupportedException("A native bone collision allocation template is required.");
            var component = Components(frame.Element).FirstOrDefault(n => n.TypeId == 0x2b7b4775);
            if (component is null)
            {
                var attached = AddCollisionComponent(frame, out var why) ?? throw new NotSupportedException(why);
                component = attached.Component; componentEdit = attached.Edit;
            }
            var owner = Graph.Owner(component);
            var shape = copies.CloneDefinition(shapeTemplate, followDependencies: false);
            CompiledElement? physics = null;
            HavokConvexHull? hull = null;
            if (type != 0x4fd32ee0)
            {
                if (type == 0x48430eeb)
                {
                    if (points is null || points.Count is < 4 or > 100000 || points.Any(p => !Finite(p))) throw new ArgumentException("Convex collision needs 4–100000 finite points.");
                    var built = ConvexHull.Build(points, simplify: false) ?? throw new ArgumentException("Convex collision points must span a volume.");
                    hull = new HavokConvexHull(built.Vertices, built.Triangles.Select(i => (uint)i).ToArray(), 0);
                }
                else if (type == 0x63ee7798)
                {
                    Vector3[] vertices = [new(-size.X,-size.Y,-size.Z),new(size.X,-size.Y,-size.Z),new(size.X,size.Y,-size.Z),new(-size.X,size.Y,-size.Z),
                        new(-size.X,-size.Y,size.Z),new(size.X,-size.Y,size.Z),new(size.X,size.Y,size.Z),new(-size.X,size.Y,size.Z)];
                    hull = new HavokConvexHull(vertices,[0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7],0);
                }
                var original = CollisionPhysics(shapeTemplate) ?? Graph.Nodes.FirstOrDefault(n => n.TypeId == 0xaf6549f7 && Graph.Field(n,"m_Platform") is {} platform && Graph.Integer(platform)==0)
                    ?? throw new NotSupportedException("No PC Havok data template is loaded.");
                int variant = type == 0x7a0b5196 ? checked((int)Graph.Integer(Graph.Field(shapeTemplate,"m_DataIndex")!)) : 0;
                byte[] cooked;
                if (type == 0x7a0b5196)
                {
                    if (points is null || triangleIndices is null) throw new ArgumentException("Triangle collision needs local vertices and triangle indices.");
                    var tableObject = Graph.Field(shape,"m_MatTable") ?? throw new NotSupportedException("The native triangle material table is unavailable.");
                    var table = Descendants(tableObject).SingleOrDefault(node=>node.Event==4&&node.TypeId==0x10f6f534)
                        ?? throw new NotSupportedException("The native triangle material table has an unknown payload layout.");
                    byte[] nativeTable = Graph.Data(table).ToArray();
                    if (nativeTable.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(nativeTable) == 0
                        || nativeTable.Length != 4L + 28L * BinaryPrimitives.ReadUInt32LittleEndian(nativeTable))
                        throw new NotSupportedException("A verified native contact material record is required.");
                    var templateGeometry = HavokMeshData.ReadVariant(Graph.Data(Graph.Field(original,"m_Data")!),variant)
                        ?? throw new NotSupportedException("The triangle template is unavailable.");
                    var paletteZeroTags = templateGeometry.Materials.Where(tag=>(tag&0x1ff)==0).ToArray();
                    if (paletteZeroTags.Length == 0) throw new NotSupportedException("Triangle creation requires a verified palette-zero contact template.");
                    cooked = HavokMeshTopologyWriter.Replace(Graph.Data(Graph.Field(original,"m_Data")!), variant, points, triangleIndices,
                        Enumerable.Repeat(paletteZeroTags[0],triangleIndices.Count/3).ToArray());
                    byte[] single = nativeTable[..32];
                    BinaryPrimitives.WriteUInt32LittleEndian(single,1);
                    BinaryPrimitives.WriteUInt32LittleEndian(single.AsSpan(4),0);
                    BinaryPrimitives.WriteUInt32LittleEndian(single.AsSpan(8),checked((uint)triangleIndices.Count/3));
                    BinaryPrimitives.WriteUInt32LittleEndian(single.AsSpan(24),material);
                    Graph.Owner(table).ReplaceVariable(table,single);
                    if (Graph.Field(shape,"m_ScaleVector") is {} scales) SetItems(scales,[]);
                }
                else cooked = HavokConvexWriter.Write(Graph.Data(Graph.Field(original,"m_Data")!),[hull!]);
                physics = copies.CloneDefinition(original,followDependencies:false);
                var data = Graph.Field(physics,"m_Data")!; Graph.Owner(data).ReplaceVariable(data,cooked);
                SetNumber(physics,"m_DataSize",(uint)cooked.Length); SetNumber(physics,"m_Id",physics.ObjectId);
                SetNumber(shape,"m_DataHash",physics.ObjectId); SetNumber(shape,"m_DataIndex",(uint)variant);
            }
            else { SetNumber(shape,"m_DataHash",0); SetNumber(shape,"m_DataIndex",uint.MaxValue); }
            SetNumber(shape,"m_MaterialId",material); SetNumber(shape,"m_Hash",shape.ObjectId);
            SetBytes(shape,"m_LocalMatrix",WriteMatrix(Matrix4x4.Identity));
            if(type==0x63ee7798)SetBytes(shape,"m_Size",VectorBytes(size));
            else if(type==0x4fd32ee0){SetBytes(shape,"m_Radius",BitConverter.GetBytes(size.X));SetBytes(shape,"m_Height",BitConverter.GetBytes(size.Z));}
            else if(type==0x48430eeb)
            {
                SetNumber(shape,"m_NumVertices",(uint)hull!.Vertices.Length);
                SetBytes(shape,"m_ConvexDataAABB",VectorBytes(hull.Vertices.Aggregate(Vector3.Min)).Concat(VectorBytes(hull.Vertices.Aggregate(Vector3.Max))).ToArray());
            }
            copies.Finish();
            var entry=owner.CopySubtree(Graph.Owner(entryTemplate),entryTemplate,new Dictionary<ulong,ulong>());
            Graph.RegisterSubtree(owner,entry);
            var reference=Graph.Field(entry,"m_ItemDesc")!;byte[] pointer=Graph.Data(reference).ToArray();BinaryPrimitives.WriteUInt64LittleEndian(pointer,shape.ObjectId);owner.Replace(reference,pointer);
            var materialRef=Graph.Field(entry,"m_MaterialDef")!;byte[] materialBytes=Graph.Data(materialRef).ToArray();Array.Clear(materialBytes,0,8);owner.Replace(materialRef,materialBytes);
            var placement=Graph.Field(entry,"m_Data")!;byte[] placementBytes=new byte[88];
            BinaryPrimitives.WriteUInt16LittleEndian(placementBytes.AsSpan(14),parentBone);
            WriteMatrix(local).CopyTo(placementBytes,24); BinaryPrimitives.WriteUInt64LittleEndian(placementBytes.AsSpan(72),shape.ObjectId);
            ulong ownerHash=Graph.Field(frame.Element,"m_HashName") is {} hash ? Graph.Integer(hash) : 0;
            BinaryPrimitives.WriteUInt64LittleEndian(placementBytes.AsSpan(80),ownerHash);owner.Replace(placement,placementBytes);
            var changes=new List<(CompiledElement Container,CompiledElement[] Before,CompiledElement[] After)>();
            void Change(CompiledElement container,IEnumerable<CompiledElement> after)
            {var before=CurrentItems(container).ToArray();var next=after.ToArray();changes.Add((container,before,next));SetItems(container,next);}
            var definitions=Graph.Field(component,"m_ElementDefs")!;Change(definitions,CurrentItems(definitions).Append(entry));
            var helperMap=Graph.Field(component,"m_BoneCollisionHelperMap")!;
            var helpers=CurrentItems(helperMap).ToArray();var previous=helpers.FirstOrDefault(n=>Graph.Integer(Graph.Field(n,"m_BoneId")!)==parentBone);
            var helper=owner.CopySubtree(Graph.Owner(previous??helperTemplate),previous??helperTemplate,new Dictionary<ulong,ulong>());
            Graph.RegisterSubtree(owner,helper);SetNumber(helper,"m_BoneId",parentBone);
            var elements=Graph.Field(helper,"m_Elements")!;
            var nullTemplate=CurrentItems(Graph.Field(helperTemplate,"m_Elements")!).First();
            int count=CurrentItems(definitions).Count(n=>BinaryPrimitives.ReadUInt16LittleEndian(Graph.Data(Graph.Field(n,"m_Data")!)[14..])==parentBone);
            var slots=new List<CompiledElement>();
            for(int i=0;i<count;i++)
            {
                var slot=owner.CopySubtree(Graph.Owner(nullTemplate),nullTemplate,new Dictionary<ulong,ulong>());Graph.RegisterSubtree(owner,slot);
                foreach(var value in Descendants(slot).Where(n=>n.Event==4))owner.Replace(value,new byte[value.DataSize]);slots.Add(slot);
            }
            Change(elements,slots);Change(helperMap,helpers.Where(n=>!ReferenceEquals(n,previous)).Append(helper));
            var added=new List<CompiledElement>{entry,helper};added.AddRange(slots);
            if(physics is not null)
            {
                var template=Graph.Nodes.Select(n=>Graph.Field(n,"m_DataDependencies")).OfType<CompiledElement>().SelectMany(CurrentItems)
                    .First(n=>Graph.Field(n,"m_DataContainer") is {Event:4,DataSize:12});
                var dependency=owner.CopySubtree(Graph.Owner(template),template,new Dictionary<ulong,ulong>());Graph.RegisterSubtree(owner,dependency);
                var field=Graph.Field(dependency,"m_DataContainer")!;byte[] bytes=Graph.Data(field).ToArray();BinaryPrimitives.WriteUInt64LittleEndian(bytes,physics.ObjectId);owner.Replace(field,bytes);
                SetNumber(dependency,"m_Platform",0);var dependencies=Graph.Field(component,"m_DataDependencies")!;Change(dependencies,CurrentItems(dependencies).Append(dependency));added.Add(dependency);
            }
            var schemas=added.SelectMany(n=>Graph.ValidateCopySubtree(owner,n)).GroupBy(p=>p.Key).ToDictionary(g=>g.Key,g=>g.First().Value);
            foreach(var(typeId,bytes)in schemas){_schemaCopies[(owner,typeId)]=_schemaCopies.GetValueOrDefault((owner,typeId))+1;Plan(owner).AdditionalSchemas[typeId]=bytes;}
            Plan(owner).SchemaStreamIndex=owner.StreamIndex(owner.Roots.Last(n=>n.Event==2));Plan(owner).AllowNativeAllocatorFallback=true;
            string label=type switch {0x63ee7798=>"Box",0x48430eeb=>"Convex",0x7a0b5196=>"Triangle mesh",_=>"Capsule"};
            var source=new CompiledCollisionNode(this,frame,shape,placement,$"Collision {CurrentItems(definitions).Count} ({label})",reference);
            var root=new SdsFrameNode{Name=source.TypeName,Kind="Collision volume",Source=source};
            var result=new CollisionPlacementEdit(this,root,copies,componentEdit,owner,added,changes,schemas);
            transaction.Commit();_collisionHavok.Clear();return result;
        }
        catch(Exception ex)when(ex is ArgumentException or InvalidDataException or NotSupportedException or IOException)
        {copies.Detach();componentEdit?.Undo();refusal=ex.Message;return null;}
    }

    private static byte[] VectorBytes(Vector3 value)
    {byte[] bytes=new byte[12];BinaryPrimitives.WriteSingleLittleEndian(bytes,value.X);BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4),value.Y);BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8),value.Z);return bytes;}
    private void SetBytes(CompiledElement node,string name,byte[] bytes)
    {var field=Graph.Field(node,name)??throw new InvalidDataException("Missing collision field "+name);Graph.Owner(field).Replace(field,bytes);}
    private void SetNumber(CompiledElement node,string name,ulong number)
    {
        var field=Graph.Field(node,name)??throw new InvalidDataException("Missing collision field "+name);byte[] bytes=Graph.Data(field).ToArray();
        switch(bytes.Length){case 1:bytes[0]=checked((byte)number);break;case 2:BinaryPrimitives.WriteUInt16LittleEndian(bytes,checked((ushort)number));break;case 4:BinaryPrimitives.WriteUInt32LittleEndian(bytes,checked((uint)number));break;case 8:BinaryPrimitives.WriteUInt64LittleEndian(bytes,number);break;default:throw new InvalidDataException("Invalid collision integer.");}
        Graph.Owner(field).Replace(field,bytes);
    }

    private void LoadCollisionTemplates(uint shapeType)
    {
        bool Ready()=>Graph.Nodes.Any(n=>n.TypeId==shapeType)&&Graph.Nodes.Any(n=>n.TypeId==0x2b7b4775)&&Graph.Nodes.Any(n=>n.TypeId==0xaf6549f7&&Graph.Field(n,"m_Platform")is{}p&&Graph.Integer(p)==0);
        if(Ready())return;
        var archive=new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder,"city_crash",SourceArchive.Name.StartsWith("si_",StringComparison.OrdinalIgnoreCase)?"si_city_crash.sds":"bc_city_crash.sds"));
        if(!archive.Exists)throw new NotSupportedException("The city's shared collision templates are unavailable.");
        foreach(string path in SdsManifest.Load(SdsMeshLoader.EnsureExtracted(archive)).GetFiles("Generic"))
        {
            if(Graph.Resources.Any(r=>r.Path.Equals(path,StringComparison.OrdinalIgnoreCase)))continue;
            var resource=CompiledResource.Load(path);
            if(!resource.Elements.Any(n=>n.Event==2&&(n.TypeId==shapeType||n.TypeId==0x2b7b4775||n.TypeId==0xaf6549f7)))continue;
            if(resource.Elements.Any(n=>n.Event==2&&n.ObjectId!=0&&Graph.Objects.ContainsKey(n.ObjectId)))continue;
            Graph.AddResource(resource);_archives.Add(resource,archive);if(Ready())return;
        }
        throw new NotSupportedException("No compatible PC collision templates were found.");
    }

    private sealed class CollisionPlacementEdit(CompiledSceneDocument document,SdsFrameNode root,GeometryCopy copies,IEditAction? component,
        CompiledResource resource,List<CompiledElement> added,List<(CompiledElement Container,CompiledElement[] Before,CompiledElement[] After)> changes,IReadOnlyDictionary<uint,byte[]> schemas):IFrameDuplication
    {
        private bool _attached=true;
        public ISceneHierarchyNode Root=>root;
        public void Detach()
        {
            if(!_attached)return;
            foreach(var change in changes.AsEnumerable().Reverse())document.SetItems(change.Container,change.Before);
            foreach(var node in added)document.Graph.SetSubtreeActive(resource,node,false);
            foreach(uint type in schemas.Keys)if(--document._schemaCopies[(resource,type)]==0)document.Plan(resource).AdditionalSchemas.Remove(type);
            copies.Detach();component?.Undo();document._collisionHavok.Clear();_attached=false;
        }
        public void Reattach()
        {
            if(_attached)return;component?.Redo();copies.Reattach();
            foreach(var node in added)document.Graph.SetSubtreeActive(resource,node,true);
            foreach(var(type,bytes)in schemas){document._schemaCopies[(resource,type)]=document._schemaCopies.GetValueOrDefault((resource,type))+1;document.Plan(resource).AdditionalSchemas[type]=bytes;}
            foreach(var change in changes)document.SetItems(change.Container,change.After);
            document._collisionHavok.Clear();_attached=true;
        }
    }
}
