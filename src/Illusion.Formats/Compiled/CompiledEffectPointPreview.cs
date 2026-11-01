using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Verified stationary point bursts and point/plane timelines in an isolated, unit-scale generation.
/// This does not run effect scheduling, external forces or the game's particle shaders.</summary>
public sealed partial class CompiledEffectPointPreview
{
    private readonly CompiledGraph _graph;
    private readonly CompiledElement _generation;
    private readonly CompiledElement _birth, _size, _color;
    private readonly CompiledElement? _texture, _emissivity, _speed, _acceleration, _rotation;
    private readonly Vector2? _planeSize;
    private readonly Vector3? _boxSize;
    private readonly int _boxDistribution;
    public ulong GenerationId => _generation.ObjectId;
    public ulong MaterialGuid { get; }
    public int BurstCount { get; }
    public string EmitterShape => _boxSize.HasValue ? "box" : _planeSize.HasValue ? "plane" : "point";
    public Vector2 EmitterSize => _planeSize ?? (_boxSize is { } box ? new(box.X,box.Y) : Vector2.Zero);
    public Vector3 EmitterDimensions => _boxSize ?? (_planeSize is { } plane ? new(plane,0) : Vector3.Zero);

    private CompiledEffectPointPreview(CompiledGraph graph, CompiledElement generation, CompiledElement birth,
        CompiledElement size, CompiledElement color, CompiledElement? texture, CompiledElement? emissivity, CompiledElement? speed, CompiledElement? acceleration, ulong material, int count, Vector2? planeSize, CompiledElement? rotation, Vector3? boxSize, int boxDistribution)
    {
        _graph = graph; _generation = generation; _birth = birth; _size = size; _color = color;
        _texture = texture; _emissivity = emissivity; _speed = speed; _acceleration = acceleration; MaterialGuid = material; BurstCount = count; _planeSize = planeSize; _rotation = rotation; _boxSize = boxSize; _boxDistribution = boxDistribution;
    }

    public static bool TryCreate(CompiledGraph graph, CompiledElement generation, out CompiledEffectPointPreview? preview, out string? refusal)
        => TryCreateCore(graph, generation, out preview, out refusal, false);

    internal static bool TryCreateTimeline(CompiledGraph graph, CompiledElement generation, out CompiledEffectPointPreview? preview, out string? refusal)
        => TryCreateCore(graph, generation, out preview, out refusal, true);

