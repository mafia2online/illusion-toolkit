using System.Globalization;
using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What a car's PREFAB says about its lights, side by side for several cars: the frames named as headlight,
/// backlight and toplight, the light sources' places, and every entry of the lights list - which bone is lit,
/// the numbers that tell one kind of light from another, how it glows and pulses. Read from the working
/// copies; nothing is written. It exists to learn what a police car has that a civilian one has not.
/// <para>Output: %TEMP%\illusion_car_lights.txt</para>
/// </summary>
internal static class CarLightProbes
{
    /// <summary>
    /// AN EXPERIMENT, not a tool: turns the light of one bone of a car into another kind, in the PREFAB of the
    /// car's working copy. Arguments: car, bone name, kind, the last number of the entry, light model name.
    /// The file is written in place - keep a copy first. Output: %TEMP%\illusion_car_light_set.txt
    /// </summary>
    internal static void SetKind(string[] a)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_light_set.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            if (a.Length < 5) { sb.AppendLine("usage: car bone kind unk12 model"); return; }
            var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", a[0] + ".sds"));
            string extracted = MafiaEnvironment.ExtractedDir(car);
            string file = Path.Combine(extracted, SdsManifest.Load(extracted).GetFiles("PREFAB")[0]);
            byte[] before = File.ReadAllBytes(file);
            PrefabFile prefab = PrefabFile.Load(file);
            sb.AppendLine($"{file}: {before.Length} bytes; written back unchanged it is the same file: {prefab.ToBytes().AsSpan().SequenceEqual(before)}");
            ulong bone = Fnv64.Hash(a[1]);
            Illusion.Formats.Native.Model.PrefabLightInitW? light = prefab.Wire.Prefabs.Where(p => p.CarInit.Count > 0)
                .SelectMany(p => p.CarInit[0].ShaderEffects).SelectMany(e => e.Lights).FirstOrDefault(l => l.FrameName == bone);
            if (light == null) { sb.AppendLine($"no light on bone '{a[1]}'"); return; }
            sb.AppendLine($"'{a[1]}' was kind 0x{light.Unk3:X}, last number {light.Unk12}, model 0x{light.LightModelHash:X16}");
            light.Unk3 = uint.Parse(a[2], CultureInfo.InvariantCulture);
            light.Unk12 = uint.Parse(a[3], CultureInfo.InvariantCulture);
            light.LightModelHash = Fnv64.Hash(a[4]);
            byte[] after = prefab.ToBytes();
            File.WriteAllBytes(file, after);
            CarPrefab.Light? read = PrefabFile.Load(file).Car?.Lights.FirstOrDefault(l => l.Frame == bone);
            sb.AppendLine($"written: {after.Length} bytes; reads back as kind 0x{read?.Unk3:X}, last number {read?.Unk12}, model 0x{read?.LightModel:X16} ({a[4]})");
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception — " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    /// <summary>
    /// AN EXPERIMENT, not a tool: adds a bone to a car's rig in its working copy (<see cref="Illusion.Assets.Frames.RigBones"/>)
    /// and writes the scene. Arguments: car, then any number of "name parent x y z". Keep a copy first, and
    /// reopen the car in the editor afterwards. Output: %TEMP%\illusion_car_bone_add.txt
    /// </summary>
    internal static void AddBones(string[] a)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_bone_add.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            if (a.Length < 6 || (a.Length - 1) % 5 != 0) { sb.AppendLine("usage: car (name parent x y z)..."); return; }
            var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", a[0] + ".sds"));
            string extracted = MafiaEnvironment.ExtractedDir(car);
            Illusion.Formats.Frames.FrameResource? frame = SdsMeshLoader.OpenScene(extracted).FrameResource;
            FrameObjectModel? model = frame?.FrameObjects.Values.OfType<FrameObjectModel>().OrderByDescending(m => m.GetSkeletonObject().BoneNames.Length).FirstOrDefault();
            if (frame == null || model == null) { sb.AppendLine("no skinned model"); return; }
            sb.AppendLine($"{a[0]}: {model.GetSkeletonObject().BoneNames.Length} bones before");
            for (int i = 1; i + 4 < a.Length; i += 5)
            {
                var at = new System.Numerics.Vector3(float.Parse(a[i + 2], CultureInfo.InvariantCulture), float.Parse(a[i + 3], CultureInfo.InvariantCulture), float.Parse(a[i + 4], CultureInfo.InvariantCulture));
                string? refused = Illusion.Assets.Frames.RigBones.Insert(model, a[i], a[i + 1], at, new System.Numerics.Vector3(0.06f, 0.10f, 0.07f), out int index);
                sb.AppendLine(refused == null ? $"  '{a[i]}' added under '{a[i + 1]}' at #{index}, standing at {at}" : $"  '{a[i]}' REFUSED: {refused}");
                if (refused != null) return;        // nothing is written when any bone is refused
            }
            string written = SdsWriter.SaveFrameResource(frame, car);
            FrameObjectModel? read = SdsMeshLoader.OpenScene(extracted).FrameResource?.FrameObjects.Values.OfType<FrameObjectModel>()
                .OrderByDescending(m => m.GetSkeletonObject().BoneNames.Length).FirstOrDefault();
            sb.AppendLine($"written {written}; read back: {read?.GetSkeletonObject().BoneNames.Length} bones, "
                + string.Join(", ", (read?.GetSkeletonObject().BoneNames ?? []).Select((n, i) => (n, i)).Where(x => a.Contains(x.n.String)).Select(x => $"{x.n.String} #{x.i}")));
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception — " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    /// <summary>
    /// AN EXPERIMENT, not a tool: adds a light to a car's PREFAB in its working copy, written like the light
    /// of another bone. Arguments: car, bone, kind, side (8 left, 0 right), the entry's last number, light model
    /// name, check bone, the bone whose entry is the pattern. Output: %TEMP%\illusion_car_light_set.txt
    /// </summary>
    internal static void AddLight(string[] a)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_light_set.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            if (a.Length < 8) { sb.AppendLine("usage: car bone kind side unk12 model checkBone likeBone"); return; }
            var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", a[0] + ".sds"));
            string extracted = MafiaEnvironment.ExtractedDir(car);
            string file = Path.Combine(extracted, SdsManifest.Load(extracted).GetFiles("PREFAB")[0]);
            PrefabFile prefab = PrefabFile.Load(file);
            ulong like = Fnv64.Hash(a[7]), bone = Fnv64.Hash(a[1]);
            List<Illusion.Formats.Native.Model.PrefabLightInitW>? list = prefab.Wire.Prefabs.Where(p => p.CarInit.Count > 0)
                .SelectMany(p => p.CarInit[0].ShaderEffects).Select(e => e.Lights).FirstOrDefault(l => l.Any(x => x.FrameName == like));
            Illusion.Formats.Native.Model.PrefabLightInitW? pattern = list?.FirstOrDefault(x => x.FrameName == like);
            if (list == null || pattern == null) { sb.AppendLine($"no light on bone '{a[7]}' to write it like"); return; }
            if (list.Any(x => x.FrameName == bone)) { sb.AppendLine($"'{a[1]}' has a light already"); return; }
            list.Add(new Illusion.Formats.Native.Model.PrefabLightInitW
            {
                FrameName = bone,
                Unk1 = pattern.Unk1,
                Unk2 = pattern.Unk2,
                Unk3 = uint.Parse(a[2], CultureInfo.InvariantCulture),
                Unk4 = uint.Parse(a[3], CultureInfo.InvariantCulture),
                EmissivePower = pattern.EmissivePower,
                EmissiveMiddle = pattern.EmissiveMiddle,
                EmissiveSpeed0 = pattern.EmissiveSpeed0,
                EmissiveSpeed1 = pattern.EmissiveSpeed1,
                CheckBoneName = [Fnv64.Hash(a[6])],
                LightModelHash = Fnv64.Hash(a[5]),
                ParticleBreakId = pattern.ParticleBreakId,
                Unk12 = uint.Parse(a[4], CultureInfo.InvariantCulture),
            });
            byte[] after = prefab.ToBytes();
            File.WriteAllBytes(file, after);
            sb.AppendLine($"'{a[1]}' added as light {list.Count} of its list; the file is {after.Length} bytes and reads back with {PrefabFile.Load(file).Car?.Lights.Count} lights");
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception — " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    internal static void Run(string[] cars)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_lights.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            if (cars.Length == 0) cars = ["smith_200_p_pha", "smith_200_pha", "shubert_38"];
            string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

