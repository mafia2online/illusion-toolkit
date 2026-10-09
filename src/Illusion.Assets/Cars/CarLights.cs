using System.Globalization;
using System.Numerics;
using Illusion.Assets.Frames;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

/// <summary>
/// A car's lights, and the bones they stand on.
///
/// <para>
/// A car has no light objects. Its lights are a list in its PREFAB, each entry naming a BONE of the body's
/// rig: the piece of the body skinned to that bone is what glows, and the flare and the cast light stand at
/// the bone itself. An entry says what kind of light it is (headlight, indicator, brake, back, beacon), which
/// side it switches with, how it glows, which bones must be whole for it to work, and which light model of
/// <c>cars_universal</c> it casts.
/// </para>
/// <para>
/// So a new lamp is three things: a bone standing AT the lamp (<see cref="AddBone"/>), the lamp's glass
/// skinned to that bone (in Blender, through the bridge), and an entry for the bone (<see cref="SetLight"/>).
/// A roof beacon that turns like a police car's is the police cars' own arrangement, copied whole:
/// <see cref="AddBeacon"/>.
/// </para>
/// <para>
/// Everything here works on the car's WORKING COPY on disk - the archive's own (the overloads taking the
/// archive) or any folder holding one (the <c>...In</c> overloads). An editor that has the car open keeps its
/// own copy of the scene: load the car again afterwards, and never run these with unsaved edits.
/// </para>
/// </summary>
public static class CarLights
{
    /// <summary>The names the police cars give the two bones of a roof beacon: the housing the light entry
    /// names, and under it the lamp the engine turns.</summary>
    public const string BeaconCasing = "light beacon casing", BeaconLamp = "light beacon";

    /// <summary>What <c>checkBones</c> is given as to take every check bone off an entry.</summary>
    public const string NoCheckBones = "-";

    /// <summary>How strongly a lit piece may be asked to glow. The shipped cars write 1.2 and 2.</summary>
    public const float MaxPower = 100f;

    /// <summary>The light models of <c>cars_universal</c> the shipped cars use, by what they are for.</summary>
    public static IReadOnlyList<string> Models { get; } =
        ["car_headlight_glow", "car_brake_glow", "car_indicator", "car_back", "car_beacon"];

    /// <summary>One light as a person reads it.</summary>
    public sealed record Light(string Bone, int Kind, string KindName, string Side, float Power, float Middle,
        float Speed0, float Speed1, string Model, int BreakParticle, int Last, IReadOnlyList<string> CheckBones);

    /// <summary>A car's lights and the names of its rig's bones.</summary>
    public sealed record Sheet(string Car, IReadOnlyList<Light> Lights, IReadOnlyList<string> Bones, string? ScaleBone);

    /// <summary>What <see cref="AddBeacon"/> did: the two bones, what the casing hangs on, where it stands.</summary>
    public sealed record Beacon(string Casing, string Lamp, string Parent, Vector3 At, bool BonesAdded, bool LightAdded, int Bones);

    private sealed class Opened
    {
        public required string Label;
        public required string Extracted;
        public required FrameResource Frame;
        public required string FramePath;
        public required FrameObjectModel Model;
        public required string PrefabPath;
        public required PrefabFile Prefab;
        public required Dictionary<ulong, string> Names;        // every bone, frame and known light model, by hash - for reading
        public required string[] Bones;
    }

    private static string? Open(string extracted, string label, out Opened? opened)
    {
        opened = null;
        ArgumentException.ThrowIfNullOrEmpty(extracted);
        if (SdsMeshLoader.OpenScene(extracted).FrameResource is not { FrameObjects: not null } frame) return $"{label} has no scene";
        FrameObjectModel? model = frame.FrameObjects.Values.OfType<FrameObjectModel>()
            .OrderByDescending(m => m.GetSkeletonObject().BoneNames?.Length ?? 0).FirstOrDefault();
        if (model == null) return $"{label} has no skinned model - a car's body is one";
        SdsManifest manifest = SdsManifest.Load(extracted);
        IReadOnlyList<string> prefabs = manifest.GetFiles("PREFAB"), scenes = manifest.GetFiles("FrameResource");
        if (prefabs.Count == 0) return $"{label} has no PREFAB";
        if (scenes.Count == 0) return $"{label} lists no frame resource";
        byte[] bytes = File.ReadAllBytes(prefabs[0]);
        PrefabFile prefab = PrefabFile.Load(prefabs[0]);
        if (prefab.Car == null) return $"{label}'s PREFAB holds no car";
        // The file is rewritten whole: only a file this code writes back byte for byte is touched.
        if (!prefab.ToBytes().AsSpan().SequenceEqual(bytes)) return $"{label}'s PREFAB is not written back the way it was read - it is left alone";

        var names = new Dictionary<ulong, string>();
        foreach (object o in frame.FrameObjects.Values)
        {
            if (o is FrameObjectBase f && f.Name.String is { Length: > 0 } n) names[f.Name.Hash] = n;
        }
        string[] bones = [.. (model.GetSkeletonObject().BoneNames ?? []).Select(b => b.String ?? "")];
        foreach (string bone in bones.Where(b => b.Length > 0)) names[Fnv64.Hash(bone)] = bone;
        foreach (string known in Models) names.TryAdd(Fnv64.Hash(known), known);
        opened = new Opened { Label = label, Extracted = extracted, Frame = frame, FramePath = scenes[0], Model = model, PrefabPath = prefabs[0], Prefab = prefab, Names = names, Bones = bones };
        return null;
    }