    private static bool TryCreateCore(CompiledGraph graph, CompiledElement generation, out CompiledEffectPointPreview? preview, out string? refusal, bool timeline)
    {
        preview = null; refusal = null;
        try
        {
            if (generation.TypeId != 0x46DE2E52) throw new NotSupportedException("Select a native effect generation.");
            var emitter = graph.Resolve(graph.Field(generation, "m_EmitterNode"));
            var scene = graph.Resolve(graph.Field(emitter, "m_RootSceneObject"));
            if (scene is null) throw new NotSupportedException("The generation has no native emitter frame.");
            var components = graph.Items(graph.Field(scene, "m_Components"))
                .Select(n => graph.Resolve(graph.Field(n, "m_CPtr")) ?? graph.Resolve(n)).OfType<CompiledElement>();
            var emitterComponent = components.SingleOrDefault(n => n.TypeId == 0x230314B5)
                ?? throw new NotSupportedException("The frame has no native emitter component.");
            var emitterData = graph.Data(graph.Field(emitterComponent, "m_Data")!);
            // Original S_Data reflection101801DD0: emitter type72, local-space74; native
            // Position::GenerationContinues copies type to its dispatch byte, case0 zeros XYZ.
            if (emitterData.Length != 128 || emitterData[72] != 0 && !(timeline && emitterData[72] is 1 or 4))
                throw new NotSupportedException("Only native point, stationary plane and box timelines are verified for this isolated preview.");
            Vector2? planeSize = null; Vector3? boxSize = null;
            if (emitterData[72] is 1 or 4)
            {
                // S_Data::RegisterAttributes101801DD0: shape variation48, size60, local-space74.
                float width = BinaryPrimitives.ReadSingleLittleEndian(emitterData[60..]);
                float height = BinaryPrimitives.ReadSingleLittleEndian(emitterData[64..]);
                float depth = emitterData[72] == 4 ? BinaryPrimitives.ReadSingleLittleEndian(emitterData[68..]) : 0;
                if (!float.IsFinite(depth) || depth < 0 || !float.IsFinite(width) || !float.IsFinite(height) || width < 0 || height < 0
                    || emitterData[74] == 0 || Vector(emitterData, 48, 3) != Vector3.Zero)
                    throw new NotSupportedException("Plane/box preview requires finite nonnegative dimensions, local space and zero shape-scale variation.");
                if (emitterData[72] == 4) boxSize = new(width,height,depth); else planeSize = new(width, height);
            }
            var data = graph.Data(graph.Field(generation, "m_Data")!);
            if (data.Length != 44 || data[40] != 0 || data[41] != 0)
                throw new NotSupportedException("Ribbon and scale-to-motion generations require their native runtime.");
            ulong material = BinaryPrimitives.ReadUInt64LittleEndian(data);
            var operators = graph.Items(graph.Field(generation, "m_Operators")).Select(graph.Resolve).OfType<CompiledElement>()
                .Where(n => Bool(graph, n, "m_Enabled")).ToArray();
            foreach (var op in operators)
                if (!(timeline && op.TypeId is 0x16EA57CD or 0x2DBB6813) && op.TypeId is not (0x3EBB96BE or 0x3040651E or 0x51491D77 or 0x21AF2099 or 0x35C27036 or 0x45EA6434 or 0x0D9D4DF9))
                    throw new NotSupportedException($"Active {CompiledEffectLibraryData.OperatorName(graph, op)} [{op.TypeId:X8}] is not verified for isolated point bursts.");
            CompiledElement? Find(uint type) => operators.SingleOrDefault(n => n.TypeId == type);
            var birth = Find(0x3EBB96BE) ?? throw new NotSupportedException("A native birth operator is required.");
            var size = Find(0x3040651E) ?? throw new NotSupportedException("A native scalar size operator is required.");
            var color = Find(0x35C27036) ?? throw new NotSupportedException("A native RGB color operator is required.");
            var position = Find(0x51491D77) ?? throw new NotSupportedException("A native position operator is required.");
            if ((planeSize.HasValue || boxSize.HasValue) && (Integer(graph, position, "m_Distribution") is not (0 or 3)
                || Array.IndexOf(operators, birth) > Array.IndexOf(operators, size)
                || Array.IndexOf(operators, size) > Array.IndexOf(operators, position)
                || Find(0x16EA57CD) is not null
                || Find(0x45EA6434) is { } planeSpeed && (Array.IndexOf(operators, position) > Array.IndexOf(operators, planeSpeed)
                    || CompiledEffectCurve.FromParameter(graph, planeSpeed, "m_DivergencyY").Sample(0).X >= 0)))
                throw new NotSupportedException("Plane/box preview requires distribution0/3, ordered birth/size/position, no acceleration and only ensuing verified cone speed.");
            if (Bool(graph, position, "m_AttachToCamera")) throw new NotSupportedException("Camera-attached particle emitters require the runtime camera contract.");
            foreach (string field in timeline ? ["m_AmountVar", "m_SpawnNumber", "m_SpawnNumberVar"] : new[] { "m_Rate", "m_RateVar", "m_AmountVar", "m_LifeVar", "m_SpawnNumber", "m_SpawnNumberVar" })
                RequireZero(graph, birth, field);
            if (Float(graph, birth, "m_StopEmitTime") != 0) throw new NotSupportedException("Stop-emit scheduling is not part of isolated burst preview.");
            var amount = CompiledEffectCurve.FromParameter(graph, birth, "m_Amount");
            float count = amount.Sample(0).X;
            if (!amount.IsConstant || count < 0 || count > 4096) throw new NotSupportedException("A constant native burst amount between0 and4096 is required.");
            if (!timeline) RequireZero(graph, size, "m_SizeVar");
            else
                foreach (var pair in new[] { (birth, "m_Rate"), (birth, "m_RateVar"), (birth, "m_LifeVar"), (size, "m_SizeVar") })
                    CompiledEffectCurve.FromParameter(graph, pair.Item1, pair.Item2).Sample(0);
            var colorVariation = Vector(graph.Data(graph.Field(color, "m_ColorVariation")!), 0, 3);
            if (!Finite(colorVariation)) throw new NotSupportedException("Native RGB variation must be finite.");
            if (!timeline && colorVariation != Vector3.Zero)
                throw new NotSupportedException("Random RGB variation requires its native per-particle seed contract.");
            var speed = Find(0x45EA6434);
            if (!timeline && speed is not null) { RequireZero(graph, speed, "m_Speed"); RequireZero(graph, speed, "m_SpeedVar"); }
            if (timeline && speed is not null)
            {
                foreach (string field in new[] { "m_Speed", "m_SpeedVar", "m_SpeedMultiplier" })
                    CompiledEffectCurve.FromParameter(graph, speed, field).Sample(0);
                if (CompiledEffectCurve.FromParameter(graph, speed, "m_DivergencyY").Sample(0).X >= 0)
                {
                    RequireZero(graph, speed, "m_Speed");
                    RequireZero(graph, speed, "m_SpeedVar");
                }
                if (Integer(graph, speed, "m_SpeedDirection") != 1)
                    throw new NotSupportedException("Continuous point preview requires the verified native direction1 random sequence.");
                if (Vector(graph.Data(graph.Field(speed, "m_EmitterAngles")!), 0, 3) != Vector3.Zero)
                    throw new NotSupportedException("Continuous point preview requires the verified zero emitter-angle basis.");
                foreach (string field in new[] { "m_DivergencyX", "m_DivergencyY" })
                {
                    var curve = CompiledEffectCurve.FromParameter(graph, speed, field);
                    if (!curve.IsConstant || MathF.Abs(curve.Sample(0).X) > 2 * MathF.PI)
                        throw new NotSupportedException("Continuous point preview requires finite constant divergence angles within one revolution.");
                }
            }
            var acceleration = timeline ? Find(0x16EA57CD) : null;
            if (acceleration is not null)
            {
                if (speed is null || Array.IndexOf(operators, acceleration) > Array.IndexOf(operators, speed))
                    throw new NotSupportedException("Native acceleration preview requires an ensuing speed integration operator.");
                foreach (string field in new[] { "m_Acceleration", "m_DampingFactor", "m_AccelerationMultiplier" })
                    CompiledEffectCurve.FromParameter(graph, acceleration, field).Sample(0);
                float damping = CompiledEffectCurve.FromParameter(graph, acceleration, "m_DampingFactor").Sample(0).X;
                if (damping < 0 || damping > 100) throw new NotSupportedException("Native damping preview is verified in the0..100 range.");
                Float(graph, speed, "m_MaxDist");
            }
            var rotation = timeline ? Find(0x2DBB6813) : null;
            if (rotation is not null)
            {
                if (Integer(graph, rotation, "m_OrientationType") != 0 || Float(graph, rotation, "m_OrientationVarianceDeg") != 0
                    || Float(graph, rotation, "m_AlignVelocity") != 0)
                    throw new NotSupportedException("Native rotation preview requires stationary billboard orientation without axis variance or motion alignment.");
                CompiledEffectCurve.FromParameter(graph, rotation, "m_Spin").Sample(0);
                CompiledEffectCurve.FromParameter(graph, rotation, "m_SpinDirection").Sample(0);
                CompiledEffectCurve.FromParameter(graph, rotation, "m_SpinVar").Sample(0);
                foreach (string field in new[] { "m_InitialRotation", "m_InitialRotationVar" })
                    if (MathF.Abs(Float(graph, rotation, field)) > 3600) throw new NotSupportedException("Native billboard initial angles are bounded to ten revolutions.");
                if (new[] { size, position, speed }.OfType<CompiledElement>().Any(op => Array.IndexOf(operators, op) > Array.IndexOf(operators, rotation)))
                    throw new NotSupportedException("Native billboard rotation requires its verified birth ordering after size, position and speed.");
            }
            var texture = Find(0x21AF2099);
            if (texture is not null)
            {
                if (Bool(graph, texture, "m_Animated") || Integer(graph, texture, "m_RandomMirror") != 0
                    || !timeline && (Bool(graph, texture, "m_RandomStartFrame") || Integer(graph, texture, "m_Rows") != 1 || Integer(graph, texture, "m_Cols") != 1))
                    throw new NotSupportedException("Native texture preview supports static nonmirrored atlases; seedless bursts require one cell without random start.");
                if (timeline) ReadFixedAtlas(graph,texture);
            }
            if (timeline && texture is not null && new[] { size, position, speed, rotation }.OfType<CompiledElement>()
                .Any(op => Array.IndexOf(operators, op) > Array.IndexOf(operators, texture)))
                throw new NotSupportedException("Native fixed texture birth requires its verified ordering after size, position and speed.");
            if (texture is not null)
                foreach (string field in new[] { "m_RegionU1", "m_RegionV1", "m_RegionU2", "m_RegionV2" }) Float(graph, texture, field);
            // Verify all consumed curve classes before advertising a generation as supported.
            foreach (var (op, field) in new[] { (birth, "m_Life"), (size, "m_Size"), (color, "m_Color"), (color, "m_Opacity") })
                CompiledEffectCurve.FromParameter(graph, op, field).Sample(0);
            var emissivity = Find(0x0D9D4DF9);
            if (emissivity is not null) CompiledEffectCurve.FromParameter(graph, emissivity, "m_Emissivity").Sample(0);
            preview = new(graph, generation, birth, size, color, texture, emissivity, speed, acceleration, material, (int)count, planeSize, rotation, boxSize, Integer(graph, position, "m_Distribution"));
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or InvalidOperationException or ArgumentException)
        { refusal = ex.Message; return false; }
    }

