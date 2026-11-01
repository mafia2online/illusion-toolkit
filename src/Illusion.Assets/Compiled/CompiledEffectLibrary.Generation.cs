using System.Buffers.Binary;
using Illusion.Domain.Properties;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledEffectLibrary
{
    // Original C_Generation::S_Data reflection1017A4D20. Opaque layout is patched in place.
    private PropertyGroup? GenerationDataProperties(CompiledElement generation)
    {
        if (Graph.Field(generation,"m_Data") is not { Event: 4, DataSize: 44, TypeId: 0x7526396D } data) return null;
        return new PropertyGroup
        {
            Title = "Native generation scheduling / rendering", IsTypeSpecific = true,
            Properties = [Field("DelayTime",20),Field("PreloadTime",24),Field("MotionBlurTime",16),
                Field("ScaleToMotionFactor",36),Field("SortLayer",28,PropertyKind.Int),Field("MotionBlurSamples",12,PropertyKind.Int),
                Field("IsRibbon",40,PropertyKind.Bool),Field("ScaleToMotionEnabled",41,PropertyKind.Bool)]
        };
        PropertyDescriptor Field(string name,int offset,PropertyKind kind = PropertyKind.Float) => new()
        {
            Id = $"object:{generation.ObjectId:X16}.nativeData.{name}",Label = name,Kind = kind,
            IsReadOnly = name == "IsRibbon",
            Min = name is "SortLayer" or "ScaleToMotionFactor" ? int.MinValue : 0,Max = int.MaxValue,
            Tooltip = name == "IsRibbon" ? "Native ribbon allocation propagation is not yet verified; this structural flag remains read-only."
                : "Native game data. The isolated generation preview uses its own local clock and does not apply effect scheduling, motion blur, ribbons or scale-to-motion.",
            Get = () => kind switch
            {
                PropertyKind.Bool => (object)(Graph.Data(data)[offset] != 0),
                PropertyKind.Int => (long)BinaryPrimitives.ReadInt32LittleEndian(Graph.Data(data)[offset..]),
                _ => BinaryPrimitives.ReadSingleLittleEndian(Graph.Data(data)[offset..])
            },
            Set = name == "IsRibbon" ? null : input =>
            {
                var bytes = Graph.Data(data).ToArray();
                if (kind == PropertyKind.Bool) bytes[offset] = (bool)input! ? (byte)1 : (byte)0;
                else if (kind == PropertyKind.Int)
                {
                    long value = (long)input!;
                    if (name != "SortLayer" && value < 0) throw new ArgumentOutOfRangeException(nameof(input));
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset),checked((int)value));
                }
                else
                {
                    float value = (float)input!;
                    if (!float.IsFinite(value) || name != "ScaleToMotionFactor" && value < 0) throw new ArgumentOutOfRangeException(nameof(input),"Use finite native values; times must be nonnegative.");
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset),value);
                }
                Graph.Owner(data).Replace(data,bytes);
            }
        };
    }
}