    private static string? Open(FileInfo car, out Opened? opened)
    {
        opened = null;
        ArgumentNullException.ThrowIfNull(car);
        if (!car.Exists) return $"no such archive: {car.FullName}";
        return Open(SdsMeshLoader.EnsureExtracted(car), car.Name, out opened);
    }

    private static Sheet Describe(Opened o)
    {
        string Named(ulong hash) => hash == 0 ? "" : o.Names.TryGetValue(hash, out string? n) ? n : "0x" + hash.ToString("X16", CultureInfo.InvariantCulture);
        CarPrefab prefab = o.Prefab.Car!;
        return new Sheet(o.Label,
            [.. prefab.Lights.Select(l => new Light(Named(l.Frame), (int)l.Unk3,
                PrefabFile.LightKinds.TryGetValue(l.Unk3, out string? kind) ? kind : "?", SideName(l.Unk4),
                l.EmissivePower, l.EmissiveMiddle, l.EmissiveSpeed0, l.EmissiveSpeed1, Named(l.LightModel), (int)l.ParticleBreakId, (int)l.Unk12,
                [.. l.CheckBones.Select(Named)]))],
            o.Bones, prefab.ScaleBone == 0 ? null : Named(prefab.ScaleBone));
    }

    private static string SideName(uint stored) =>
        stored == 8 ? "left" : stored == 0 ? "right" : "0x" + stored.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Reads a car's lights and bones.</summary>
    public static string? Read(FileInfo car, out Sheet? sheet)
    {
        sheet = null;
        if (Open(car, out Opened? o) is { } refused) return refused;
        sheet = Describe(o!);
        return null;
    }

    /// <inheritdoc cref="Read(FileInfo, out Sheet?)"/>
    public static string? ReadIn(string extracted, out Sheet? sheet)
    {
        sheet = null;
        if (Open(extracted, Path.GetFileName(extracted), out Opened? o) is { } refused) return refused;
        sheet = Describe(o!);
        return null;
    }

    // A bone of the body's rig, spelled as the rig spells it.
    private static string? Bone(Opened o, string asked, string what, out string name)
    {
        string wanted = (asked ?? "").Trim();
        name = o.Bones.FirstOrDefault(b => b.Length > 0 && string.Equals(b, wanted, StringComparison.OrdinalIgnoreCase)) ?? "";
        return name.Length > 0 ? null : $"the body's rig has no bone named '{wanted}' ({what})";
    }

