using System.Numerics;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Navigation;

namespace Illusion.Assets.Compiled;

public class Mafia3AiWorldProperties : IPropertySource
{
    protected Mafia3AiObject Node { get; }
    public Mafia3AiWorldProperties(Mafia3AiObject node) => Node = node;
    public string TypeName => Node.TypeName;
    public IReadOnlyList<PropertyGroup> GetPropertyGroups()
    {
        var fields = new List<PropertyDescriptor>
        {
            new() { Id = "ObjectId", Label = "Object ID", Kind = PropertyKind.UInt64Hex, IsReadOnly = true, Get = () => Node.ObjectId },
            new() { Id = "Type", Label = "Type", Kind = PropertyKind.Int, IsReadOnly = true, Get = () => (long)Node.Type },
            new() { Id = "Name", Label = "Name", Kind = PropertyKind.Text, IsReadOnly = true, Get = () => Node.Name },
            new() { Id = "Active", Label = "Active", Kind = PropertyKind.Bool, Get = () => Node.Enabled, Set = value => Node.Enabled = (bool)value! },
            new() { Id = "Connections", Label = "Connections", Kind = PropertyKind.StructList, IsReadOnly = true,
                Get = () => Node.Connections.Select(id => $"{id:X16}").ToArray() }
        };
        if (Node.HasPosition) fields.Add(Vector("Position", () => Node.Position, value => Node.Position = value));
        if (Node.HasDirection) fields.Add(Vector("Direction", () => Node.Direction, value => Node.Direction = value));
        if (Node.HasHalfExtents) fields.Add(Vector("HalfExtents", () => Node.HalfExtents, value => Node.HalfExtents = value));
        for (int i = 0; i < Node.Contour.Count; i++)
        {
            int index = i;
            fields.Add(Vector($"Contour[{i}]", () => Node.Contour[index], value => Node.SetContourPoint(index, value)));
        }
        return [new PropertyGroup { Title = TypeName, IsTypeSpecific = true, Properties = fields }];
    }
    private static PropertyDescriptor Vector(string id, Func<Vector3> get, Action<Vector3> set) => new()
    { Id = id, Label = id, Kind = PropertyKind.Vector3, Get = () => get(), Set = value => set((Vector3)value!) };
}

/// <summary>Canonical translation adapter for an AI point. Facing and extents have explicit properties;
/// arbitrary matrix scaling/rotation is refused instead of silently changing unrelated fields.</summary>
public sealed class Mafia3AiFrameNode(Mafia3AiObject node) : Mafia3AiWorldProperties(node), IFrameNode
{
    public Mafia3AiObject Object => Node;
    public Matrix4x4 LocalTransform
    {
        get => Matrix4x4.CreateTranslation(Node.Position);
        set
        {
            var linear = value; linear.Translation = Vector3.Zero;
            if (linear != Matrix4x4.Identity) throw new EditRefusedException("AI points use translation; edit their Direction and HalfExtents properties for facing and size.");
            Node.Position = value.Translation;
        }
    }
    public Matrix4x4 WorldTransform => LocalTransform;
    public Matrix4x4 ParentWorldTransform => Matrix4x4.Identity;
    public IFrameNode? Parent => null;
    public bool IsOnNameTable => false;
    public int NameTableFlags => 0;
}

public sealed class Mafia3NavDataProperties(Mafia3NavigationMesh mesh, bool faces, int first, int count) : IPropertySource
{
    public string TypeName => faces ? "Navigation face data" : "Navigation edge data";
    public IReadOnlyList<PropertyGroup> GetPropertyGroups()
    {
        int stride = faces ? mesh.FaceDataStride : mesh.EdgeDataStride;
        var fields = new List<PropertyDescriptor>();
        for (int i = first; i < first + count; i++)
            for (int c = 0; c < stride; c++)
            {
                int index = i, channel = c;
                fields.Add(new PropertyDescriptor { Id = $"{(faces ? "Face" : "Edge")}[{i}].Data[{c}]", Label = $"{i} / {c}",
                    Kind = PropertyKind.Int, Min = int.MinValue, Max = int.MaxValue,
                    Get = () => (long)(faces ? mesh.GetFaceData(index, channel) : mesh.GetEdgeData(index, channel)),
                    Set = value => { int data = checked((int)(long)value!); if (faces) mesh.SetFaceData(index, data, channel); else mesh.SetEdgeData(index, data, channel); } });
            }
        return [new PropertyGroup { Title = TypeName, IsTypeSpecific = true, Properties = fields }];
    }
}
