using System.Globalization;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

/// <summary>
/// Which of a car's materials the game colours, dirties, burns and deforms.
///
/// <para>
/// A mesh names its materials; what the game DOES to a material on a car is said elsewhere - in two lists of
/// the car's PREFAB, by the material's hash. The body paint and the far level's paint carry a row that makes
/// the game paint them in the car's colour; paint, chrome and glass carry rows with the car's burnt texture;
/// the interior a row of its own; paint, chrome and glass are also named as what deforms with the body.
/// </para>
/// <para>
/// So a car's OWN material - a copy of the paint with the car's own normal map - is not painted until it has
/// the rows the paint has: seen in the game as a body of bare primer grey whatever colour it was given. One
/// call of <see cref="Adopt"/> gives a material the rows of the one it was copied from.
/// </para>
/// </summary>
public static class CarMaterials
{
    /// <summary>One row of the colour-and-dirt list, as a person reads it.</summary>
    public sealed record Row(string Material, string Hash, int Flags, string What, string Texture, bool OnTheCar);

    /// <summary>One row of the deform list.</summary>
    public sealed record DeformRow(string Material, string Hash, int Group, bool OnTheCar);

    /// <summary>The two lists, and the materials the car's meshes name that neither list has.</summary>
    public sealed record Sheet(string Car, IReadOnlyList<Row> Rows, IReadOnlyList<DeformRow> Deform, IReadOnlyList<string> Unlisted);

    private static string Name(ulong hash) => MafiaMaterials.GetMaterialName(hash) ?? "0x" + hash.ToString("X16", CultureInfo.InvariantCulture);

    private static string What(uint flags) => flags switch
    {
        CarMaterialRow.Painted => "painted in the car's colour",
        2 => "dirt and burning",
        4 => "interior",
        _ => "?",
    };

    private static string? Folder(FileInfo car, out string extracted)
    {
        extracted = "";
        ArgumentNullException.ThrowIfNull(car);
        if (!car.Exists) return $"no such archive: {car.FullName}";
        extracted = SdsMeshLoader.EnsureExtracted(car);
        return null;
    }

