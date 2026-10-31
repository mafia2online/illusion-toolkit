using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Cars;

/// <summary>Fusion S_InitLight authoring. New entries copy a native same-kind template.</summary>
public static class CompiledCarLights
{
    private static readonly IReadOnlyDictionary<int, string> Kinds = new Dictionary<int, string>
    {
        [1] = "headlight", [2] = "small headlight", [4] = "indicator", [6] = "reverse",
        [7] = "beacon", [9] = "brake", [12] = "interior", [15] = "beacon flash A", [16] = "beacon flash B"
    };
    private static readonly string[] Models = ["car_aux_amber_glow", "car_aux_red_glow", "car_beacon",
        "car_brake_glow", "car_broken_light_glow", "car_headlight_broken_glow", "car_headlight_glow",
        "car_headlight_small_glow", "car_interior", "car_reverse_glow", "car_searchlight",
        "car_servicebeacon", "car_signal_amber_glow", "car_signal_white_glow"];

    private sealed class Opened
    {
        public required CompiledSceneDocument Document;
        public required CompiledVehicleRig Rig;
        public required CompiledElement Container;
        public CompiledGraph Graph => Document.Graph;
        public IReadOnlyList<CompiledElement> Rows => Graph.Items(Container);
        public CompiledResource Owner => Graph.Owner(Container);
    }

    private static string? Open(FileInfo archive, out Opened? opened)
    {
        opened = null;
        var doc = new CompiledSceneDocument(archive);
        var rigs = new List<CompiledVehicleRig>();
        void Visit(SdsFrameNode node)
        {
            if (node.Source is CompiledSceneDocument.CompiledFrameNode frame && frame.VehicleRig is { } rig) rigs.Add(rig);
            foreach (var child in node.Children) Visit(child);
        }
        foreach (var root in doc.Roots) Visit(root);
        if (rigs.MaxBy(r => r.Count) is not { } main) return "The vehicle has no compiled skeleton.";
        var graph = doc.Graph;
        var entity = graph.Nodes.LastOrDefault(n => n.TypeId == 0xdeb11995);
        var prefab = graph.Resolve(graph.Field(entity, "m_BuiltPrefabsCPtr"));
        var container = graph.Field(graph.Field(prefab, "m_SShaderEffectInit"), "m_LightInit");
        if (container is null) return "The vehicle has no native S_InitLight array.";
        opened = new() { Document = doc, Rig = main, Container = container };
        return null;
    }

