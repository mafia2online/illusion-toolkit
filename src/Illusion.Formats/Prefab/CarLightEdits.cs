namespace Illusion.Formats.Prefab;

public sealed partial class PrefabFile
{
    /// <summary>
    /// What the second number of a light entry (<see cref="CarPrefab.Light.Unk3"/>) says the light is, read
    /// off the shipped cars: every car's headlights carry 0, its indicators 3, and so on; 6 is on the police
    /// cars alone, on the bone of the roof beacon.
    /// </summary>
    public static IReadOnlyDictionary<uint, string> LightKinds { get; } = new Dictionary<uint, string>
    {
        [0] = "headlight", [2] = "brake", [3] = "indicator", [5] = "back", [6] = "beacon", [7] = "brake", [8] = "brake", [9] = "taxi sign",
    };

    /// <summary>
    /// The numbers the shipped cars write a light of a kind with, where they all agree: how strongly the lit
    /// piece glows, the middle of its pulse, the particles a broken lamp throws (760 the clear glass of the
    /// front, 761 the coloured glass of the rest), and the entry's last number, which is 2 on a beacon and 0 on
    /// every other light.
    /// </summary>
    public static (float Power, float Middle, uint BreakParticle, uint Last) LightDefaults(uint kind) => kind switch
    {
        0 => (2f, 0.6f, 760u, 0u),
        6 => (2f, 0.6f, 761u, 2u),
        7 or 8 or 2 => (2f, 0.5f, 761u, 0u),
        _ => (2f, 0.6f, 761u, 0u),
    };

    /// <summary>
    /// Writes one light of the car: the entry of <paramref name="light"/>'s bone is replaced, or - when the
    /// bone has none - a new one is added to the list the car keeps its lights in.
    /// </summary>
    /// <param name="added">True when the bone had no light before.</param>
    /// <returns>Null on success, otherwise why nothing was written.</returns>
    public string? SetLight(CarPrefab.Light light, out bool added)
    {
        ArgumentNullException.ThrowIfNull(light);
        added = false;
        if (Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is not { } entry) return "the file holds no car";
        List<Native.Model.PrefabShaderEffectInitW> effects = entry.CarInit[0].ShaderEffects;
        if (effects.Count == 0) return "the car has no block to keep lights in";
        if (light.Frame == 0) return "a light names the bone it lights";

        Native.Model.PrefabLightInitW? existing = effects.SelectMany(e => e.Lights).FirstOrDefault(l => l.FrameName == light.Frame);
        Native.Model.PrefabLightInitW target = existing ?? new Native.Model.PrefabLightInitW { FrameName = light.Frame };
        target.Unk1 = light.Unk1;
        target.Unk2 = light.Unk2;
        target.Unk3 = light.Unk3;
        target.Unk4 = light.Unk4;
        target.EmissivePower = light.EmissivePower;
        target.EmissiveMiddle = light.EmissiveMiddle;
        target.EmissiveSpeed0 = light.EmissiveSpeed0;
        target.EmissiveSpeed1 = light.EmissiveSpeed1;
        target.CheckBoneName = [.. light.CheckBones];
        target.LightModelHash = light.LightModel;
        target.ParticleBreakId = light.ParticleBreakId;
        target.Unk12 = light.Unk12;
        if (existing == null)
        {
            // the block that already keeps the car's lights; a car without any keeps them in its first block
            (effects.OrderByDescending(e => e.Lights.Count).First()).Lights.Add(target);
            added = true;
        }
        return null;
    }

    /// <summary>Takes the light of a bone out of the car. False when the bone has none.</summary>
    public bool RemoveLight(ulong frame)
    {
        if (Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is not { } entry) return false;
        bool removed = false;
        foreach (Native.Model.PrefabShaderEffectInitW effect in entry.CarInit[0].ShaderEffects)
        {
            removed |= effect.Lights.RemoveAll(l => l.FrameName == frame) > 0;
        }
        return removed;
    }
}
