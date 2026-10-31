using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Illusion.Domain.Properties;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Verified fixed-width fields in the Mafia III C_Car entity initialization block.</summary>
public sealed class CompiledVehicleProperties : IPropertySource
{
    public string TypeName => "Vehicle dynamics";
    private readonly CompiledGraph _graph;
    private readonly CompiledElement _blob;
    private readonly IReadOnlyList<PropertyDescriptor> _properties;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    private CompiledVehicleProperties(CompiledGraph graph, CompiledElement blob, int axles)
    {
        _graph = graph;
        _blob = blob;
        List<PropertyDescriptor> properties =
        [
            Float("Mass", "Mass", 0x20),
            Vector("CenterOfMass", "Center of mass", 0x24),
            Float("Power", "Engine power", 0x54),
            Float("RotationsMin", "Minimum engine RPM", 0x5c),
            Float("RotationsMax", "Maximum engine RPM", 0x60),
            Float("RotationsMaxMax", "Engine RPM limit", 0x64),
            Float("TorqueStart", "Starting torque", 0x68),
            Float("TorqueMax", "Maximum torque", 0x6c),
            Float("TorqueEnd", "Ending torque", 0x70),
            Float("RotationsTorqueLow", "Low torque RPM (% of limit)", 0x74),
            Float("RotationsTorqueHigh", "High torque RPM (% of limit)", 0x78),
            Float("MotorBrakeTorque", "Engine braking torque", 0x7c),
            Float("MotorInertia", "Engine inertia", 0x80),
            Float("FuelConsumption", "Fuel consumption", 0x90),
            Float("FuelTankCapacity", "Fuel tank capacity", 0x94),
            Integer("FinalGearCount", "Final gear count", 0x98, 0, 2),
            Float("FinalGearRatio[0]", "Final gear ratio 0", 0x9c),
            Float("FinalGearRatio[1]", "Final gear ratio 1", 0xa0),
            Boolean("Automatic", "Default automatic gearbox", 0xa4),
            Integer("GearCount", "Default gear count", 0xb0, 0, 7),
            Integer("GearReverseCount", "Default reverse gear count", 0xb4, 0, 7),
            Float("ShiftDelayMin", "Default minimum shift delay", 0xa8),
            Float("ShiftDelayMax", "Default maximum shift delay", 0xac),
            Float("MinClutchGlobal", "Default minimum clutch", 0x160),
            Float("MinClutchAngleCoeffGlobal", "Default minimum clutch angle coefficient", 0x164),
            Float("BrakeTorque", "Brake torque", 0x778),
            Float("BrakeReaction", "Brake reaction", 0x77c),
            Float("HandBrakeTorque", "Handbrake torque", 0x780),
            Float("BrakeReactionLow", "Low brake reaction", 0x784),
            Float("BrakeReactionHigh", "High brake reaction", 0x788)
        ];
        for (int gear = 0; gear < 7; gear++)
        {
            properties.Add(Float($"GearRatio[{gear}]", $"Default gear {gear} ratio", 0xb8 + gear * 4));
            properties.Add(Float($"RotationsGearUp[{gear}]", $"Default gear {gear} shift up", 0xd4 + gear * 4));
            properties.Add(Float($"RotationsGearDown[{gear}]", $"Default gear {gear} shift down", 0xf0 + gear * 4));
            properties.Add(Float($"GearMinClutch[{gear}]", $"Default gear {gear} minimum clutch", 0x128 + gear * 4));
            properties.Add(Float($"GearMinClutchAngleCoeff[{gear}]", $"Default gear {gear} minimum clutch angle coefficient", 0x144 + gear * 4));
        }
        for (int axle = 0; axle < axles; axle++)
        {
            string id = $"Axle[{axle}].", label = $"Axle {axle + 1} ";
            int displacement = axle * 4;
            properties.Add(Wheel(axle));
            properties.Add(Float(id + "SpringK", label + "spring stiffness", 1212 + displacement));
            properties.Add(Float(id + "SpringLength", label + "spring length (cm)", 1252 + displacement));
            properties.Add(Float(id + "SpringPreLoad", label + "spring preload (cm)", 1292 + displacement));
            properties.Add(Float(id + "DamperBound", label + "compression damping", 1332 + displacement));
            properties.Add(Float(id + "DamperRebound", label + "rebound damping", 1372 + displacement));
            properties.Add(Integer(id + "DifferentialIndex", label + "differential index", 1412 + displacement, 0, 9));
            properties.Add(Float(id + "AntiRollBarTorque", label + "anti-roll bar torque", 1452 + displacement));
            properties.Add(Float(id + "ToeIn", label + "toe-in (degrees)", 1532 + displacement));
            properties.Add(Float(id + "KPInclination", label + "kingpin inclination (degrees)", 1572 + displacement));
            properties.Add(Float(id + "KPCaster", label + "caster (degrees)", 1612 + displacement));
            properties.Add(Float(id + "Camber", label + "camber (degrees)", 1652 + displacement));
            properties.Add(Float(id + "BrakeCoeffLeft", label + "left brake coefficient (%)", 1772 + displacement));
            properties.Add(Float(id + "BrakeCoeffRight", label + "right brake coefficient (%)", 1812 + displacement));
            properties.Add(Boolean(id + "Steering", label + "steering enabled", 1892 + axle));
            properties.Add(Boolean(id + "HandBrake", label + "handbrake enabled", 1902 + axle));
        }
        _properties = properties;
    }

