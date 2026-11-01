using System.Buffers.Binary;
using System.Numerics;
using Illusion.Domain.Properties;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledEffectLibrary
{
    // Native S_Data::RegisterAttributes101801DD0 and Position::GenerationContinues.
    private PropertyGroup? EmitterProperties(CompiledElement generation)
    {
        var emitter = Graph.Resolve(Graph.Field(generation, "m_EmitterNode"));
        var scene = Graph.Resolve(Graph.Field(emitter, "m_RootSceneObject"));
        var components = Graph.Items(Graph.Field(scene, "m_Components"))
            .Select(n => Graph.Resolve(Graph.Field(n, "m_CPtr")) ?? Graph.Resolve(n)).OfType<CompiledElement>()
            .Where(n => n.TypeId == 0x230314B5).ToArray();
        if (components.Length != 1 || Graph.Field(components[0], "m_Data") is not { Event: 4, DataSize: 128 } data) return null;
        string prefix = $"emitter:{components[0].ObjectId:X16}";
        return new PropertyGroup
        {
            Title = "Native emitter (shared scene component)", IsTypeSpecific = true,
            Properties = [Field("Shape", PropertyKind.Int, 72), Field("Dimensions", PropertyKind.Vector3, 60),
                Field("ShapeScaleVariation", PropertyKind.Vector3, 48), Field("LocalSpace", PropertyKind.Bool, 74)]
        };
        PropertyDescriptor Field(string name, PropertyKind kind, int offset) => new()
        {
            Id = $"{prefix}.{name}", Label = name, Kind = kind, Min = 0, Max = 5,
            Tooltip = "Edits the native component shared by every generation referencing this emitter. Shape: 0 point, 1 plane, 2 disc, 3 sphere, 4 box, 5 user. Isolated preview supports only its verified subset.",
            Get = () => kind switch
            {
                PropertyKind.Int => (object)(long)Graph.Data(data)[offset],
                PropertyKind.Bool => Graph.Data(data)[offset] != 0,
                _ => ReadVector(offset)
            },
            Set = value =>
            {
                var bytes = Graph.Data(data).ToArray();
                if (kind == PropertyKind.Int)
                {
                    long shape = (long)value!;
                    if (shape is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(value), "Native emitter shape must be 0–5.");
                    bytes[offset] = (byte)shape;
                }
                else if (kind == PropertyKind.Bool) bytes[offset] = (bool)value! ? (byte)1 : (byte)0;
                else
                {
                    var vector = (Vector3)value!;
                    if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) || !float.IsFinite(vector.Z)
                        || name == "Dimensions" && (vector.X < 0 || vector.Y < 0 || vector.Z < 0))
                        throw new ArgumentOutOfRangeException(nameof(value), "Emitter vectors must be finite; dimensions must be nonnegative.");
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), vector.X);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 4), vector.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 8), vector.Z);
                }
                Graph.Owner(data).Replace(data, bytes);
            }
        };
        Vector3 ReadVector(int at) => new(BinaryPrimitives.ReadSingleLittleEndian(Graph.Data(data)[at..]),
            BinaryPrimitives.ReadSingleLittleEndian(Graph.Data(data)[(at + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(Graph.Data(data)[(at + 8)..]));
    }
}