    private static string Name(Opened o, ulong hash)
    {
        if (hash == 0) return "";
        foreach (string known in new[] { CarLights.BeaconCasing, CarLights.BeaconLamp })
            if (Fnv64.Hash(known, Encoding.UTF8) == hash) return known;
        for (int i = 0; i < o.Rig.Count; i++) if (o.Rig.NameHash(i) == hash) return o.Rig.Name(i);
        return Models.FirstOrDefault(n => Fnv64.Hash(n, Encoding.UTF8) == hash) ?? $"0x{hash:X16}";
    }
    private static ulong Integer(Opened o, CompiledElement row, string name) => o.Graph.Integer(o.Graph.Field(row, name)!);
    private static float Float(Opened o, CompiledElement row, string name) =>
        BinaryPrimitives.ReadSingleLittleEndian(o.Graph.Data(o.Graph.Field(row, name)!));
    private static IReadOnlyList<ulong> Checks(Opened o, CompiledElement row)
    {
        var data = o.Graph.Data(o.Graph.Field(row, "m_CheckBoneName")!);
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data));
        if (data.Length != 4 + count * 8) throw new InvalidDataException("Invalid native light check-bone array.");
        var result = new ulong[count];
        for (int i = 0; i < count; i++) result[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(4 + i * 8)..]);
        return result;
    }
    private static CarLights.Sheet Describe(Opened o, string car) => new(car, o.Rows.Select(row =>
    {
        int kind = checked((int)Integer(o, row, "m_LightType"));
        uint flags = checked((uint)Integer(o, row, "m_LightFlags"));
        return new CarLights.Light(Name(o, Integer(o, row, "m_FrameName")), kind, Kinds.GetValueOrDefault(kind, $"native {kind}"),
            (flags & 8) != 0 ? "left" : "right", Float(o, row, "m_EmissiveOn"), Float(o, row, "m_EmissiveMiddle"),
            Float(o, row, "m_LightEmissiveChangeSpeedOn"), Float(o, row, "m_LightEmissiveChangeSpeedOff"),
            Name(o, Integer(o, row, "m_LightModelName")), unchecked((int)(uint)Integer(o, row, "m_ParticleBreakId")),
            checked((int)Integer(o, row, "m_NumOfBeaconLights")), Checks(o, row).Select(h => Name(o, h)).ToArray());
    }).ToArray(), Enumerable.Range(0, o.Rig.Count).Select(i => Name(o, o.Rig.NameHash(i))).ToArray(), null);

    public static string? Read(FileInfo archive, out CarLights.Sheet? sheet)
    {
        sheet = null;
        if (Open(archive, out var o) is { } error) return error;
        sheet = Describe(o!, Path.GetFileNameWithoutExtension(archive.Name));
        return null;
    }

    public static string? SetLight(FileInfo archive, string bone, string? kind, string? side, string? model,
        IReadOnlyList<string> checkBones, float? power, out bool added, out CarLights.Sheet? sheet)
    {
        added = false; sheet = null;
        if (Open(archive, out var opened) is { } error) return error;
        var o = opened!;
        int index = o.Rig.Find(bone);
        if (index < 0) return "The compiled body rig has no such bone; use a name or bone:index from car_lights.";
        ulong frameHash = o.Rig.NameHash(index);
        var existing = o.Rows.SingleOrDefault(row => Integer(o, row, "m_FrameName") == frameHash);
        int number;
        if (string.IsNullOrWhiteSpace(kind))
        {
            if (existing is null) return "A new light requires a native kind (car_lights lists this vehicle's kinds).";
            number = checked((int)Integer(o, existing, "m_LightType"));
        }
        else if (!int.TryParse(kind, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            var known = Kinds.FirstOrDefault(k => k.Value.Equals(kind.Trim(), StringComparison.OrdinalIgnoreCase));
            if (known.Value is null) return "Use a native light kind number or " + string.Join(", ", Kinds.Values) + ".";
            number = known.Key;
        }
        if (number is < 0 or > 17) return "The native light type must be between 0 and 17 and have a verified template.";
        var template = existing is not null && Integer(o, existing, "m_LightType") == (ulong)number ? existing
            : o.Rows.FirstOrDefault(row => Integer(o, row, "m_LightType") == (ulong)number);
        var templateSource = o;
        if (template is null)
        {
            var stock = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "cars", "bruno_ravine_sheriff.sds"));
            if (stock.Exists && !stock.FullName.Equals(archive.FullName, StringComparison.OrdinalIgnoreCase)
                && Open(stock, out var donor) is null)
            {
                template = donor!.Rows.FirstOrDefault(row => Integer(donor, row, "m_LightType") == (ulong)number);
                if (template is not null) templateSource = donor;
            }
        }
        if (template is null) return "No native template of that light kind is available; no guessed defaults are written.";
        if (!ReferenceEquals(templateSource, o) && templateSource.Graph.ValidateCopySubtree(o.Owner, template).Count != 0)
            return "The stock light template requires reflection schemas absent from this vehicle.";
        if (power is { } p && (!float.IsFinite(p) || p < 0 || p > CarLights.MaxPower)) return "Emissive power must be finite and between 0 and 100.";
        uint? sideBit = side?.Trim().ToLowerInvariant() switch
        {
            null or "" => null, "left" or "l" => 8, "right" or "r" => 0, _ => uint.MaxValue
        };
        if (sideBit == uint.MaxValue) return "Side is left or right.";
        ulong? modelHash = null;
        if (!string.IsNullOrWhiteSpace(model))
        {
            if (model.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(model.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash)) modelHash = hash;
            else if (Models.FirstOrDefault(m => m.Equals(model, StringComparison.OrdinalIgnoreCase)) is { } known) modelHash = Fnv64.Hash(known, Encoding.UTF8);
            else return "Use a native cars_universal light model name or its 0x hash.";
        }
        List<ulong>? checks = null;
        if (checkBones.Count == 0 && existing is not null && !ReferenceEquals(existing, template)) checks = Checks(o, existing).ToList();
        if (checkBones.Count > 0)
        {
            checks = [];
            if (!(checkBones.Count == 1 && checkBones[0] == CarLights.NoCheckBones))
                foreach (string check in checkBones)
                {
                    int found = o.Rig.Find(check);
                    if (found < 0) return "A check bone is absent from the compiled body rig: " + check;
                    checks.Add(o.Rig.NameHash(found));
                }
        }
        // Preflight every request before creating a detached clone or changing a native value.
        int shaderIndex = existing is null ? o.Rows.Select(row => checked((int)Integer(o, row, "m_LightShaderIndex"))).DefaultIfEmpty(0).Max() + 1
            : checked((int)Integer(o, existing, "m_LightShaderIndex"));
        if (shaderIndex is < 0 or > 46) return "The native emissivity index range is exhausted; existing mesh indices are retained.";
        var rowToWrite = existing is not null && ReferenceEquals(template, existing) ? existing
            : o.Owner.CopySubtree(templateSource.Owner, template, new Dictionary<ulong, ulong>());
        var graph = ReferenceEquals(rowToWrite, existing) ? o.Graph : new CompiledGraph([o.Owner]);
        var plan = new CompiledRewrite { AllowNativeAllocatorFallback = true };
        void Value(string name, byte[] data) => plan.Values[graph.Field(rowToWrite, name)!] = data;
        static byte[] U32(uint value) { byte[] b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); return b; }
        static byte[] U64(ulong value) { byte[] b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); return b; }
        Value("m_FrameName", U64(frameHash));
        Value("m_LightShaderIndex", U32(checked((uint)shaderIndex)));
        if (sideBit is { } bit) Value("m_LightFlags", U32((checked((uint)Integer(templateSource, template, "m_LightFlags")) & ~8u) | bit));
        if (modelHash is { } modelValue) Value("m_LightModelName", U64(modelValue));
        if (power is { } emissive) Value("m_EmissiveOn", BitConverter.GetBytes(emissive));
        if (checks is not null)
        {
            byte[] values = new byte[4 + checks.Count * 8]; BinaryPrimitives.WriteUInt32LittleEndian(values, checked((uint)checks.Count));
            for (int i = 0; i < checks.Count; i++) BinaryPrimitives.WriteUInt64LittleEndian(values.AsSpan(4 + i * 8), checks[i]);
            Value("m_CheckBoneName", values);
        }
        if (sideBit is null && existing is not null && !ReferenceEquals(existing, template))
            Value("m_LightFlags", U32((checked((uint)Integer(templateSource, template, "m_LightFlags")) & ~8u)
                | (checked((uint)Integer(o, existing, "m_LightFlags")) & 8u)));
        var rows = o.Rows.ToList();
        added = existing is null;
        if (existing is null) rows.Add(rowToWrite);
        else rows[rows.IndexOf(existing)] = rowToWrite;
        // Stock data is sorted by bone hash; retain that native lookup order when adding/moving an entry.
        rows = rows.OrderBy(row => ReferenceEquals(row, rowToWrite) ? frameHash : Integer(o, row, "m_FrameName")).ToList();
        plan.Children[o.Container] = o.Container.Children.Where(c => !o.Rows.Contains(c)).Concat(rows).ToArray();
        plan.Values[o.Container.Values.First()] = U32(checked((uint)rows.Count));
        AtomicFile.WriteAllBytes(o.Owner.Path, o.Owner.Rewrite(plan).ToArray());
        return Read(archive, out sheet);
    }

    public static string? RemoveLight(FileInfo archive, string bone, out CarLights.Sheet? sheet)
    {
        sheet = null;
        if (Open(archive, out var opened) is { } error) return error;
        var o = opened!; int index = o.Rig.Find(bone);
        if (index < 0) return "The compiled body rig has no such bone.";
        var remove = o.Rows.Where(row => Integer(o, row, "m_FrameName") == o.Rig.NameHash(index)).ToArray();
        if (remove.Length == 0) return "That bone has no native light entry.";
        var plan = new CompiledRewrite { AllowNativeAllocatorFallback = true };
        plan.Children[o.Container] = o.Container.Children.Where(c => !remove.Contains(c)).ToArray();
        byte[] count = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(count, checked((uint)(o.Rows.Count - remove.Length)));
        plan.Values[o.Container.Values.First()] = count;
        AtomicFile.WriteAllBytes(o.Owner.Path, o.Owner.Rewrite(plan).ToArray());
        return Read(archive, out sheet);
    }

    public static string? AddBeacon(FileInfo archive, Vector3 position, string checkBone, float reach, out CarLights.Beacon? beacon)
    {
        beacon = null;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
            || !float.IsFinite(reach) || reach <= 0) return "Beacon position and positive bone reach must be finite.";
        if (Open(archive, out var opened) is { } error) return error;
        var o = opened!;
        int parent = o.Rig.Find(checkBone);
        if (parent < 0) return "The beacon parent/check bone must exist in the compiled body rig.";
        int casing = o.Rig.Find(CarLights.BeaconCasing), lampIndex = o.Rig.Find(CarLights.BeaconLamp);
        int needed = (casing < 0 ? 1 : 0) + (lampIndex < 0 ? 1 : 0);
        if (o.Rig.Count + needed > 255) return "The compiled skeleton has no space for the beacon bones.";
        var manifest = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(archive));
        var before = manifest.GetFiles("Generic").ToDictionary(path => path, File.ReadAllBytes);
        try
        {
            if (casing < 0) CompiledCarRig.AddBone(archive, CarLights.BeaconCasing, o.Rig.Name(parent), position, reach);
            else
            {
                int originalParent = o.Rig.Parent(casing);
                Matrix4x4 parentModel = originalParent < 0 ? Matrix4x4.Identity : o.Rig.ModelRest(originalParent);
                if (!Matrix4x4.Invert(parentModel, out var inverse)) throw new InvalidDataException("Invalid beacon parent transform.");
                var pose = o.Rig.ModelRest(casing); pose.Translation = position;
                o.Rig.BoneSource(casing).LocalTransform = pose * inverse;
                o.Document.SaveWorkingCopy();
            }
            if (lampIndex < 0) CompiledCarRig.AddBone(archive, CarLights.BeaconLamp, CarLights.BeaconCasing, position, reach);
            if (SetLight(archive, CarLights.BeaconCasing, "7", "left", "car_beacon", [o.Rig.Name(parent)], null,
                out bool lightAdded, out _) is { } refused)
            {
                foreach (var (path, bytes) in before) AtomicFile.WriteAllBytes(path, bytes);
                return refused;
            }
            beacon = new(CarLights.BeaconCasing, CarLights.BeaconLamp, o.Rig.Name(parent), position, needed > 0, lightAdded, o.Rig.Count + needed);
            return null;
        }
        catch
        {
            foreach (var (path, bytes) in before) AtomicFile.WriteAllBytes(path, bytes);
            throw;
        }
    }
}
