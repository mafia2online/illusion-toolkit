using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Verified stationary point-burst operators in an isolated, unit-scale generation.
/// This does not run effect scheduling, external forces or the game's particle shaders.</summary>
public sealed partial class CompiledEffectPointPreview
{
    private readonly CompiledGraph _graph;
    private readonly CompiledElement _generation;
    private readonly CompiledElement _birth, _size, _color;
    private readonly CompiledElement? _texture, _emissivity, _speed, _acceleration;
    public ulong GenerationId => _generation.ObjectId;
    public ulong MaterialGuid { get; }
    public int BurstCount { get; }

    private CompiledEffectPointPreview(CompiledGraph graph, CompiledElement generation, CompiledElement birth,
        CompiledElement size, CompiledElement color, CompiledElement? texture, CompiledElement? emissivity, CompiledElement? speed, CompiledElement? acceleration, ulong material, int count)
    {
        _graph = graph; _generation = generation; _birth = birth; _size = size; _color = color;
        _texture = texture; _emissivity = emissivity; _speed = speed; _acceleration = acceleration; MaterialGuid = material; BurstCount = count;
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
            if (emitterData.Length != 128 || emitterData[72] != 0)
                throw new NotSupportedException("Only native point emitters are verified for this isolated preview.");
            var data = graph.Data(graph.Field(generation, "m_Data")!);
            if (data.Length != 44 || data[40] != 0 || data[41] != 0)
                throw new NotSupportedException("Ribbon and scale-to-motion generations require their native runtime.");
            ulong material = BinaryPrimitives.ReadUInt64LittleEndian(data);
            var operators = graph.Items(graph.Field(generation, "m_Operators")).Select(graph.Resolve).OfType<CompiledElement>()
                .Where(n => Bool(graph, n, "m_Enabled")).ToArray();
            foreach (var op in operators)
                if (!(timeline && op.TypeId == 0x16EA57CD) && op.TypeId is not (0x3EBB96BE or 0x3040651E or 0x51491D77 or 0x21AF2099 or 0x35C27036 or 0x45EA6434 or 0x0D9D4DF9))
                    throw new NotSupportedException($"Active {CompiledEffectLibraryData.OperatorName(graph, op)} [{op.TypeId:X8}] is not verified for isolated point bursts.");
            CompiledElement? Find(uint type) => operators.SingleOrDefault(n => n.TypeId == type);
            var birth = Find(0x3EBB96BE) ?? throw new NotSupportedException("A native birth operator is required.");
            var size = Find(0x3040651E) ?? throw new NotSupportedException("A native scalar size operator is required.");
            var color = Find(0x35C27036) ?? throw new NotSupportedException("A native RGB color operator is required.");
            var position = Find(0x51491D77) ?? throw new NotSupportedException("A native position operator is required.");
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
            if (Vector(graph.Data(graph.Field(color, "m_ColorVariation")!), 0, 3) != Vector3.Zero)
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
            var texture = Find(0x21AF2099);
            if (texture is not null && (Bool(graph, texture, "m_Animated") || Bool(graph, texture, "m_RandomStartFrame")
                || Integer(graph, texture, "m_RandomMirror") != 0 || Integer(graph, texture, "m_Rows") != 1 || Integer(graph, texture, "m_Cols") != 1))
                throw new NotSupportedException("Only a fixed native one-cell texture region is verified for this preview.");
            if (texture is not null)
                foreach (string field in new[] { "m_RegionU1", "m_RegionV1", "m_RegionU2", "m_RegionV2" }) Float(graph, texture, field);
            // Verify all consumed curve classes before advertising a generation as supported.
            foreach (var (op, field) in new[] { (birth, "m_Life"), (size, "m_Size"), (color, "m_Color"), (color, "m_Opacity") })
                CompiledEffectCurve.FromParameter(graph, op, field).Sample(0);
            var emissivity = Find(0x0D9D4DF9);
            if (emissivity is not null) CompiledEffectCurve.FromParameter(graph, emissivity, "m_Emissivity").Sample(0);
            preview = new(graph, generation, birth, size, color, texture, emissivity, speed, acceleration, material, (int)count);
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
        if (time >= lifetime) return new(GenerationId, MaterialGuid, time, []);
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
        return new(GenerationId, MaterialGuid, time, Enumerable.Repeat(particle, BurstCount).ToArray());
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
    public float AgeSeconds { get; init; }
    public float EmissionTime { get; init; }
    public Vector3 Velocity { get; init; }
}
public sealed record EffectPointSnapshot(ulong GenerationId, ulong MaterialGuid, float Time, IReadOnlyList<EffectPointParticle> Particles)
{
    public uint RandomState { get; init; }
    public int Frames { get; init; }
}
