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
/// What a car's PREFAB says about its MATERIALS: the list the game colours and dirties by, and the list it
/// deforms by - each entry a 64-bit id written as two halves - held against the materials the car's meshes
/// name, for each archive named (a winter twin is named like any other: <c>car_z</c>). Read from the working
/// copies, which are extracted first where the archive has none; nothing else is written.
/// <para>Output: %TEMP%\illusion_car_paint.txt</para>
/// </summary>
internal static class CarPaintProbes
{
    internal static void Run(string[] cars)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_paint.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            MafiaMaterials.EnsureLoaded();
            if (cars.Length == 0) cars = ["shubert_hearse", "shubert_38", "smith_200_p_pha"];
            string Material(ulong hash) => MafiaMaterials.GetMaterialName(hash) ?? "?";

            foreach (string name in cars)
            {
                var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", name + ".sds"));
                sb.AppendLine($"════ {name} ════");
                if (!car.Exists) { sb.AppendLine("  no such archive"); continue; }
                string extracted = SdsMeshLoader.EnsureExtracted(car);

                // the materials the meshes name, per level
                var used = new Dictionary<ulong, int>();
                if (SdsMeshLoader.OpenScene(extracted).FrameResource is { FrameObjects: not null } frame)
                {
                    foreach (FrameObjectSingleMesh mesh in frame.FrameObjects.Values.OfType<FrameObjectSingleMesh>())
                    {
                        foreach (var level in mesh.Material?.Materials ?? [])
                        {
                            foreach (var slot in level ?? []) used[slot.MaterialHash] = used.GetValueOrDefault(slot.MaterialHash) + slot.NumFaces;
                        }
                    }
                }
                sb.AppendLine($"  materials the meshes name: {used.Count}");
                foreach ((ulong hash, int faces) in used.OrderBy(p => Material(p.Key), StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"    0x{hash:X16}  {Material(hash),-28} {faces,6} faces");
                }

                // texture names of the archive, by hash, to read the lists' texture references against
                var textures = new Dictionary<ulong, string>();
                foreach (string file in SdsManifest.Load(extracted).GetFiles("Texture"))
                {
                    string texture = Path.GetFileName(file);
                    textures[Fnv64.Hash(texture)] = texture;
                    textures[Fnv64.Hash(Path.GetFileNameWithoutExtension(texture))] = texture + " (no extension)";
                }

                foreach (string file in SdsManifest.Load(extracted).GetFiles("PREFAB"))
                {
                    PrefabFile prefab = PrefabFile.Load(file);
                    foreach (var entry in prefab.Wire.Prefabs.Where(p => p.CarInit.Count > 0))
                    {
                        int block = 0;
                        foreach (var effect in entry.CarInit[0].ShaderEffects)
                        {
                            sb.AppendLine($"  shader effects block {block++}: stiffness {effect.BoneStiffness.ToString("0.###", CultureInfo.InvariantCulture)}, "
                                + $"guid {effect.SpzAndLightGuid.Part0:X8} {effect.SpzAndLightGuid.Part1:X8}, clone visuals {effect.FgsCloneVisuals.Count}");
                            sb.AppendLine($"    colour and dirt: {effect.ColorAndDirty.Count}");
                            foreach (var row in effect.ColorAndDirty)
                            {
                                ulong lowFirst = ((ulong)row.Guid.Part1 << 32) | row.Guid.Part0, highFirst = ((ulong)row.Guid.Part0 << 32) | row.Guid.Part1;
                                string which = MafiaMaterials.GetMaterialName(lowFirst) is { } a ? $"{a} (part0 low)" : MafiaMaterials.GetMaterialName(highFirst) is { } b ? $"{b} (part0 high)" : "?";
                                sb.AppendLine($"      {row.Guid.Part0:X8} {row.Guid.Part1:X8}  {which,-40} used {used.ContainsKey(lowFirst) || used.ContainsKey(highFirst),-5} "
                                    + $"texture 0x{row.TextureName:X16} {(textures.TryGetValue(row.TextureName, out string? t) ? t : "?")}  flags 0x{row.Flags:X}");
                            }
                            sb.AppendLine($"    deform materials: {effect.DeformMaterial.Count}");
                            foreach (var row in effect.DeformMaterial)
                            {
                                ulong lowFirst = ((ulong)row.Guid.Part1 << 32) | row.Guid.Part0, highFirst = ((ulong)row.Guid.Part0 << 32) | row.Guid.Part1;
                                string which = MafiaMaterials.GetMaterialName(lowFirst) is { } a ? $"{a} (part0 low)" : MafiaMaterials.GetMaterialName(highFirst) is { } b ? $"{b} (part0 high)" : "?";
                                sb.AppendLine($"      {row.Guid.Part0:X8} {row.Guid.Part1:X8}  {which,-40} used {used.ContainsKey(lowFirst) || used.ContainsKey(highFirst),-5} group {row.Group}");
                            }
                            foreach (ulong visual in effect.FgsCloneVisuals) sb.AppendLine($"    clone visual 0x{visual:X16} {Material(visual)}");
                        }
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
