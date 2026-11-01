using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Native ordered fixed-step replay of one stationary point generation. External effect
/// scheduling, emitter motion, child spawning and particle lighting are outside this isolated clock.</summary>
public sealed class CompiledEffectPointTimeline
{
    private readonly CompiledGraph _graph;
    private readonly CompiledElement _generation;
    public ulong GenerationId => _generation.ObjectId;
    public ulong MaterialGuid { get; }
    private CompiledEffectPointTimeline(CompiledGraph graph, CompiledElement generation, ulong material)
    { _graph = graph; _generation = generation; MaterialGuid = material; }

    public static bool TryCreate(CompiledGraph graph, CompiledElement generation, out CompiledEffectPointTimeline? preview, out string? refusal)
    {
        preview = null;
        if (!CompiledEffectPointPreview.TryCreateTimeline(graph, generation, out var config, out refusal)) return false;
        preview = new(graph, generation, config!.MaterialGuid);
        return true;
    }

    public EffectPointSnapshot SampleFrames(int frames, float deltaSeconds = 1 / 60f, uint seed = 1)
    {
        Validate(frames, deltaSeconds);
        var session = CreateSession(deltaSeconds, seed);
        session.AdvanceToFrame(frames, 36000, maxWorkMilliseconds: int.MaxValue);
        return session.Snapshot;
    }

