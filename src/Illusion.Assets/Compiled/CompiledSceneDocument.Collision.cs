using System.Buffers.Binary;
using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private readonly Dictionary<CompiledElement, IReadOnlyList<HavokConvexHull?>> _collisionHavok = [];
    private readonly Dictionary<CompiledElement, IReadOnlyList<HavokTriangleMesh?>> _collisionMeshes = [];
    private readonly HashSet<string> _collisionWarnings = [];
    public IReadOnlyCollection<string> CollisionWarnings => _collisionWarnings;

    private IReadOnlyList<SdsFrameNode> BuildCollisionNodes(CompiledFrameNode frame)
    {
        var result = new List<SdsFrameNode>();
        foreach (var component in Components(frame.Element))
        {
            int index = 0;
            foreach (var element in Graph.Field(component, "m_ElementDefs") is { } definitions ? CurrentItems(definitions) : [])
            {
                if (Graph.Field(element, "m_ItemDesc") is not { DataSize: >= 8 } reference ||
                    !Graph.Objects.TryGetValue(Graph.Integer(reference), out var shape) ||
                    Graph.Field(element, "m_Data") is not { DataSize: 88 } placement) continue;
                string? kind = shape.TypeId switch { 0x48430eeb => "Convex", 0x63ee7798 => "Box", 0x4fd32ee0 => "Capsule", 0x7a0b5196 => "Triangle mesh", _ => null };
                if (kind is null) continue;
                string name = $"Collision {++index} ({kind})";
                var source = new CompiledCollisionNode(this, frame, shape, placement, name, reference);
                result.Add(new SdsFrameNode { Name = name, Kind = "Collision volume", Source = source });
            }
        }
        return result;
    }

    /// <summary>Current collision volume surfaces for the collision overlay, independent of ordinary render meshes.</summary>
    public IReadOnlyList<MeshData> CollisionMeshes()
    {
        var result = new List<MeshData>();
        foreach (var source in ActiveCollisionNodes())
            if (CollisionGeometry(source.Shape) is { } geometry)
            {
                result.Add(new MeshData
                {
                    Name = source.TypeName, World = source.WorldTransform,
                    Positions = geometry.Positions, Indices = geometry.Indices,
                    Normals = Enumerable.Repeat(Vector3.UnitZ, geometry.Positions.Length).ToArray(),
                    Parts = [new MeshPart(0, geometry.Indices.Length, null, tint: new Vector4(0.1f, 0.85f, 1, 0.35f), blended: true)]
                });
            }
        result.AddRange(WorldCollisionMeshes());
        return result;
    }

    private IEnumerable<CompiledCollisionNode> ActiveCollisionNodes()
    {
        var visited = new HashSet<CompiledFrameNode>();
        var pending = new Stack<CompiledFrameNode>(Roots.Select(root => root.Source).OfType<CompiledFrameNode>());
        while (pending.TryPop(out var frame))
        {
            if (_deleted.Contains(frame) || !visited.Add(frame)) continue;
            foreach (var row in BuildCollisionNodes(frame))
                if (row.Source is CompiledCollisionNode collision) yield return collision;
            foreach (var child in frame.Children) pending.Push(child);
        }
    }

    private (Vector3[] Positions, uint[] Indices)? CollisionGeometry(CompiledElement shape)
    {
        if (shape.TypeId is 0x48430eeb or 0x7a0b5196)
        {
            if (Graph.Field(shape, "m_DataIndex") is not { } indexField) return null;
            int index = unchecked((int)Graph.Integer(indexField));
            ulong hash = Graph.Field(shape, "m_DataHash") is { } hashField ? Graph.Integer(hashField) : 0;
            var physics = Graph.Nodes.FirstOrDefault(node => node.TypeId == 0xaf6549f7 &&
                Graph.Field(node, "m_Platform") is { } platform && Graph.Integer(platform) == 0 &&
                (hash == 0 ? ReferenceEquals(Graph.Owner(node), Graph.Owner(shape)) :
                    Graph.Field(node, "m_Id") is { } id && Graph.Integer(id) == hash));
            if (physics is null || Graph.Field(physics, "m_Data") is not { } data) return null;
            if (shape.TypeId == 0x7a0b5196)
            {
                if (!_collisionMeshes.TryGetValue(data, out var meshes))
                {
                    try { meshes = HavokMeshData.Read(Graph.Data(data)); }
                    catch (NotSupportedException ex)
                    {
                        _collisionWarnings.Add($"{Path.GetFileName(Graph.Owner(data).Path)}: {ex.Message}");
                        meshes = [];
                    }
                    _collisionMeshes.Add(data, meshes);
                }
                if ((uint)index >= meshes.Count || meshes[index] is not { } mesh) return null;
                return (mesh.Vertices, mesh.Indices);
            }
            if (!_collisionHavok.TryGetValue(data, out var shapes))
            {
                shapes = HavokConvexData.Read(Graph.Data(data));
                _collisionHavok.Add(data, shapes);
            }
            if ((uint)index >= shapes.Count || shapes[index] is not { } hull) return null;
            return (hull.Vertices, hull.Indices);
        }
        if (shape.TypeId == 0x63ee7798 && Graph.Field(shape, "m_Size") is { DataSize: 12 } sizeField)
        {
            Vector3 size = CompiledMeshDecoder.Vector(Graph.Data(sizeField)); // native box half-extents
            if (!Finite(size) || size.X <= 0 || size.Y <= 0 || size.Z <= 0) return null;
            return ([new(-size.X,-size.Y,-size.Z), new(size.X,-size.Y,-size.Z), new(size.X,size.Y,-size.Z), new(-size.X,size.Y,-size.Z),
                new(-size.X,-size.Y,size.Z), new(size.X,-size.Y,size.Z), new(size.X,size.Y,size.Z), new(-size.X,size.Y,size.Z)],
                [0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7]);
        }
        if (shape.TypeId == 0x4fd32ee0 && Graph.Field(shape, "m_Radius") is { DataSize: 4 } radiusField &&
            Graph.Field(shape, "m_Height") is { DataSize: 4 } heightField)
        {
            float radius = CompiledMeshDecoder.Float(Graph.Data(radiusField), 0), height = CompiledMeshDecoder.Float(Graph.Data(heightField), 0);
            if (!float.IsFinite(radius) || !float.IsFinite(height) || radius <= 0 || height < 0) return null;
            // C_RBCapsuleElementDesc::GetLocalAABB: Z endpoints +/-height/2, expanded by radius.
            const int segments = 16, steps = 4;
            var points = new List<Vector3>();
            for (int half = 0; half < 2; half++)
                for (int ring = 0; ring <= steps; ring++)
                {
                    float latitude = (half == 0 ? -MathF.PI / 2 : 0) + ring * MathF.PI / (2 * steps);
                    float z = (half == 0 ? -height / 2 : height / 2) + radius * MathF.Sin(latitude);
                    for (int side = 0; side < segments; side++)
                    {
                        float angle = side * 2 * MathF.PI / segments, radial = radius * MathF.Cos(latitude);
                        points.Add(new Vector3(radial * MathF.Cos(angle), radial * MathF.Sin(angle), z));
                    }
                }
            var indices = new List<uint>();
            for (int ring = 0; ring < 2 * (steps + 1) - 1; ring++)
                for (int side = 0; side < segments; side++)
                {
                    uint a = (uint)(ring * segments + side), b = (uint)(ring * segments + (side + 1) % segments);
                    uint c = a + segments, d = b + segments;
                    indices.AddRange([a,b,d, a,d,c]);
                }
            return (points.ToArray(), indices.ToArray());
        }
        return null;
    }

    private static bool Finite(Vector3 vector) => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);

    private int ActiveCollisionReferences(CompiledElement shape)
        => ActiveCollisionNodes().Count(source => ReferenceEquals(source.Shape, shape));

    private sealed class CompiledCollisionNode(CompiledSceneDocument document, CompiledFrameNode frame,
        CompiledElement shape, CompiledElement placement, string name, CompiledElement reference) : IFrameNode, IPropertySource
    {
        public CompiledElement Shape => document.Graph.Objects.GetValueOrDefault(document.Graph.Integer(reference)) ?? shape;
        public CompiledElement Reference => reference;
        public CompiledFrameNode Frame => frame;
        public string TypeName => name;
        public IFrameNode Parent => frame;
        // ActivateElement copies m_CollMatrix directly into CreateElementInternal; it is already
        // in model bind space. ParentBoneId is attachment metadata, not another bind transform.
        public Matrix4x4 ParentWorldTransform => frame.WorldTransform;
        public Matrix4x4 WorldTransform => LocalTransform * ParentWorldTransform;
        public Matrix4x4 LocalTransform
        {
            get => DescriptorTransform * ReadMatrix(document.Graph.Data(placement).Slice(24, 48));
            set
            {
                float[] fields = [value.M11,value.M12,value.M13,value.M21,value.M22,value.M23,value.M31,value.M32,value.M33,value.M41,value.M42,value.M43];
                if (fields.Any(number => !float.IsFinite(number))) throw new ArgumentOutOfRangeException(nameof(value));
                var bytes = document.Graph.Data(placement).ToArray();
                if (!Matrix4x4.Invert(DescriptorTransform, out var inverse))
                    throw new InvalidOperationException("The collision descriptor transform is singular.");
                WriteMatrix(inverse * value).CopyTo(bytes, 24); document.Graph.Owner(placement).Replace(placement, bytes);
            }
        }
        private Matrix4x4 DescriptorTransform => document.Graph.Field(Shape, "m_LocalMatrix") is { DataSize: 48 } matrix
            ? ReadMatrix(document.Graph.Data(matrix)) : Matrix4x4.Identity;
        public bool IsOnNameTable => false;
        public int NameTableFlags => 0;
        public IReadOnlyList<PropertyGroup> GetPropertyGroups()
        {
            var groups = new CompiledProperties(document.Graph, Shape, name).GetPropertyGroups();
            int references = document.ActiveCollisionReferences(Shape);
            bool shared = references > 1;
            var result = groups.Select(group => new PropertyGroup
            {
                Title = shared ? $"{group.Title} (shape shared by {references} volumes)" : group.Title,
                IsTypeSpecific = group.IsTypeSpecific,
                Properties = group.Properties.Select(property =>
                {
                    bool metadata = Shape.TypeId == 0x7a0b5196 || property.Id is "m_NumVertices" or "m_DataIndex" or "m_DataHash" or "m_Hash" or "m_Type" || property.Id.StartsWith("m_ConvexDataAABB", StringComparison.Ordinal);
                    bool cookedBox = Shape.TypeId == 0x63ee7798 && property.Id == "m_Size";
                    return new PropertyDescriptor
                    {
                        Id = property.Id, Label = property.Label, Kind = property.Kind,
                        Get = () => new CompiledProperties(document.Graph, Shape, name).GetPropertyGroups()
                            .SelectMany(g => g.Properties).First(p => p.Id == property.Id).Get(),
                        IsReadOnly = property.IsReadOnly || metadata, Min = property.Min, Max = property.Max,
                        Tooltip = cookedBox ? "Resizing creates independent cooked Havok box data and updates its physics dependency. Undo restores the original shape."
                            : shared ? "Editing creates an independent descriptor for this volume; other placements retain their shape."
                            : metadata ? "Compiled shape metadata; editing requires rebuilding the cooked shape." : property.Tooltip,
                        ApplyEdit = property.Set is null || metadata ? null : cookedBox
                            ? input => document.ResizeCookedBox(this, (Vector3)input!)
                            : input => document.EditCollisionField(this, property.Id, input),
                        Set = property.Set is null || metadata ? null : cookedBox
                            ? input => document.ResizeCookedBox(this, (Vector3)input!)
                            : input => document.EditCollisionField(this, property.Id, input)

                    };
                }).ToArray()
            }).ToList();
            result.Add(new PropertyGroup
            {
                Title = "Collision attachment", IsTypeSpecific = true,
                Properties = [new PropertyDescriptor
                {
                    Id = "m_ParentBoneId", Label = "Parent bone ID", Kind = PropertyKind.Int, IsReadOnly = true,
                    Get = () => (long)BinaryPrimitives.ReadUInt16LittleEndian(document.Graph.Data(placement)[14..]),
                    Tooltip = "Native attachment metadata. The collision matrix already contains its model bind placement."
                }]
            });
            return result;
        }
    }
}