    private static string? Kind(string asked, out uint kind)
    {
        string wanted = (asked ?? "").Trim();
        if (uint.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out kind))
        {
            return PrefabFile.LightKinds.ContainsKey(kind) ? null : $"no shipped car has a light of kind {kind}: the kinds are {string.Join(", ", PrefabFile.LightKinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}";
        }
        foreach ((uint number, string name) in PrefabFile.LightKinds.OrderBy(k => k.Key))
        {
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) { kind = number; return null; }
        }
        return $"kind is one of {string.Join(", ", PrefabFile.LightKinds.Values.Distinct())}, or the number itself";
    }

    // What is asked of an entry. Null = not asked: an entry that exists keeps what it has, a new one is written
    // like a light of that kind the car already has, else the way every shipped car writes the kind.
    private static string? Change(Opened o, string bone, string? kind, string? side, string? model, IReadOnlyList<string>? checkBones,
        float? power, out bool added)
    {
        added = false;
        if (Bone(o, bone, "the bone the light stands on", out string boneName) is { } noBone) return noBone;
        ulong hash = Fnv64.Hash(boneName);
        CarPrefab.Light? was = o.Prefab.Car!.Lights.FirstOrDefault(l => l.Frame == hash);

        uint number;
        if (string.IsNullOrWhiteSpace(kind))
        {
            if (was == null) return "a new light needs its kind: " + string.Join(", ", PrefabFile.LightKinds.Values.Distinct());
            number = was.Unk3;
        }
        else if (Kind(kind, out number) is { } noKind)
        {
            return noKind;
        }
        // The entry itself is its own pattern; a new one, or one that changes kind, is written like its kind.
        CarPrefab.Light? like = was != null && was.Unk3 == number
            ? was
            : o.Prefab.Car!.Lights.FirstOrDefault(l => l.Unk3 == number && l.Frame != hash);
        (float defPower, float defMiddle, uint defBreak, uint defLast) = PrefabFile.LightDefaults(number);

        uint sideNumber;
        switch ((side ?? "").Trim().ToLowerInvariant())
        {
            case "": sideNumber = was?.Unk4 ?? 8; break;
            case "left" or "l": sideNumber = 8; break;
            case "right" or "r": sideNumber = 0; break;
            default: return "side is 'left' or 'right' (a lamp in the middle is written as left, as the police beacon is)";
        }

        ulong modelHash;
        string wantedModel = (model ?? "").Trim();
        if (wantedModel.Length == 0)
        {
            string standard = number switch { 0 => "car_headlight_glow", 3 => "car_indicator", 5 => "car_back", 6 => "car_beacon", 9 => "", _ => "car_brake_glow" };
            modelHash = like?.LightModel ?? (standard.Length > 0 ? Fnv64.Hash(standard) : 0);
        }
        else if (Models.FirstOrDefault(m => string.Equals(m, wantedModel, StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            modelHash = Fnv64.Hash(known);
        }
        else if (wantedModel.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(wantedModel[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong parsed))
        {
            modelHash = parsed;
        }
        else
        {
            return $"model is one of {string.Join(", ", Models)} - or the 0x... hash of another light model's name";
        }

        List<ulong> checks;
        if (checkBones == null || checkBones.Count == 0)
        {
            checks = [.. was?.CheckBones ?? []];
        }
        else if (checkBones.Count == 1 && checkBones[0].Trim() == NoCheckBones)
        {
            checks = [];
        }
        else
        {
            checks = [];
            foreach (string check in checkBones)
            {
                if (Bone(o, check, "a bone that must be whole for the light to work", out string checkName) is { } noCheck) return noCheck;
                checks.Add(Fnv64.Hash(checkName));
            }
        }

        if (power is { } asked && !(float.IsFinite(asked) && asked >= 0f && asked <= MaxPower))
        {
            return $"power is how strongly the piece glows: 0 to {MaxPower.ToString(CultureInfo.InvariantCulture)} (the shipped cars write 1.2 and 2)";
        }

        var light = new CarPrefab.Light(hash, like?.Unk1 ?? -1, like?.Unk2 ?? -1, number, sideNumber,
            power ?? like?.EmissivePower ?? defPower, like?.EmissiveMiddle ?? defMiddle, like?.EmissiveSpeed0 ?? 8f, like?.EmissiveSpeed1 ?? 3f,
            checks, modelHash, like?.ParticleBreakId ?? defBreak, like?.Unk12 ?? defLast);
        return o.Prefab.SetLight(light, out added);
    }

    private static void WritePrefab(Opened o) => AtomicFile.WriteAllBytes(o.PrefabPath, o.Prefab.ToBytes());

    private static void WriteScene(Opened o) => AtomicFile.WriteAllBytes(o.FramePath, o.Frame.WriteToStream());

    /// <summary>
    /// Writes the light of one bone. An entry the bone already has is CHANGED: only what is asked for is
    /// replaced. A bone without one gets a new entry, written like a light of that kind the car already has,
    /// else the way every shipped car writes that kind.
    /// </summary>
    /// <param name="kind">A name of <see cref="PrefabFile.LightKinds"/> or the number. Null keeps an existing
    /// entry's kind; a new entry needs one.</param>
    /// <param name="side">"left" or "right": which side's switch it follows. Null keeps (new: left).</param>
    /// <param name="model">A light model of <c>cars_universal</c> by name (<see cref="Models"/>) or by the
    /// 0x... hash of its name. Null keeps (new: the one the shipped cars use with that kind).</param>
    /// <param name="checkBones">Bones of the rig that must be whole for the light to work - the part the lamp
    /// sits on. Null or empty keeps; the single name <see cref="NoCheckBones"/> takes them all off.</param>
    /// <param name="power">How strongly the piece glows, 0 to <see cref="MaxPower"/>. Null keeps.</param>
    public static string? SetLight(FileInfo car, string bone, string? kind, string? side, string? model, IReadOnlyList<string>? checkBones,
        float? power, out bool added, out Sheet? sheet)
    {
        added = false;
        sheet = null;
        if (Open(car, out Opened? o) is { } refused) return refused;
        return SetLightOn(o!, bone, kind, side, model, checkBones, power, out added, out sheet);
    }

    /// <inheritdoc cref="SetLight(FileInfo, string, string?, string?, string?, IReadOnlyList{string}?, float?, out bool, out Sheet?)"/>
    public static string? SetLightIn(string extracted, string bone, string? kind, string? side, string? model, IReadOnlyList<string>? checkBones,
        float? power, out bool added, out Sheet? sheet)
    {
        added = false;
        sheet = null;
        if (Open(extracted, Path.GetFileName(extracted), out Opened? o) is { } refused) return refused;
        return SetLightOn(o!, bone, kind, side, model, checkBones, power, out added, out sheet);
    }

    private static string? SetLightOn(Opened o, string bone, string? kind, string? side, string? model, IReadOnlyList<string>? checkBones,
        float? power, out bool added, out Sheet? sheet)
    {
        sheet = null;
        if (Change(o, bone, kind, side, model, checkBones, power, out added) is { } notSet) return notSet;
        WritePrefab(o);
        sheet = Describe(o);
        return null;
    }

    /// <summary>Takes the light of a bone out of the car.</summary>
    public static string? RemoveLight(FileInfo car, string bone, out Sheet? sheet)
    {
        sheet = null;
        if (Open(car, out Opened? o) is { } refused) return refused;
        return RemoveLightOn(o!, bone, out sheet);
    }

    /// <inheritdoc cref="RemoveLight(FileInfo, string, out Sheet?)"/>
    public static string? RemoveLightIn(string extracted, string bone, out Sheet? sheet)
    {
        sheet = null;
        if (Open(extracted, Path.GetFileName(extracted), out Opened? o) is { } refused) return refused;
        return RemoveLightOn(o!, bone, out sheet);
    }

    private static string? RemoveLightOn(Opened o, string bone, out Sheet? sheet)
    {
        sheet = null;
        if (Bone(o, bone, "the bone whose light goes", out string name) is { } noBone) return noBone;
        if (!o.Prefab.RemoveLight(Fnv64.Hash(name))) return $"'{name}' has no light";
        WritePrefab(o);
        sheet = Describe(o);
        return null;
    }

    /// <summary>
    /// Adds a bone to the body's rig, standing at a place in the model's space (<see cref="RigBones.Insert"/>),
    /// and writes the scene. Bones behind it move up by one - whatever was pulled into Blender before is stale.
    /// </summary>
    /// <param name="reach">How far the geometry the bone will carry reaches from it, in metres.</param>
    public static string? AddBone(FileInfo car, string name, string parent, Vector3 at, float reach, out int index, out int count)
    {
        index = -1;
        count = 0;
        if (Open(car, out Opened? o) is { } refused) return refused;
        return AddBoneOn(o!, name, parent, at, reach, out index, out count);
    }

    /// <inheritdoc cref="AddBone(FileInfo, string, string, Vector3, float, out int, out int)"/>
    public static string? AddBoneIn(string extracted, string name, string parent, Vector3 at, float reach, out int index, out int count)
    {
        index = -1;
        count = 0;
        if (Open(extracted, Path.GetFileName(extracted), out Opened? o) is { } refused) return refused;
        return AddBoneOn(o!, name, parent, at, reach, out index, out count);
    }

    private static string? AddBoneOn(Opened o, string name, string parent, Vector3 at, float reach, out int index, out int count)
    {
        index = -1;
        count = 0;
        if (!(reach > 0f) || reach > 5f) return "reach is how far the bone's geometry reaches from it, in metres";
        // A refusal leaves the working copy alone: the scene read here is simply not written.
        if (RigBones.Insert(o.Model, (name ?? "").Trim(), (parent ?? "").Trim(), at, new Vector3(reach), out index) is { } notAdded) return notAdded;
        WriteScene(o);
        count = o.Model.GetSkeletonObject().BoneNames?.Length ?? 0;
        return null;
    }

    /// <summary>
    /// Gives the car a roof beacon the way the police cars have one - the only arrangement seen to work as a
    /// TURNING light: a bone named <see cref="BeaconCasing"/> under the bone the rig is scaled by, a bone named
    /// <see cref="BeaconLamp"/> under it, both standing at the lamp, and one light entry of the beacon kind on
    /// the casing. (A beacon entry on a bone hung elsewhere in the rig showed no light.) A car that already has
    /// the two bones AT that place keeps them and gets the light entry; one that has them somewhere else is
    /// refused - this does not move a beacon. What is left to do is skin the lamp's glass to the casing bone,
    /// and anything that should turn inside it to the lamp bone.
    /// </summary>
    /// <param name="checkBone">The bone that must be whole for the beacon to work: the deform bone of the part
    /// it sits on (the roof's).</param>
    public static string? AddBeacon(FileInfo car, Vector3 at, string checkBone, float reach, out Beacon? beacon)
    {
        beacon = null;
        if (Open(car, out Opened? o) is { } refused) return refused;
        return AddBeaconOn(o!, at, checkBone, reach, out beacon);
    }

    /// <inheritdoc cref="AddBeacon(FileInfo, Vector3, string, float, out Beacon?)"/>
    public static string? AddBeaconIn(string extracted, Vector3 at, string checkBone, float reach, out Beacon? beacon)
    {
        beacon = null;
        if (Open(extracted, Path.GetFileName(extracted), out Opened? o) is { } refused) return refused;
        return AddBeaconOn(o!, at, checkBone, reach, out beacon);
    }

    private static string? AddBeaconOn(Opened o, Vector3 at, string checkBone, float reach, out Beacon? beacon)
    {
        beacon = null;
        if (!(reach > 0f) || reach > 5f) return "reach is how far the lamp reaches from its middle, in metres";
        if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z)) return "the lamp's place must be finite numbers";
        if (Bone(o, checkBone, "the part the beacon sits on", out _) is { } noCheck) return noCheck;
        ulong scale = o.Prefab.Car!.ScaleBone;
        string? scaleBone = o.Bones.FirstOrDefault(b => b.Length > 0 && Fnv64.Hash(b) == scale);
        if (scaleBone == null) return "the car's PREFAB names no bone of the rig as the one it is scaled by";

        // A beacon the car already has stays where it is; asking for one somewhere else is not a move.
        foreach (string existing in new[] { BeaconCasing, BeaconLamp })
        {
            int had = Array.FindIndex(o.Bones, b => string.Equals(b, existing, StringComparison.OrdinalIgnoreCase));
            if (had < 0) continue;
            Vector3 stands = (o.Model.RestTransform ?? [])[had].Translation;
            if (Vector3.Distance(stands, at) > 0.01f)
            {
                return string.Create(CultureInfo.InvariantCulture, $"the car already has the bone '{existing}', standing at {stands.X:0.###}, {stands.Y:0.###}, {stands.Z:0.###} - this does not move a beacon");
            }
        }

        bool bonesAdded = false;
        foreach ((string name, string under) in new[] { (BeaconCasing, scaleBone), (BeaconLamp, BeaconCasing) })
        {
            string[] now = [.. (o.Model.GetSkeletonObject().BoneNames ?? []).Select(b => b.String ?? "")];
            if (now.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (RigBones.Insert(o.Model, name, under, at, new Vector3(reach), out _) is { } notAdded) return $"'{name}': {notAdded}";
            bonesAdded = true;
        }
        o.Bones = [.. (o.Model.GetSkeletonObject().BoneNames ?? []).Select(b => b.String ?? "")];
        foreach (string bone in o.Bones.Where(b => b.Length > 0)) o.Names[Fnv64.Hash(bone)] = bone;
        if (Change(o, BeaconCasing, "beacon", "left", "car_beacon", [checkBone], null, out bool lightAdded) is { } notSet) return notSet;

        // Two files. The scene first; if the prefab then cannot be written, the scene goes back to what it was,
        // so the car is never left with half a beacon.
        byte[]? sceneWas = bonesAdded ? File.ReadAllBytes(o.FramePath) : null;
        if (bonesAdded) WriteScene(o);
        try
        {
            WritePrefab(o);
        }
        catch (Exception ex) when (sceneWas != null && ex is IOException or UnauthorizedAccessException)
        {
            AtomicFile.WriteAllBytes(o.FramePath, sceneWas!);
            throw;
        }

        int casing = Array.FindIndex(o.Bones, b => string.Equals(b, BeaconCasing, StringComparison.OrdinalIgnoreCase));
        byte[] parents = o.Model.GetSkeletonHierarchyObject().ParentIndices ?? [];
        string parent = casing >= 0 && casing < parents.Length && parents[casing] < o.Bones.Length ? o.Bones[parents[casing]] : scaleBone;
        beacon = new Beacon(BeaconCasing, BeaconLamp, parent, (o.Model.RestTransform ?? [])[casing].Translation, bonesAdded, lightAdded, o.Bones.Length);
        return null;
    }
}