    private static string? Open(string extracted, out string label, out string prefabPath, out PrefabFile? prefab, out HashSet<ulong> used, out Dictionary<ulong, string> textures)
    {
        prefabPath = "";
        prefab = null;
        used = [];
        textures = [];
        ArgumentException.ThrowIfNullOrEmpty(extracted);
        label = Path.GetFileName(extracted.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        SdsManifest manifest = SdsManifest.Load(extracted);
        IReadOnlyList<string> prefabs = manifest.GetFiles("PREFAB");
        if (prefabs.Count == 0) return $"{label} has no PREFAB";
        prefabPath = prefabs[0];
        byte[] bytes = File.ReadAllBytes(prefabPath);
        prefab = PrefabFile.Load(prefabPath);
        if (prefab.Car == null) return $"{label}'s PREFAB holds no car";
        if (!prefab.ToBytes().AsSpan().SequenceEqual(bytes)) return $"{label}'s PREFAB is not written back the way it was read - it is left alone";
        MafiaMaterials.EnsureLoaded();
        foreach (FrameObjectSingleMesh mesh in SdsMeshLoader.OpenScene(extracted).FrameResource?.FrameObjects?.Values.OfType<FrameObjectSingleMesh>() ?? [])
        {
            foreach (var level in mesh.Material?.Materials ?? [])
            {
                foreach (var slot in level ?? []) used.Add(slot.MaterialHash);
            }
        }
        foreach (string file in manifest.GetFiles("Texture")) textures[Fnv64.Hash(Path.GetFileName(file))] = Path.GetFileName(file);
        return null;
    }

    private static Sheet Describe(string label, PrefabFile prefab, HashSet<ulong> used, Dictionary<ulong, string> textures)
    {
        IReadOnlyList<CarMaterialRow> rows = prefab.CarMaterialRows;
        IReadOnlyList<CarDeformMaterialRow> deform = prefab.CarDeformMaterialRows;
        var listed = rows.Select(r => r.Material).Concat(deform.Select(r => r.Material)).ToHashSet();
        return new Sheet(label,
            [.. rows.Select(r => new Row(Name(r.Material), "0x" + r.Material.ToString("X16", CultureInfo.InvariantCulture), (int)r.Flags, What(r.Flags),
                r.Texture == 0 ? "" : textures.TryGetValue(r.Texture, out string? t) ? t : "0x" + r.Texture.ToString("X16", CultureInfo.InvariantCulture), used.Contains(r.Material)))],
            [.. deform.Select(r => new DeformRow(Name(r.Material), "0x" + r.Material.ToString("X16", CultureInfo.InvariantCulture), (int)r.Group, used.Contains(r.Material)))],
            [.. used.Where(h => h != 0 && !listed.Contains(h)).Select(Name).Order(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>Reads the two lists.</summary>
    public static string? Read(FileInfo car, out Sheet? sheet)
    {
        sheet = null;
        return Folder(car, out string extracted) ?? ReadIn(extracted, out sheet);
    }

    /// <summary>Reads the two lists of a working copy in any folder.</summary>
    public static string? ReadIn(string extracted, out Sheet? sheet)
    {
        sheet = null;
        if (Open(extracted, out string label, out _, out PrefabFile? prefab, out HashSet<ulong> used, out Dictionary<ulong, string> textures) is { } refused) return refused;
        sheet = Describe(label, prefab!, used, textures);
        return null;
    }

    private static string? Hash(string asked, out ulong hash)
    {
        string wanted = (asked ?? "").Trim();
        hash = 0;
        if (wanted.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(wanted[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash))
        {
            return null;
        }
        if (MafiaMaterials.FindHashByName(wanted) is { } found)
        {
            hash = found;
            return null;
        }
        return $"no material named '{wanted}'";
    }

    /// <summary>
    /// Gives <paramref name="material"/> every row <paramref name="like"/> has in the car's two lists, so the
    /// game treats it the way it treats the other: paints it, dirties it, burns it, deforms it.
    /// </summary>
    /// <param name="added">How many rows were written; 0 when it had them all.</param>
    public static string? Adopt(FileInfo car, string material, string like, out int added, out Sheet? sheet)
    {
        added = 0;
        sheet = null;
        return Folder(car, out string extracted) ?? AdoptIn(extracted, material, like, out added, out sheet);
    }

    /// <inheritdoc cref="Adopt(FileInfo, string, string, out int, out Sheet?)"/>
    public static string? AdoptIn(string extracted, string material, string like, out int added, out Sheet? sheet)
    {
        added = 0;
        sheet = null;
        if (Open(extracted, out string label, out string path, out PrefabFile? prefab, out HashSet<ulong> used, out Dictionary<ulong, string> textures) is { } refused) return refused;
        if (Hash(material, out ulong mine) is { } noMaterial) return noMaterial;
        if (Hash(like, out ulong theirs) is { } noLike) return noLike;
        if (mine == theirs) return "the material and the one it is treated like are the same";
        if (!prefab!.CarMaterialRows.Any(r => r.Material == theirs) && !prefab.CarDeformMaterialRows.Any(r => r.Material == theirs))
        {
            return $"'{Name(theirs)}' has no row in this car's lists - there is nothing to be treated like";
        }
        added = prefab.AdoptCarMaterial(theirs, mine);
        if (added > 0) AtomicFile.WriteAllBytes(path, prefab.ToBytes());
        sheet = Describe(label, prefab, used, textures);
        return null;
    }

    /// <summary>Takes a material's rows out of both lists.</summary>
    public static string? Drop(FileInfo car, string material, out int removed, out Sheet? sheet)
    {
        removed = 0;
        sheet = null;
        return Folder(car, out string extracted) ?? DropIn(extracted, material, out removed, out sheet);
    }

    /// <inheritdoc cref="Drop(FileInfo, string, out int, out Sheet?)"/>
    public static string? DropIn(string extracted, string material, out int removed, out Sheet? sheet)
    {
        removed = 0;
        sheet = null;
        if (Open(extracted, out string label, out string path, out PrefabFile? prefab, out HashSet<ulong> used, out Dictionary<ulong, string> textures) is { } refused) return refused;
        if (Hash(material, out ulong mine) is { } noMaterial) return noMaterial;
        removed = prefab!.DropCarMaterial(mine);
        if (removed > 0) AtomicFile.WriteAllBytes(path, prefab.ToBytes());
        sheet = Describe(label, prefab, used, textures);
        return null;
    }
}