    /// <summary>Replays complete fixed steps followed by one remaining partial step.
    /// Time and RNG state report the actual resulting native float clock.</summary>
    public EffectPointSnapshot Sample(float time, float deltaSeconds = 1 / 60f, uint seed = 1)
    {
        if (!float.IsFinite(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
        Validate(0, deltaSeconds);
        float quotient = time / deltaSeconds;
        if (!float.IsFinite(quotient) || quotient > 36000) throw new ArgumentOutOfRangeException(nameof(time), "Preview is limited to36000 steps.");
        int frames = (int)quotient;
        float remainder = time - frames * deltaSeconds;
        return Configuration().ReplayTimeline(frames, deltaSeconds, remainder, seed);
    }

    public CompiledEffectPointSession CreateSession(float deltaSeconds = 1 / 60f, uint seed = 1)
    {
        Validate(0, deltaSeconds);
        return new(Configuration, _graph, deltaSeconds, seed);
    }

    private CompiledEffectPointPreview Configuration() => CompiledEffectPointPreview.TryCreateTimeline(_graph, _generation, out var config, out var reason)
        ? config! : throw new NotSupportedException(reason);
    private static void Validate(int frames, float delta)
    {
        if (frames < 0 || frames > 36000) throw new ArgumentOutOfRangeException(nameof(frames));
        if (!float.IsFinite(delta) || delta <= 0 || delta > 1) throw new ArgumentOutOfRangeException(nameof(delta), "Give a positive finite step of at most one second.");
    }
}

public sealed partial class CompiledEffectPointPreview
{
    private sealed class TimelineParticle
    {
        public float Age, Life, NormalizedAge, Emission, HalfSize, SizeFactor, SpeedVariation, PreviousSpeed;
        public Vector3 Position, Velocity, Direction;
        public bool Outside;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    // Original1019D43E0: native float log/exp polynomial, preserving every operation's order.
    private static float NativePointDamping(float damping, float dt)
    {
        float x = (100 - damping) / 100 - 1;
        float x2 = x * x, x3 = x * x2;
        float power = x - x2 * .5f;
        power += x3 / 3;
        float t = (dt * 25) * power;
        t *= .25f;
        float t2 = t * t, t3 = t * t2;
        float exp = t + 1;
        exp += t2 * .5f;
        exp += t3 / 6;
        float e2 = exp * exp, e3 = exp * e2;
        return exp * e3;
    }

    internal PointTimelineState CreateTimelineState(uint seed)
    {
        var curves = new Dictionary<CompiledElement, CompiledEffectCurve>();
        CompiledEffectCurve Curve(CompiledElement owner, string field)
        {
            var source = _graph.Resolve(_graph.Field(_graph.Field(owner, field), "m_Interpolator"))
                ?? throw new InvalidDataException("Missing point timeline interpolator.");
            if (!curves.TryGetValue(source, out var curve)) curves[source] = curve = CompiledEffectCurve.Read(_graph, source);
            return curve;
        }
        Vector3 Value(CompiledElement owner, string field, float emission = 0, float age = 0) => Curve(owner, field).Sample(emission, age);
        float Scalar(CompiledElement owner, string field, float emission = 0, float age = 0) => Value(owner, field, emission, age).X;
        uint state = seed;
        var particleRandom = new PointRandom(seed);
        uint Next()
        {
            state = unchecked(state * 214013 + 2531011);
            return state >> 16 & 32767;
        }
        float Rand() => Next() / 16383.5f - 1;
        float Unit() => Next() / 32767f;
        var particles = new List<TimelineParticle>();
        float clock = 0, fraction = 0;
        int amountCount = 0;
        bool uniform = Bool(_graph, _birth, "m_UniformSpawn");
        float divergence = _speed is null ? 0 : Scalar(_speed, "m_DivergencyX");
        bool cone = _speed is not null && Scalar(_speed, "m_DivergencyY") < 0;
        int steps = 0;
        void Step(float dt)
        {
            steps++;
            clock += dt;
            // Native GenerationContinues precedes old-particle aging and all new births.
            float rate = Scalar(_birth, "m_Rate", clock);
            float rateVariation = Scalar(_birth, "m_RateVar", clock);
            float total = MathF.Max(dt * (rate + Rand() * rateVariation) + fraction, 0);
            if (!float.IsFinite(total) || total > 4096) throw new NotSupportedException("Point preview is limited to4096 births per step.");
            int born = (int)total;
            fraction = total - (long)total;
            int burst = Math.Max(BurstCount - amountCount, 0);
            amountCount += burst; born += burst;
            // UniformSpawn computes child-spawn counters even for zero SpawnNumber. Its draw
            // occurs every frame, including frames with no births (original native oracle).
            if (uniform) Rand();
            float sizeVariation = Scalar(_size, "m_SizeVar", clock);
            var sizeCurve = Curve(_size, "m_Size");
            foreach (var p in particles)
            {
                p.Age += dt;
                p.NormalizedAge = NormalizeAge(p.Age, p.Life);
                var size = sizeCurve.IsConstant ? sizeCurve.Sample(0) : sizeCurve.Sample(p.Emission, p.NormalizedAge);
                p.HalfSize = size.X * p.SizeFactor;
                if (!float.IsFinite(p.HalfSize)) throw new NotSupportedException("Native point size overflowed the finite preview range.");
            }
            if (_acceleration is not null)
                foreach (var p in particles)
                {
                    var acceleration = Value(_acceleration, "m_Acceleration", p.Emission, p.NormalizedAge);
                    float multiplier = Scalar(_acceleration, "m_AccelerationMultiplier", p.Emission, p.NormalizedAge);
                    float damping = Scalar(_acceleration, "m_DampingFactor", p.Emission, p.NormalizedAge);
                    if (damping < 0 || damping > 100) throw new NotSupportedException("Native damping preview is verified in the0..100 range.");
                    var applied = acceleration * multiplier;
                    p.Velocity = applied * dt + p.Velocity * NativePointDamping(damping, dt);
                }
            if (_speed is not null)
            {
                float maxDistance = Float(_graph, _speed, "m_MaxDist");
                float maxSquared = maxDistance * maxDistance;
                foreach (var p in particles)
                {
                    float speed = (Scalar(_speed, "m_Speed", 0, p.NormalizedAge)
                        + p.SpeedVariation * Scalar(_speed, "m_SpeedVar", 0, p.NormalizedAge))
                        * Scalar(_speed, "m_SpeedMultiplier", 0, p.NormalizedAge);
                    p.Velocity = p.Direction * speed + (p.Velocity - p.Direction * p.PreviousSpeed);
                    p.PreviousSpeed = speed;
                    p.Position += p.Velocity * dt;
                    if (!Finite(p.Position) || !Finite(p.Velocity)) throw new NotSupportedException("Native particle motion overflowed the finite preview range.");
                    float distance = p.Position.X * p.Position.X + p.Position.Y * p.Position.Y;
                    distance += p.Position.Z * p.Position.Z;
                    p.Outside = distance > maxSquared;
                }
            }
            // Native DeleteParticle swaps the last element into the hole and rechecks it.
            for (int i = 0; i < particles.Count;)
                if (particles[i].Age >= particles[i].Life || particles[i].Outside) { particles[i] = particles[^1]; particles.RemoveAt(particles.Count - 1); }
                else i++;
            if (born > 4096 || particles.Count + born > 4096) throw new NotSupportedException("Point preview is limited to4096 live particles.");
            if (_speed is not null && !cone && born > 1)
                throw new NotSupportedException("Multiple births per step with rectangular divergence require its native direction/subframe displacement path; reduce the preview step.");
            float initialAge = 0, spacing = born == 0 ? 0 : dt / born;
            for (int i = 0; i < born; i++)
            {
                float speedVariation = (particleRandom.Next() ^ 0x3423u) * (1 / 32767f) - .5f;
                float lifeValue = Scalar(_birth, "m_Life", clock);
                float lifeVariation = Scalar(_birth, "m_LifeVar", clock);
                float life = lifeValue + Rand() * lifeVariation;
                if (life <= 1e-8f) life = 100000000;
                life = MathF.Max(life, 1e-4f);
                if (!float.IsFinite(life)) throw new NotSupportedException("Native point lifetime overflowed the finite preview range.");
                float sizeFactor = (1 + Rand() * sizeVariation) * .5f;
                float half = sizeCurve.Sample(clock, 0).X * sizeFactor;
                if (!float.IsFinite(half)) throw new NotSupportedException("Native point size overflowed the finite preview range.");
                // Even speed zero has native direction-based newborn subframe displacement.
                var position = Vector3.Zero;
                var direction = Vector3.Zero;
                float speed = 0;
                if (_speed is not null)
                {
                    float symmetric = Rand(), unit = Unit();
                    if (cone)
                    {
                        direction = NativePointConeDirection(symmetric, unit, divergence);
                        var offset = direction * initialAge;
                        position = new(offset.X == 0 ? 0 : offset.X, offset.Y == 0 ? 0 : offset.Y, offset.Z == 0 ? 0 : offset.Z);
                    }
                    speed = (Scalar(_speed, "m_Speed") + speedVariation * Scalar(_speed, "m_SpeedVar"))
                        * Scalar(_speed, "m_SpeedMultiplier");
                }
                var velocity = direction * speed;
                velocity = new(velocity.X == 0 ? 0 : velocity.X, velocity.Y == 0 ? 0 : velocity.Y, velocity.Z == 0 ? 0 : velocity.Z);
                particles.Add(new() { Direction = direction, PreviousSpeed = speed, SpeedVariation = speedVariation, Velocity = velocity, Age = initialAge, Life = life, NormalizedAge = 0, Emission = clock, HalfSize = half, SizeFactor = sizeFactor, Position = position });
                initialAge += spacing;
            }
        }
        EffectPointSnapshot Snapshot()
        {
        Vector4 uv = _texture is null ? new(0, 0, 1, 1) : new(Float(_graph, _texture, "m_RegionU1"), Float(_graph, _texture, "m_RegionV1"),
            Float(_graph, _texture, "m_RegionU2"), Float(_graph, _texture, "m_RegionV2"));
        var output = new EffectPointParticle[particles.Count];
        for (int i = 0; i < output.Length; i++)
        {
            var p = particles[i];
            var color = Vector3.Clamp(Value(_color, "m_Color", p.Emission, p.NormalizedAge), Vector3.Zero, Vector3.One);
            color = new((int)(color.X * 255) / 255f, (int)(color.Y * 255) / 255f, (int)(color.Z * 255) / 255f);
            float opacity = Scalar(_color, "m_Opacity", p.Emission, p.NormalizedAge);
            var emissivity = _emissivity is null ? Vector3.Zero : Value(_emissivity, "m_Emissivity", p.Emission, p.NormalizedAge);
            uint packed = unchecked((uint)(int)(emissivity.X * 255) << 16 | (uint)(int)(emissivity.Y * 255) << 8 | (uint)(int)(emissivity.Z * 255));
            emissivity = new((packed >> 16 & 255) / 255f, (packed >> 8 & 255) / 255f, (packed & 255) / 255f);
            output[i] = new(p.Position, new(p.HalfSize), new(color, opacity), uv, emissivity, p.NormalizedAge, p.Life)
            { AgeSeconds = p.Age, EmissionTime = p.Emission, Velocity = p.Velocity };
        }
        return new(GenerationId, MaterialGuid, clock, output) { Frames = steps, RandomState = state };
        }
        return new(Step, Snapshot);
    }

    internal sealed record PointTimelineState(Action<float> Step, Func<EffectPointSnapshot> Snapshot);

    internal EffectPointSnapshot ReplayTimeline(int frames, float delta, float remainder, uint seed)
    {
        var state = CreateTimelineState(seed);
        for (int frame = 0; frame < frames; frame++) state.Step(delta);
        if (remainder > 0) state.Step(remainder);
        return state.Snapshot();
    }
}
