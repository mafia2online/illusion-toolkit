using Illusion.Assets.Materials;
using Illusion.Assets.Sds;
using Illusion.Assets.Textures;
using Illusion.Domain.Materials;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;

namespace Illusion.Assets.Cars;

/// <summary>
/// A car's winter twin, made from the summer car.
///
/// <para>
/// The game keeps a car twice: <c>name.sds</c> and <c>name_z.sds</c>, the one it loads when the city is under
/// snow. Read off the shipped pairs, the two hold the SAME model - the same vertex and index buffers, the same
/// prefab, tuning and collision shapes, byte for byte - and differ in three things only: some material slots
/// of the meshes name a winter material instead of the summer one (the paint, the chrome, the glass: the ones
/// with snow and frost in them), the textures those materials need replace the summer ones in the archive, and
/// the effects file.
/// </para>
/// <para>
/// So a car that was reshaped in its summer archive gets its winter twin by copying, not by doing the work
/// twice: the summer working copy whole, then the three differences, learned from the pair of the car it was
/// made from. A material the reference car does not have at all - the car's own - stays as it is, unless the
/// library holds a material of its name with the game's winter suffix (<see cref="WinterSuffix"/>): that is
/// its winter twin, and it is swapped for it.
/// </para>
/// <para>
/// The new copy is built in a folder BESIDE the winter working copy and takes its place only when it is whole:
/// a refusal or a failed write leaves the winter copy exactly as it was.
/// </para>
/// </summary>
public static class CarWinter
{
    /// <summary>What the game adds to a material's name for its winter twin.</summary>
    public const string WinterSuffix = "^zima";

    /// <summary>A material swapped for winter: both names (or hashes as text) and how many slots carry it.</summary>
    public sealed record Swap(string Summer, string Winter, int Slots);

    /// <summary>What <see cref="Sync"/> did.</summary>
    /// <param name="TexturesAdded">Winter textures brought in from the reference's winter copy.</param>
    /// <param name="TexturesRemoved">Summer textures taken out: the reference's winter copy has none, and no
    /// material of the winter car names them.</param>
    /// <param name="TexturesKept">Summer textures the reference's winter copy drops but a material of this
    /// winter car still names - left in.</param>
    /// <param name="TexturesCarried">Textures only the OLD winter copy had that a material of the new one
    /// names (a winter material made into the winter archive) - carried over.</param>
    public sealed record Result(string Winter, int FilesCopied, IReadOnlyList<Swap> Materials,
        IReadOnlyList<string> TexturesAdded, IReadOnlyList<string> TexturesRemoved, IReadOnlyList<string> TexturesKept,
        IReadOnlyList<string> TexturesCarried, bool EffectsReplaced);

    /// <summary>The winter archive that belongs to a summer one: <c>name_z.sds</c> beside it.</summary>
    public static FileInfo TwinOf(FileInfo summer)
    {
        ArgumentNullException.ThrowIfNull(summer);
        return new FileInfo(Path.Combine(summer.DirectoryName ?? "", Path.GetFileNameWithoutExtension(summer.Name) + "_z.sds"));
    }

