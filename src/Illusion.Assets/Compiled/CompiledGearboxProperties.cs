using Illusion.Domain.Properties;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Properties named from the shipped engine's S_CarGearboxTableItem registration.</summary>
public sealed class CompiledGearboxProperties(CompiledGearboxTable table, int row) : IPropertySource
{
    public string TypeName => "Gearbox " + table.Description(row);
    public IReadOnlyList<PropertyGroup> GetPropertyGroups()
    {
        var fields = new List<PropertyDescriptor>
        {
            new() { Id = "m_ID", Label = "ID", Kind = PropertyKind.Int, IsReadOnly = true, Get = () => (long)table.ReadUInt32(row, 0) },
            new() { Id = "m_Automatic", Label = "Automatic", Kind = PropertyKind.Bool, Get = () => table.ReadAutomatic(row), Set = v => table.WriteAutomatic(row, (bool)v!) },
            Integer("m_GearCount", "Gear count", 40), Integer("m_GearReverseCount", "Reverse gear count", 44)
        };
        for (int gear = 0; gear < 7; gear++)
        {
            fields.Add(Float($"m_GearRatio{gear}", $"Gear {gear} ratio", 48 + gear * 12));
            fields.Add(Float($"m_RotationsGearUp{gear}", $"Gear {gear} shift up", 52 + gear * 12));
            fields.Add(Float($"m_RotationsGearDown{gear}", $"Gear {gear} shift down", 56 + gear * 12));
        }
        fields.Add(Float("m_MinClutchGlobal", "Minimum clutch", 132));
        fields.Add(Float("m_MinClutchAngleCoeffGlobal", "Minimum clutch angle coefficient", 136));
        fields.Add(Float("m_ShiftDelayMin", "Minimum shift delay", 140));
        fields.Add(Float("m_ShiftDelayMax", "Maximum shift delay", 144));
        return [new PropertyGroup { Title = TypeName, IsTypeSpecific = true, Properties = fields }];
    }
    private PropertyDescriptor Integer(string id, string label, int offset) => new()
    {
        Id = id, Label = label, Kind = PropertyKind.Int, Min = 0, Max = 7,
        Get = () => (long)table.ReadUInt32(row, offset), Set = v => table.WriteUInt32(row, offset, checked((uint)(long)v!))
    };
    private PropertyDescriptor Float(string id, string label, int offset) => new()
    {
        Id = id, Label = label, Kind = PropertyKind.Float,
        Get = () => table.ReadFloat(row, offset), Set = v => table.WriteFloat(row, offset, (float)v!)
    };
}
