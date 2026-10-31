using System.Buffers.Binary;
using System.Numerics;
using Illusion.Assets.Collisions;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    public MeshData? CollisionMesh(IFrameNode volume)
    {
        if (volume is not CompiledCollisionNode source || !_frames.ContainsValue(source.Frame)
            || CollisionGeometry(source.Shape) is not { } geometry) return null;
        return new MeshData { Name = source.TypeName, Positions = geometry.Positions, Indices = geometry.Indices,
            Normals = Enumerable.Repeat(Vector3.UnitZ, geometry.Positions.Length).ToArray(),
            World = source.WorldTransform };
    }

    /// <summary>Rebuilds one collision volume as an independent native convex asset. Already applied on success.</summary>
    public IEditAction? RecookCollision(IFrameNode volume, IReadOnlyList<Vector3> positions, out string? refusal)
    {
        refusal = null;
        if (volume is not CompiledCollisionNode source || !_frames.ContainsValue(source.Frame))
        { refusal = "Select a collision volume from this compiled document."; return null; }
        if (source.Shape.TypeId is not (0x48430eeb or 0x7a0b5196))
        { refusal = "Select a convex or triangle-mesh collision volume."; return null; }
        if (positions.Count > 100000 || positions.Any(p => !Finite(p)))
        { refusal = "Collision input needs at most 100000 finite points."; return null; }
        try
        {
            if (source.Shape.TypeId == 0x7a0b5196)
            {
                if (Graph.Field(source.Shape, "m_ScaleVector") is { } scales && CurrentItems(scales).Any())
                    throw new NotSupportedException("This triangle descriptor has separately cooked scale variants; editing only its base would leave stale scaled collision.");
                var physics = CollisionPhysics(source.Shape) ?? throw new NotSupportedException("The original PC physics data is unavailable.");
                var data = Graph.Field(physics, "m_Data") ?? throw new InvalidDataException("The physics container has no data.");
                int index = checked((int)Graph.Integer(Graph.Field(source.Shape, "m_DataIndex")!));
                byte[] original = Graph.Data(data).ToArray();
                byte[] cooked = HavokMeshWriter.ReplacePositions(original, index, positions);
                return RecookShape(source, null, triangleData: cooked);
            }
            var hull = ConvexHull.Build(positions, simplify: false)
                ?? throw new ArgumentException("The collision vertices must span a three-dimensional volume.");
            return RecookShape(source, new HavokConvexHull(hull.Vertices, hull.Triangles.Select(i => (uint)i).ToArray(), 0));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException or IOException)
        { refusal = ex.Message; return null; }
    }

    private CompiledElement? CollisionPhysics(CompiledElement shape)
    {
        ulong hash = Graph.Field(shape, "m_DataHash") is { } value ? Graph.Integer(value) : 0;
        return Graph.Nodes.FirstOrDefault(node => node.TypeId == 0xaf6549f7
            && Graph.Field(node, "m_Platform") is { } platform && Graph.Integer(platform) == 0
            && (hash == 0 ? ReferenceEquals(Graph.Owner(node), Graph.Owner(shape))
                : Graph.Field(node, "m_Id") is { } id && Graph.Integer(id) == hash));
    }

    private IEditAction ResizeCookedBox(CompiledCollisionNode source, Vector3 size)
    {
        if (!Finite(size) || size.X <= 0 || size.Y <= 0 || size.Z <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Box half-extents must be finite and positive.");
        Vector3[] points = [new(-size.X,-size.Y,-size.Z), new(size.X,-size.Y,-size.Z), new(size.X,size.Y,-size.Z), new(-size.X,size.Y,-size.Z),
            new(-size.X,-size.Y,size.Z), new(size.X,-size.Y,size.Z), new(size.X,size.Y,size.Z), new(-size.X,size.Y,size.Z)];
        uint[] indices = [0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7];
        return RecookShape(source, new HavokConvexHull(points, indices, 0), size);
    }

    private IEditAction RecookShape(CompiledCollisionNode source, HavokConvexHull? hull, Vector3? boxSize = null, byte[]? triangleData = null)
    {
        var physics = CollisionPhysics(source.Shape) ?? throw new NotSupportedException("The original PC physics data container is unavailable.");
        var nativeData = Graph.Field(physics, "m_Data") ?? throw new InvalidDataException("The physics container has no data.");
        byte[] cooked = triangleData ?? HavokConvexWriter.Write(Graph.Data(nativeData), [hull!]);
        var component = Components(source.Frame.Element).FirstOrDefault(c => Graph.Field(c, "m_ElementDefs") is { } definitions
            && CurrentItems(definitions).Any(item => ReferenceEquals(Graph.Field(item, "m_ItemDesc"), source.Reference)));
        var dependencies = Graph.Field(component, "m_DataDependencies")
            ?? throw new NotSupportedException("The collision component has no physics dependency list.");
        var dependencyTemplate = CurrentItems(dependencies).FirstOrDefault(item =>
            Graph.Field(item, "m_DataContainer") is { Event: 4, DataSize: 12 });
        if (dependencyTemplate is null) throw new NotSupportedException("The collision component has no physics dependency template.");
        using var transaction = new ImportTransaction(this, null);
        var copies = new GeometryCopy(this, destination: Graph.Owner(source.Frame.Element));
        try
        {
            var shape = copies.CloneDefinition(source.Shape, followDependencies: false);
            var data = copies.CloneDefinition(physics, followDependencies: false);
            copies.Finish();
            void Number(CompiledElement node, string name, ulong number)
            {
                var field = Graph.Field(node, name) ?? throw new InvalidDataException("Missing collision field " + name);
                byte[] bytes = Graph.Data(field).ToArray();
                switch (bytes.Length)
                {
                    case 2: BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)number)); break;
                    case 4: BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)number)); break;
                    case 8: BinaryPrimitives.WriteUInt64LittleEndian(bytes, number); break;
                    default: throw new InvalidDataException("Invalid collision integer size.");
                }
                Graph.Owner(field).Replace(field, bytes);
            }
            var payload = Graph.Field(data, "m_Data")!;
            Graph.Owner(payload).ReplaceVariable(payload, cooked);
            Number(data, "m_DataSize", (uint)cooked.Length);
            Number(data, "m_Id", data.ObjectId);
            Number(shape, "m_DataHash", data.ObjectId);
            if (triangleData is null) Number(shape, "m_DataIndex", 0);
            Number(shape, "m_Hash", shape.ObjectId);
            if (shape.TypeId == 0x48430eeb && hull is not null)
            {
                Number(shape, "m_NumVertices", (uint)hull.Vertices.Length);
                var bounds = Graph.Field(shape, "m_ConvexDataAABB")!;
                byte[] bytes = new byte[24];
                Vector3 min = hull.Vertices.Aggregate(Vector3.Min), max = hull.Vertices.Aggregate(Vector3.Max);
                float[] values = [min.X,min.Y,min.Z,max.X,max.Y,max.Z];
                for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4 * i), values[i]);
                Graph.Owner(bounds).Replace(bounds, bytes);
            }
            if (boxSize is { } size)
            {
                var field = Graph.Field(shape, "m_Size")!; byte[] bytes = new byte[12];
                BinaryPrimitives.WriteSingleLittleEndian(bytes, size.X); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), size.Y);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), size.Z); Graph.Owner(field).Replace(field, bytes);
            }
            var owner = Graph.Owner(dependencies);
            var dependency = owner.CloneSubtree(dependencyTemplate, new Dictionary<ulong, ulong> { [physics.ObjectId] = data.ObjectId });
            Graph.RegisterSubtree(owner, dependency);
            var containerRef = Graph.Field(dependency, "m_DataContainer")!;
            byte[] containerBytes = Graph.Data(containerRef).ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(containerBytes, data.ObjectId);
            owner.Replace(containerRef, containerBytes);
            byte[] before = Graph.Data(source.Reference).ToArray(), after = (byte[])before.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(after, shape.ObjectId);
            Graph.Owner(source.Reference).Replace(source.Reference, after);
            SetItems(dependencies, CurrentItems(dependencies).Append(dependency).ToArray());
            Plan(owner).AllowNativeAllocatorFallback = true;
            _collisionHavok.Clear(); _collisionMeshes.Clear();
            var result = new CookedCollisionEdit(this, copies, source.Reference, before, after, dependencies, dependency);
            transaction.Commit();
            return result;
        }
        catch { copies.Detach(); throw; }
    }

    private sealed class CookedCollisionEdit(CompiledSceneDocument document, GeometryCopy copies,
        CompiledElement reference, byte[] before, byte[] after, CompiledElement dependencies, CompiledElement dependency) : IEditAction
    {
        private bool _applied = true;
        public void Undo()
        {
            if (!_applied) return;
            document.Graph.Owner(reference).Replace(reference, before);
            document.SetItems(dependencies, document.CurrentItems(dependencies).Where(item => !ReferenceEquals(item, dependency)).ToArray());
            document.Graph.SetSubtreeActive(document.Graph.Owner(dependency), dependency, false);
            copies.Detach(); document._collisionHavok.Clear(); document._collisionMeshes.Clear(); _applied = false;
        }
        public void Redo()
        {
            if (_applied) return;
            copies.Reattach();
            document.Graph.SetSubtreeActive(document.Graph.Owner(dependency), dependency, true);
            document.Graph.Owner(reference).Replace(reference, after);
            document.SetItems(dependencies, document.CurrentItems(dependencies).Append(dependency).ToArray());
            document._collisionHavok.Clear(); document._collisionMeshes.Clear(); _applied = true;
        }
    }
}