    /// <summary>
    /// Which winter material stands where each summer material stands, read off a pair of archives that hold
    /// the same model: slot by slot, mesh by mesh, level by level.
    /// </summary>
    /// <returns>Null with a <paramref name="refusal"/> when the two are not the same model, or when one summer
    /// material is swapped for two different winter ones.</returns>
    public static Dictionary<ulong, ulong>? Mapping(FrameResource summer, FrameResource winter, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(summer);
        ArgumentNullException.ThrowIfNull(winter);
        refusal = null;
        var map = new Dictionary<ulong, ulong>();
        Dictionary<string, FrameObjectSingleMesh> theirs = Meshes(winter).ToDictionary(m => m.Key, m => m.Mesh, StringComparer.Ordinal);
        List<(string Key, FrameObjectSingleMesh Mesh)> mine = Meshes(summer);
        if (mine.Count != theirs.Count)
        {
            refusal = $"{mine.Count} mesh(es) in summer and {theirs.Count} in winter - the two are not the same model";
            return null;
        }
        foreach ((string key, FrameObjectSingleMesh mesh) in mine)
        {
            if (!theirs.TryGetValue(key, out FrameObjectSingleMesh? twin))
            {
                refusal = $"the winter archive has no mesh '{key}' - the two are not the same model";
                return null;
            }
            List<MaterialStruct[]> a = mesh.Material?.Materials ?? [], b = twin.Material?.Materials ?? [];
            if (a.Count != b.Count)
            {
                refusal = $"'{key}' has {a.Count} level(s) in summer and {b.Count} in winter - the two are not the same model";
                return null;
            }
            for (int lod = 0; lod < a.Count; lod++)
            {
                if ((a[lod]?.Length ?? 0) != (b[lod]?.Length ?? 0))
                {
                    refusal = $"'{key}', level {lod}: {a[lod]?.Length ?? 0} material slot(s) in summer and {b[lod]?.Length ?? 0} in winter - the two are not the same model";
                    return null;
                }
                for (int slot = 0; slot < (a[lod]?.Length ?? 0); slot++)
                {
                    ulong from = a[lod][slot].MaterialHash, to = b[lod][slot].MaterialHash;
                    if (map.TryGetValue(from, out ulong known) && known != to)
                    {
                        refusal = $"material 0x{from:X16} is 0x{known:X16} in one winter slot and 0x{to:X16} in another - no one swap describes the pair";
                        return null;
                    }
                    map[from] = to;
                }
            }
        }
        foreach (ulong same in map.Where(p => p.Key == p.Value).Select(p => p.Key).ToList()) map.Remove(same);
        return map;
    }

