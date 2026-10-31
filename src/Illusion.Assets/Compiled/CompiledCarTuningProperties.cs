using Illusion.Domain.Properties;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Native S_CarTuningItemTableItem attributes consumed by the vehicle tuning manager.</summary>
public sealed class CompiledCarTuningProperties(CompiledCarTuningTable table, int row) : IPropertySource
{
    public string TypeName => "Tuning " + table.Description(row);
    private static readonly (string Name, int Offset)[] Floats =
    [
        ("EngineTorqueMinRot", 60), ("EngineTorque", 64), ("EngineTorqueMaxRot", 68),
        ("EnginePowerAndTorqueRotations", 72), ("EngineMaxRotations", 76), ("EngineBrakeTorque", 80),
        ("EngineInertia", 84), ("EngineEfficiency", 88), ("EngineTurboMinimalRotations", 92),
        ("EngineTurboOptimalRotations", 96), ("EngineTurboTurnOnTime", 100), ("EngineTurboTorqueIncrease", 104),
        ("FinalGear", 112), ("ViscousClutch", 116), ("FrontSpringLength", 120),
        ("FrontSpringStiffness", 124), ("FrontDamperStiffness", 128), ("FrontSwayBar", 132),
        ("FrontTyrePressure", 136), ("RearSpringLength", 140), ("RearSpringStiffness", 144),
        ("RearDamperStiffness", 148), ("RearSwayBar", 152), ("RearTyrePressure", 156),
        ("BreakTorque", 160), ("BreakEfficiency", 164), ("BreakReaction", 168),
        ("FrontSpoilerCoeff", 172), ("RearSpoilerCoeff", 176), ("Aerodynamic", 180),
        ("VehicleMass", 192), ("EngineResistance", 196), ("VehicleBodyBoneStiffness", 200),
        ("CrashSpeedChange", 204), ("CarCrewCrashSpeedChange", 208)
    ];
    public IReadOnlyList<PropertyGroup> GetPropertyGroups()
    {
        var fields = new List<PropertyDescriptor>
        {
            Integer("ID", 0, true), Integer("SlotId", 4, true), Integer("Flags", 40),
            new() { Id = "m_Gearbox", Label = "Gearbox ID (-1 = default)", Kind = PropertyKind.Int,
                Min = -1, Max = ushort.MaxValue, Get = () => (long)table.ReadInt32(row, 108),
                Set = v => table.WriteGearbox(row, checked((int)(long)v!)),
                Tooltip = "ID of the CarGearboxesTable row applied by this tuning item." },
            Integer("VehicleBodyMaterialID", 184), Integer("VehicleWindowMaterialID", 188)
        };
        foreach (var (name, offset) in Floats)
            fields.Add(new PropertyDescriptor
            {
                Id = "m_" + name, Label = name, Kind = PropertyKind.Float,
                Get = () => table.ReadFloat(row, offset), Set = v => table.WriteFloat(row, offset, (float)v!),
                Tooltip = "Native tuning modifier. The tuning manager combines the selected slot items before applying vehicle settings."
            });
        return [new PropertyGroup { Title = TypeName, IsTypeSpecific = true, Properties = fields }];
    }
    private PropertyDescriptor Integer(string name, int offset, bool readOnly = false) => new()
    {
        Id = "m_" + name, Label = name, Kind = PropertyKind.Int, IsReadOnly = readOnly,
        Min = 0, Max = uint.MaxValue, Get = () => (long)table.ReadUInt32(row, offset),
        Set = readOnly ? null : v => table.WriteUInt32(row, offset, checked((uint)(long)v!))
    };
}
