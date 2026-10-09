namespace Illusion.Formats.Prefab;

/// <summary>
/// One row of the list a car's PREFAB tells the game to treat its materials by: which material, what is done
/// to it, and the texture that goes with it.
/// </summary>
/// <param name="Material">The material's 64-bit hash (the file keeps it as two halves, low first).</param>
/// <param name="Flags">What the row does. Read off the shipped cars: 1 on the body paint and the far level's
/// paint and on nothing else - the materials the game COLOURS; 2 on paint, chrome and glass, with the car's
/// burnt texture where it has one; 4 on the interior.</param>
/// <param name="Texture">FNV-64 of a texture's file name, or 0.</param>
public sealed record CarMaterialRow(ulong Material, uint Flags, ulong Texture)
{
    /// <summary>The row whose flag makes the game paint the material in the car's colour.</summary>
    public const uint Painted = 1;
}

/// <summary>A material the game deforms with the body, and the group it is deformed in.</summary>
public sealed record CarDeformMaterialRow(ulong Material, uint Group);

public sealed partial class PrefabFile
{
    private static ulong HashOf(Native.Model.PrefabGuidW guid) => ((ulong)guid.Part1 << 32) | guid.Part0;

    private static Native.Model.PrefabGuidW GuidOf(ulong hash) => new() { Part0 = (uint)hash, Part1 = (uint)(hash >> 32) };

    /// <summary>
    /// The materials the game colours, dirties and burns on this car. A material that is not here is drawn
    /// as its library defines it and nothing more: a copy of the paint under a new name stays UNPAINTED until
    /// it has the rows the paint has.
    /// </summary>
    public IReadOnlyList<CarMaterialRow> CarMaterialRows =>
        Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is { } entry
            ? [.. entry.CarInit[0].ShaderEffects.SelectMany(e => e.ColorAndDirty).Select(r => new CarMaterialRow(HashOf(r.Guid), r.Flags, r.TextureName))]
            : [];

    /// <summary>The materials the game deforms with the body.</summary>
    public IReadOnlyList<CarDeformMaterialRow> CarDeformMaterialRows =>
        Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is { } entry
            ? [.. entry.CarInit[0].ShaderEffects.SelectMany(e => e.DeformMaterial).Select(r => new CarDeformMaterialRow(HashOf(r.Guid), r.Group))]
            : [];

    /// <summary>
    /// Gives a material every row another material has - in the list the game colours and dirties by and in
    /// the list it deforms by - each new row standing right after the one it mirrors. Rows the material
    /// already has are not written twice.
    /// </summary>
    /// <returns>How many rows were added; 0 when <paramref name="like"/> has none or all were there.</returns>
    public int AdoptCarMaterial(ulong like, ulong material)
    {
        if (like == material || Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is not { } entry) return 0;
        int added = 0;
        foreach (Native.Model.PrefabShaderEffectInitW effect in entry.CarInit[0].ShaderEffects)
        {
            for (int i = 0; i < effect.ColorAndDirty.Count; i++)
            {
                Native.Model.PrefabColorAndDirtyW row = effect.ColorAndDirty[i];
                if (HashOf(row.Guid) != like) continue;
                if (effect.ColorAndDirty.Any(r => HashOf(r.Guid) == material && r.Flags == row.Flags && r.TextureName == row.TextureName)) continue;
                effect.ColorAndDirty.Insert(++i, new Native.Model.PrefabColorAndDirtyW { Guid = GuidOf(material), TextureName = row.TextureName, Flags = row.Flags });
                added++;
            }
            for (int i = 0; i < effect.DeformMaterial.Count; i++)
            {
                Native.Model.PrefabDeformMaterialW row = effect.DeformMaterial[i];
                if (HashOf(row.Guid) != like) continue;
                if (effect.DeformMaterial.Any(r => HashOf(r.Guid) == material && r.Group == row.Group)) continue;
                effect.DeformMaterial.Insert(++i, new Native.Model.PrefabDeformMaterialW { Guid = GuidOf(material), Group = row.Group });
                added++;
            }
        }
        return added;
    }

    /// <summary>Takes every row of a material out of both lists. Returns how many went.</summary>
    public int DropCarMaterial(ulong material)
    {
        if (Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is not { } entry) return 0;
        int removed = 0;
        foreach (Native.Model.PrefabShaderEffectInitW effect in entry.CarInit[0].ShaderEffects)
        {
            removed += effect.ColorAndDirty.RemoveAll(r => HashOf(r.Guid) == material);
            removed += effect.DeformMaterial.RemoveAll(r => HashOf(r.Guid) == material);
        }
        return removed;
    }
}