    /// <summary>Re-points every slot of every mesh whose material the mapping swaps. Returns how many slots
    /// each swap took.</summary>
    public static Dictionary<ulong, int> Apply(FrameResource frame, IReadOnlyDictionary<ulong, ulong> mapping)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(mapping);
        var done = new Dictionary<ulong, int>();
        foreach (MaterialStruct slot in Slots(frame))
        {
            if (!mapping.TryGetValue(slot.MaterialHash, out ulong winter)) continue;
            done[slot.MaterialHash] = done.GetValueOrDefault(slot.MaterialHash) + 1;
            slot.MaterialHash = winter;
        }
        return done;
    }

    /// <summary>
    /// Makes the car's winter twin again from its summer working copy: every file of the summer copy, then the
    /// winter materials, textures and effects the <paramref name="reference"/> car's own pair shows. The winter
    /// working copy is REPLACED - whatever was edited there by hand is gone, except textures only it had that
    /// a winter material still names.
    /// </summary>
    /// <param name="car">The summer archive.</param>
    /// <param name="reference">The summer archive of the car this one was made from - a shipped car whose
    /// winter twin is beside it.</param>
    /// <returns>Null on success; otherwise why not - and the winter working copy is as it was.</returns>
    public static string? Sync(FileInfo car, FileInfo reference, out Result? result)
    {
        ArgumentNullException.ThrowIfNull(car);
        ArgumentNullException.ThrowIfNull(reference);
        result = null;
        FileInfo winter = TwinOf(car), referenceWinter = TwinOf(reference);
        foreach (FileInfo needed in new[] { car, winter, reference, referenceWinter })
        {
            if (!needed.Exists) return $"no such archive: {needed.FullName}";
        }
        if (string.Equals(car.FullName, reference.FullName, StringComparison.OrdinalIgnoreCase)) return "the reference is the car this one was made from, not the car itself";
        return SyncFolders(SdsMeshLoader.EnsureExtracted(car), SdsMeshLoader.EnsureExtracted(winter),
            SdsMeshLoader.EnsureExtracted(reference), SdsMeshLoader.EnsureExtracted(referenceWinter), winter.Name, out result);
    }

    /// <summary><see cref="Sync"/> on four working-copy folders, wherever they stand: the summer car's, its
    /// winter twin's (replaced), and the reference pair's (read).</summary>
    public static string? SyncFolders(string carDir, string winterDir, string refDir, string refWinterDir, string winterName, out Result? result)
    {
        result = null;
        string staging = winterDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".new";
        string retired = winterDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".old";
        try
        {
            // ---- what the reference pair says winter is
            if (SdsMeshLoader.OpenScene(refDir).FrameResource is not { } refFrame || SdsMeshLoader.OpenScene(refWinterDir).FrameResource is not { } refWinterFrame)
            {
                return "the reference pair has no scene to read";
            }
            Dictionary<ulong, ulong>? mapping = Mapping(refFrame, refWinterFrame, out string? refusal);
            if (mapping == null) return $"the reference pair: {refusal}";
            HashSet<ulong> referenceMaterials = [.. Slots(refFrame).Select(slot => slot.MaterialHash)];
            string[] refTextures = Textures(refDir), refWinterTextures = Textures(refWinterDir);
            string[] gone = [.. refTextures.Except(refWinterTextures, StringComparer.OrdinalIgnoreCase)];
            string[] come = [.. refWinterTextures.Except(refTextures, StringComparer.OrdinalIgnoreCase)];
            byte[]? effects = Effects(refWinterDir) is { } winterEffects && Effects(refDir) is { } summerEffects
                && !File.ReadAllBytes(winterEffects).AsSpan().SequenceEqual(File.ReadAllBytes(summerEffects))
                    ? File.ReadAllBytes(winterEffects)
                    : null;

            // ---- the summer car, whole, in a folder of its own beside the winter copy
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            int copied = 0;
            foreach (string file in Directory.GetFiles(carDir, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(staging, Path.GetRelativePath(carDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                copied++;
            }
            if (SdsMeshLoader.OpenScene(staging).FrameResource is not { } frame)
            {
                Directory.Delete(staging, recursive: true);
                return "the summer working copy has no scene";
            }

            // ---- the materials. The car's OWN - ones the reference car does not have at all - go to the
            // material of their name with the game's winter suffix, when the library has one.
            MafiaMaterials.EnsureLoaded();
            foreach (ulong own in Slots(frame).Select(slot => slot.MaterialHash).Distinct().ToList())
            {
                if (mapping.ContainsKey(own) || referenceMaterials.Contains(own)) continue;
                if (MafiaMaterials.GetMaterialName(own) is { Length: > 0 } name && MafiaMaterials.FindHashByName(name + WinterSuffix) is { } twin)
                {
                    mapping[own] = twin;
                }
            }
            Dictionary<ulong, int> swapped = Apply(frame, mapping);
            string scene = SdsManifest.Load(staging).GetFiles("FrameResource").FirstOrDefault()
                ?? throw new InvalidDataException("the summer working copy lists no frame resource");
            AtomicFile.WriteAllBytes(scene, frame.WriteToStream());

            // ---- the textures: what the winter car's materials name decides what stays
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ulong material in Slots(frame).Select(slot => slot.MaterialHash).Distinct())
            {
                foreach (MaterialSlotInfo sampler in MafiaMaterialCatalog.Instance.GetMaterial(material)?.TextureSlots ?? [])
                {
                    if (!string.IsNullOrEmpty(sampler.TextureName)) named.Add(sampler.TextureName);
                }
            }
            var removed = new List<string>();
            var kept = new List<string>();
            foreach (string name in gone)
            {
                if (!ArchiveTextureWriter.Read(staging, name).Exists) continue;
                if (named.Contains(name))
                {
                    kept.Add(name);
                    continue;
                }
                ArchiveTextureWriter.Remove(staging, name);
                removed.Add(name);
            }
            var carried = new List<string>();
            var broughtFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);        // texture -> the folder it came from
            foreach (string name in come)
            {
                ArchiveTextureWriter.Restore(staging, name, ArchiveTextureWriter.Read(refWinterDir, name));
                broughtFrom[name] = refWinterDir;
            }
            if (Directory.Exists(winterDir) && File.Exists(Path.Combine(winterDir, "SDSContent.xml")))
            {
                foreach (string name in Textures(winterDir))
                {
                    if (!named.Contains(name) || ArchiveTextureWriter.Read(staging, name).Exists) continue;
                    ArchiveTextureWriter.Restore(staging, name, ArchiveTextureWriter.Read(winterDir, name));
                    broughtFrom[name] = winterDir;
                    carried.Add(name);
                }
            }

            // ---- what each brought texture stated for the packer where it came from
            if (SdsMemoryRequirements.Load(staging) is { } stated)
            {
                foreach ((string name, string folder) in broughtFrom)
                {
                    if (SdsMemoryRequirements.Load(folder) is not { } theirs) continue;
                    foreach (string key in new[] { SdsMemoryRequirements.Key("Texture", name, 0), SdsMemoryRequirements.Key("Mipmap", "MIP_" + name, 0) })
                    {
                        if (theirs.TryGet(key, out SdsMemoryRequirement figures)) stated.Set(key, figures);
                    }
                }
                stated.Save(staging);
            }

            bool effectsReplaced = false;
            if (effects != null && Effects(staging) is { } effectsFile)
            {
                AtomicFile.WriteAllBytes(effectsFile, effects);
                effectsReplaced = true;
            }

            // ---- and only now the winter copy gives way: aside, the new one in, the old one gone
            if (Directory.Exists(retired)) Directory.Delete(retired, recursive: true);
            bool hadWinter = Directory.Exists(winterDir);
            if (hadWinter) Directory.Move(winterDir, retired);
            try
            {
                Directory.Move(staging, winterDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (hadWinter) Directory.Move(retired, winterDir);
                throw;
            }
            try
            {
                if (hadWinter) Directory.Delete(retired, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The new copy is in place; the old one could not be cleared away and is left beside it.
            }
            TextureSearchIndex.RegisterFolder(winterDir);

            string Named(ulong hash) => MafiaMaterials.GetMaterialName(hash) ?? $"0x{hash:X16}";
            result = new Result(winterName, copied,
                [.. swapped.OrderBy(p => Named(p.Key), StringComparer.OrdinalIgnoreCase).Select(p => new Swap(Named(p.Key), Named(mapping[p.Key]), p.Value))],
                [.. come.Order(StringComparer.OrdinalIgnoreCase)], [.. removed.Order(StringComparer.OrdinalIgnoreCase)],
                [.. kept.Order(StringComparer.OrdinalIgnoreCase)], [.. carried.Order(StringComparer.OrdinalIgnoreCase)], effectsReplaced);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // nothing more to do about a folder that will not go; the winter copy itself is untouched
            }
            return Directory.Exists(winterDir)
                ? $"the winter working copy is as it was - the new one could not be made: {ex.Message}"
                : $"the new winter working copy could not be put in place: {ex.Message} (the old one is in {retired})";
        }
    }

    // Every mesh of a scene under a key that tells two of one name apart: the name, and which of that name it is.
    private static List<(string Key, FrameObjectSingleMesh Mesh)> Meshes(FrameResource frame)
    {
        var meshes = new List<(string, FrameObjectSingleMesh)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (FrameObjectSingleMesh mesh in frame.FrameObjects?.Values.OfType<FrameObjectSingleMesh>() ?? [])
        {
            string name = mesh.Name?.String ?? "";
            int nth = seen.GetValueOrDefault(name);
            seen[name] = nth + 1;
            meshes.Add((nth == 0 ? name : $"{name} #{nth + 1}", mesh));
        }
        return meshes;
    }

    private static IEnumerable<MaterialStruct> Slots(FrameResource frame)
    {
        foreach (FrameObjectSingleMesh mesh in frame.FrameObjects?.Values.OfType<FrameObjectSingleMesh>() ?? [])
        {
            foreach (MaterialStruct[] level in mesh.Material?.Materials ?? [])
            {
                foreach (MaterialStruct slot in level ?? []) yield return slot;
            }
        }
    }

    private static string[] Textures(string extracted) =>
        [.. SdsManifest.Load(extracted).GetFiles("Texture").Select(file => Path.GetFileName(file))];

    private static string? Effects(string extracted) => SdsManifest.Load(extracted).GetFiles("Effects").FirstOrDefault();
}