    public static CompiledVehicleProperties? TryCreate(CompiledGraph graph, CompiledElement entity)
    {
        if (entity.TypeId != 0xdeb11995) return null;
        if (graph.Field(entity, "m_EntityType") is not { } type || graph.Integer(type) != 10) return null;
        var blob = graph.Field(graph.Field(entity, "m_InitProps"), "m_Array");
        if (blob is not { Event: 4, DataSize: 3960 } ||
            BinaryPrimitives.ReadUInt32LittleEndian(graph.Data(blob)) != 3956) return null;
        var prefab = graph.Resolve(graph.Field(entity, "m_BuiltPrefabsCPtr"));
        int axles = graph.Items(graph.Field(graph.Field(prefab, "m_SAxleWheelInit"), "m_InitAxle")).Count;
        if (axles is < 1 or > 10) return null;
        // Reject malformed names rather than interpreting arbitrary entity bytes as text.
        for (int i = 0; i < axles; i++)
        {
            var field = graph.Data(blob).Slice(0x240 + 32 * i, 32);
            int end = field.IndexOf((byte)0);
            if (end < 0) return null;
            try { Utf8.GetString(field[..end]); }
            catch (DecoderFallbackException) { return null; }
        }
        return new CompiledVehicleProperties(graph, blob, axles);
    }

    public IReadOnlyList<PropertyGroup> GetPropertyGroups() => _properties.GroupBy(GroupName)
        .Select(group => new PropertyGroup { Title = group.Key, IsTypeSpecific = true, Properties = group.ToArray() }).ToArray();

    private static string GroupName(PropertyDescriptor property)
    {
        string name = property.Id[8..]; // Vehicle.
        if (name.StartsWith("Axle[", StringComparison.Ordinal) || name.StartsWith("WheelModel[", StringComparison.Ordinal))
        {
            int start = name.IndexOf('[') + 1, end = name.IndexOf(']');
            return "Axle " + (int.Parse(name.AsSpan(start, end - start), System.Globalization.CultureInfo.InvariantCulture) + 1);
        }
        if (name.StartsWith("Fuel", StringComparison.Ordinal)) return "Fuel";
        if (name.StartsWith("Brake", StringComparison.Ordinal) || name.StartsWith("HandBrake", StringComparison.Ordinal)) return "Brakes";
        if (name.StartsWith("Gear", StringComparison.Ordinal) || name.StartsWith("FinalGear", StringComparison.Ordinal) ||
            name.StartsWith("RotationsGear", StringComparison.Ordinal) || name.StartsWith("MinClutch", StringComparison.Ordinal) ||
            name.StartsWith("ShiftDelay", StringComparison.Ordinal) || name == "Automatic") return "Default gearbox";
        return "Engine and mass";
    }