            foreach (string name in cars)
            {
                var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", name + ".sds"));
                sb.AppendLine($"════ {name} ════");
                if (!car.Exists) { sb.AppendLine("  no such archive"); continue; }
                string extracted = SdsMeshLoader.EnsureExtracted(car);

                // every frame and bone name of the car, by hash: what the prefab's references are read against
                var frames = new Dictionary<ulong, string>();
                if (SdsMeshLoader.OpenScene(extracted).FrameResource is { FrameObjects: not null } fr)
                {
                    foreach (object o in fr.FrameObjects.Values)
                    {
                        if (o is FrameObjectBase f && f.Name.String is { Length: > 0 } n) frames[f.Name.Hash] = n;
                    }
                    foreach (FrameObjectModel m in fr.FrameObjects.Values.OfType<FrameObjectModel>())
                    {
                        foreach (HashName bone in m.GetSkeletonObject().BoneNames ?? [])
                        {
                            if (bone.String is { Length: > 0 } bn) frames[bone.Hash] = bn;
                        }
                    }
                }
                string Named(ulong hash) => hash == 0 ? "-" : frames.TryGetValue(hash, out string? n) ? n : "0x" + hash.ToString("X16", CultureInfo.InvariantCulture);

                foreach (string file in SdsManifest.Load(extracted).GetFiles("PREFAB"))
                {
                    if (PrefabFile.Load(Path.Combine(extracted, file)).Car is not { } prefab) continue;
                    sb.AppendLine($"  headlight model {Named(prefab.HeadlightModel)}, backlight model {Named(prefab.BacklightModel)}, toplight model {Named(prefab.ToplightModel)}");
                    sb.AppendLine($"  light sources: {prefab.LightMatrices.Count}");
                    foreach ((System.Numerics.Vector3 at, System.Numerics.Vector3 r0, System.Numerics.Vector3 r1, System.Numerics.Vector3 r2) in prefab.LightMatrices)
                    {
                        sb.AppendLine($"    at ({F(at.X)}, {F(at.Y)}, {F(at.Z)})  axes ({F(r0.X)}, {F(r0.Y)}, {F(r0.Z)}) ({F(r1.X)}, {F(r1.Y)}, {F(r1.Z)}) ({F(r2.X)}, {F(r2.Y)}, {F(r2.Z)})");
                    }
                    sb.AppendLine($"  lights: {prefab.Lights.Count}");
                    sb.AppendLine($"    {"frame",-26} {"unk1",5} {"unk2",5} {"unk3",10} {"unk4",10} {"power",7} {"middle",7} {"speed0",7} {"speed1",7} {"model",-20} {"break",6} {"unk12",6}  check bones");
                    foreach (CarPrefab.Light l in prefab.Lights)
                    {
                        sb.AppendLine($"    {Named(l.Frame),-26} {l.Unk1,5} {l.Unk2,5} {"0x" + l.Unk3.ToString("X", CultureInfo.InvariantCulture),10} {"0x" + l.Unk4.ToString("X", CultureInfo.InvariantCulture),10} "
                            + $"{F(l.EmissivePower),7} {F(l.EmissiveMiddle),7} {F(l.EmissiveSpeed0),7} {F(l.EmissiveSpeed1),7} {Named(l.LightModel),-20} {l.ParticleBreakId,6} {l.Unk12,6}  "
                            + string.Join(", ", l.CheckBones.Select(Named)));
                    }
                }
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception — " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }
}
