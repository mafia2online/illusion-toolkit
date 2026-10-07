using Illusion.Assets.Materials;

namespace Illusion.Assets.Bridge;

/// <summary>
/// <see cref="IAuthoredMaterialHost"/> straight onto the material catalog, with no history: what a probe or
/// any other headless caller uses. The application's own host does the same edits through its undo stack.
/// </summary>
public sealed class CatalogAuthoredMaterials : IAuthoredMaterialHost
{
    private const string SpecularParameter = "D013"; // SpecularPowerAndLevel

    private readonly MafiaMaterialCatalog _catalog;
    private readonly string _library;

    public CatalogAuthoredMaterials(MafiaMaterialCatalog catalog, string library)
    {
        _catalog = catalog;
        _library = library;
    }

    /// <summary>The library new materials go to: default.mtl — the one every edition of the game loads, and
    /// where the file import puts the ones it creates — or the first loaded one.</summary>
    public static string? TargetLibrary(MafiaMaterialCatalog catalog)
    {
        IReadOnlyList<string> libraries = catalog.Libraries;
        return libraries.FirstOrDefault(l => l.Equals("default.mtl", StringComparison.OrdinalIgnoreCase))
            ?? libraries.FirstOrDefault();
    }

    public ulong? Create(AuthoredMaterial material)
    {
        ulong? hash = _catalog.CreateMaterial(_library, material.Name, material.NormalMapped);
        if (hash == null) return null;
        Apply(_catalog, hash.Value, material);
        return hash;
    }

    public bool Update(ulong hash, AuthoredMaterial material) => Apply(_catalog, hash, material);

    public ulong? Replace(ulong hash, AuthoredMaterial material) =>
        _catalog.RemoveMaterial(hash) == null ? null : Create(material);

    /// <summary>Writes the texture names and the specular values into a material that already has the
    /// slots for them. Shared with the application's host, which wraps it in history entries.</summary>
    public static bool Apply(MafiaMaterialCatalog catalog, ulong hash, AuthoredMaterial material)
    {
        if (!catalog.SetTexture(hash, "S000", material.Diffuse)) return false;
        if (material.NormalSpecular == null) return true;
        if (!catalog.SetTexture(hash, "S001", material.NormalSpecular)) return false;
        return catalog.SetParameter(hash, SpecularParameter, [material.SpecularPower, material.SpecularLevel]);
    }
}