    private PropertyDescriptor Vector(string name, string label, int payloadOffset)
    {
        int offset = payloadOffset + 4;
        return new PropertyDescriptor
        {
            Id = "Vehicle." + name, Label = label, Kind = PropertyKind.Vector3,
            Get = () => CompiledMeshDecoder.Vector(_graph.Data(_blob)[offset..]),
            Set = input =>
            {
                var vector = (Vector3)input!;
                if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) || !float.IsFinite(vector.Z))
                    throw new ArgumentOutOfRangeException(nameof(input));
                var data = _graph.Data(_blob).ToArray();
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), vector.X);
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 4), vector.Y);
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 8), vector.Z);
                _graph.Owner(_blob).Replace(_blob, data);
            },
            Tooltip = "Native mass settings center of mass, used by C_Vehicle::UpdateWeight and UpdateMassAndCOM."
        };
    }

    private PropertyDescriptor Integer(string name, string label, int payloadOffset, long min, long max)
    {
        int offset = payloadOffset + 4;
        return new PropertyDescriptor
        {
            Id = "Vehicle." + name, Label = label, Kind = PropertyKind.Int, Min = min, Max = max,
            Get = () => (long)BinaryPrimitives.ReadInt32LittleEndian(_graph.Data(_blob)[offset..]),
            Set = input =>
            {
                long number = (long)input!;
                if (number < min || number > max) throw new ArgumentOutOfRangeException(nameof(input));
                var data = _graph.Data(_blob).ToArray();
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), checked((int)number));
                _graph.Owner(_blob).Replace(_blob, data);
            },
            Tooltip = name.StartsWith("Axle[", StringComparison.Ordinal)
                ? "Native C_Car::ApplySettingsTable input to C_Vehicle::SetAxleSettings."
                : "Default input to C_Car::ApplySettingsTable; a selected tuning gearbox can override this value."
        };
    }

    private PropertyDescriptor Boolean(string name, string label, int payloadOffset)
    {
        int offset = payloadOffset + 4;
        byte original = _graph.Data(_blob)[offset];
        return new PropertyDescriptor
        {
            Id = "Vehicle." + name, Label = label, Kind = PropertyKind.Bool,
            Get = () => _graph.Data(_blob)[offset] != 0,
            Set = input =>
            {
                bool enabled = (bool)input!;
                var data = _graph.Data(_blob).ToArray();
                data[offset] = enabled == (original != 0) ? original : enabled ? (byte)1 : (byte)0;
                _graph.Owner(_blob).Replace(_blob, data);
            }
        };
    }

    private PropertyDescriptor Float(string name, string label, int payloadOffset)
    {
        int offset = payloadOffset + 4; // reflected array length precedes the native payload
        return new PropertyDescriptor
        {
            Id = "Vehicle." + name, Label = label, Kind = PropertyKind.Float,
            Get = () => BinaryPrimitives.ReadSingleLittleEndian(_graph.Data(_blob)[offset..]),
            Set = input =>
            {
                float number = (float)input!;
                if (!float.IsFinite(number)) throw new ArgumentOutOfRangeException(nameof(input));
                var data = _graph.Data(_blob).ToArray();
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), number);
                _graph.Owner(_blob).Replace(_blob, data);
            },
            Tooltip = name.StartsWith("GearRatio", StringComparison.Ordinal) || name.StartsWith("RotationsGear", StringComparison.Ordinal) || name.StartsWith("ShiftDelay", StringComparison.Ordinal)
                ? "Default gearbox setting; a selected tuning gearbox table overrides this value."
                : "Native C_Car::ApplySettingsTable input; tuning modifiers can scale the runtime value."
        };
    }

    private PropertyDescriptor Wheel(int axle)
    {
        int offset = 0x240 + 32 * axle;
        // Text undo carries strings. Retain the original padding for every observed value,
        // including nonzero bytes following the NUL, so undo restores all 32 bytes.
        var snapshots = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string Read()
        {
            var bytes = _graph.Data(_blob).Slice(offset, 32);
            string text = Utf8.GetString(bytes[..bytes.IndexOf((byte)0)]);
            snapshots.TryAdd(text, bytes.ToArray());
            return text;
        }
        Read();
        void Write(object? input)
        {
            if (input is not string text || text.Contains('\0'))
                throw new ArgumentException("A wheel model must be text without NUL characters.", nameof(input));
            byte[] encoded = Utf8.GetBytes(text);
            if (encoded.Length > 31)
                throw new ArgumentOutOfRangeException(nameof(input), "A wheel model may contain at most 31 UTF-8 bytes.");
            Read();
            var data = _graph.Data(_blob).ToArray();
            var destination = data.AsSpan(offset, 32);
            if (snapshots.TryGetValue(text, out var original)) original.CopyTo(destination);
            else { destination.Clear(); encoded.CopyTo(destination); snapshots.Add(text, destination.ToArray()); }
            _graph.Owner(_blob).Replace(_blob, data);
        }
        return new PropertyDescriptor
        {
            Id = $"Vehicle.WheelModel[{axle}]", Label = $"Axle {axle + 1} wheel model",
            Kind = PropertyKind.Text, Get = () => Read(), Set = Write,
            Tooltip = "Model name in the fixed 32-byte vehicle initialization field (maximum 31 UTF-8 bytes). Reopen the vehicle after saving to rebuild its wheel visuals."
        };
    }
}