    public EffectPointSnapshot Sample(float ageSeconds)
    {
        if (!float.IsFinite(ageSeconds) || ageSeconds < 0) throw new ArgumentOutOfRangeException(nameof(ageSeconds));
        // Re-read canonical properties, so unsaved edits and undo are immediately visible.
        if (!TryCreate(_graph, _generation, out var current, out var refusal)) throw new NotSupportedException(refusal);
        return current!.SampleCore(ageSeconds);
    }

    private EffectPointSnapshot SampleCore(float time)
    {
        Vector3 Curve(CompiledElement owner, string field, float age = 0) => CompiledEffectCurve.FromParameter(_graph, owner, field).Sample(0, age);
        float lifetime = Curve(_birth, "m_Life").X;
        // Original Birth::ParticleOnce: nonpositive/tiny lifetimes become effectively infinite,
        // followed by the native1e-4 lower bound. No authored values are changed.
        if (lifetime <= 1e-8f) lifetime = 100000000;
        lifetime = MathF.Max(lifetime, 1e-4f);
        if (time >= lifetime) return new(GenerationId, MaterialGuid, time, []) { EmitterShape = EmitterShape, EmitterSize = EmitterSize };
        float age = NormalizeAge(time, lifetime);
        float half = Curve(_size, "m_Size", age).X * .5f;
        var color = Vector3.Clamp(Curve(_color, "m_Color", age), Vector3.Zero, Vector3.One);
        // Native Color::RenderParticle truncates saturated RGB to the packed8-bit channels.
        color = new((int)(color.X * 255) / 255f, (int)(color.Y * 255) / 255f, (int)(color.Z * 255) / 255f);
        float opacity = Curve(_color, "m_Opacity", age).X;
        Vector4 uv = _texture is null ? new(0, 0, 1, 1) : new(Float(_graph, _texture, "m_RegionU1"), Float(_graph, _texture, "m_RegionV1"),
            Float(_graph, _texture, "m_RegionU2"), Float(_graph, _texture, "m_RegionV2"));
        var emissivity = _emissivity is null ? Vector3.Zero : Curve(_emissivity, "m_Emissivity", age);
        // Native Emissivity::RenderParticle packs its three255-scaled components with OR,
        // without the RGB-color operator's saturation step.
        uint packedEmissivity = unchecked((uint)(int)(emissivity.X * 255) << 16 | (uint)(int)(emissivity.Y * 255) << 8 | (uint)(int)(emissivity.Z * 255));
        emissivity = new((packedEmissivity >> 16 & 255) / 255f, (packedEmissivity >> 8 & 255) / 255f, (packedEmissivity & 255) / 255f);
        var particle = new EffectPointParticle(Vector3.Zero, new(half), new(color, opacity), uv, emissivity, age, lifetime) { AgeSeconds = time, EmissionTime = 0 };
        return new(GenerationId, MaterialGuid, time, Enumerable.Repeat(particle, BurstCount).ToArray()) { EmitterShape = EmitterShape, EmitterSize = EmitterSize };
    }

