namespace Illusion.Assets.Bridge;

/// <summary>
/// Where <see cref="AuthoredMaterialResolver"/> sends its catalog edits. The resolver runs on the bridge
/// thread and knows nothing of undo; the application implements this on the thread that owns its history
/// and records each call there, and a headless caller uses <see cref="CatalogAuthoredMaterials"/>.
/// </summary>
public interface IAuthoredMaterialHost
{
    /// <summary>Creates the material. Null when it could not be created (name taken, no library).</summary>
    ulong? Create(AuthoredMaterial material);

    /// <summary>Brings an existing material of the SAME shape (plain, or normal-mapped) up to date: its
    /// texture names and specular values. False when a slot it needs is not there.</summary>
    bool Update(ulong hash, AuthoredMaterial material);

    /// <summary>Replaces a material whose shape changed — it gained or lost its normal map, which is a
    /// different shader. The name, and with it the hash every mesh refers to, stays the same.</summary>
    ulong? Replace(ulong hash, AuthoredMaterial material);
}