    private static float NormalizeAge(float age, float life)
    {
        float inverse = 1 / life;
        return MathF.Abs(inverse) > 1e-5f ? inverse * age : age;
    }

    private static void RequireZero(CompiledGraph graph, CompiledElement owner, string field)
    {
        var curve = CompiledEffectCurve.FromParameter(graph, owner, field);
        if (!curve.IsConstant || curve.Sample(0) != Vector3.Zero)
            throw new NotSupportedException($"{field} must be a native constant zero for isolated stationary point bursts.");
    }
    private static bool Bool(CompiledGraph graph, CompiledElement node, string name) => graph.Data(graph.Field(node, name)!)[0] != 0;
    private static int Integer(CompiledGraph graph, CompiledElement node, string name) => BinaryPrimitives.ReadInt32LittleEndian(graph.Data(graph.Field(node, name)!));
    private static float Float(CompiledGraph graph, CompiledElement node, string name)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(graph.Data(graph.Field(node, name)!));
        return float.IsFinite(value) ? value : throw new InvalidDataException("Non-finite native point operator field " + name);
    }
    private static Vector3 Vector(ReadOnlySpan<byte> bytes, int at, int dimensions) => dimensions == 1 ? new(BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]), 0, 0)
        : new(BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[(at + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[(at + 8)..]));
}

public sealed record EffectPointParticle(Vector3 Position, Vector2 HalfSize, Vector4 Color, Vector4 AtlasUv, Vector3 Emissivity, float NormalizedAge, float Lifetime)
{
    public int AtlasFrame { get; init; }
    public float Rotation { get; init; }
    public float AgeSeconds { get; init; }
    public float EmissionTime { get; init; }
    public Vector3 Velocity { get; init; }
}
public sealed record EffectPointSnapshot(ulong GenerationId, ulong MaterialGuid, float Time, IReadOnlyList<EffectPointParticle> Particles)
{
    public string EmitterShape { get; init; } = "point";
    public Vector2 EmitterSize { get; init; }
    public Vector3 EmitterDimensions { get; init; }
    public uint IntegerRandomStateX { get; init; }
    public uint IntegerRandomStateY { get; init; }
    public uint RandomState { get; init; }
    public int Frames { get; init; }
}
