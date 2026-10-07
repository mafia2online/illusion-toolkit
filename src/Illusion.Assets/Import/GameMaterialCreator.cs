using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;

namespace Illusion.Assets.Import;

/// <summary>
/// Creates game materials for import: each missing name becomes a default-preset material (the plain
/// diffuse shader with an empty S000 texture slot — binding textures stays with the modder) appended to
/// the game's MTL library. Writing touches a file the WHOLE game reads (edit\materials\default.mtl), so
/// the previous version is always preserved in a timestamped <c>backups</c> folder beside it — the same
/// discipline as .sds builds.
/// </summary>
public static class GameMaterialCreator
{
    /// <summary>
    /// Adds a default-preset material named <paramref name="name"/> to <paramref name="library"/>.
    /// Returns null when the name (or its hash) is already taken. The material dictionary is swapped
    /// copy-on-write, so concurrent readers (background mesh loaders) never observe a half-mutated map.
    /// </summary>
    public static IMaterial? AddDefault(MaterialLibrary library, string name) =>
        Add(library, name, MaterialPreset.Default);

    /// <summary>The same, from any preset — how a material that arrives with a normal map gets the shader
    /// that reads one.</summary>
    public static IMaterial? Add(MaterialLibrary library, string name, MaterialPreset preset)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (string.IsNullOrWhiteSpace(name)) return null;

        IMaterial material = MaterialFactory.ConstructMaterial(library.Version);
        material.SetupFromPreset(preset);
        material.SetName(name);
        if (library.Materials.ContainsKey(material.GetMaterialHash())) return null;

        var swapped = new Dictionary<ulong, IMaterial>(library.Materials)
        {
            [material.GetMaterialHash()] = material,
        };
        library.Materials = swapped;
        return material;
    }

    /// <summary>Backs the library file up (timestamped, kept forever) and atomically rewrites it from
    /// the in-memory library. Returns the backup path, or null when the file did not exist before.</summary>
    public static string? BackupAndWrite(MaterialLibrary library, string path)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(path);

        string? backup = null;
        if (File.Exists(path))
        {
            string dir = Path.Combine(Path.GetDirectoryName(path) ?? ".", "backups");
            Directory.CreateDirectory(dir);
            string stem = Path.GetFileNameWithoutExtension(path);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            backup = Path.Combine(dir, $"{stem}_{stamp}.mtl");
            for (int n = 2; File.Exists(backup); n++)
                backup = Path.Combine(dir, $"{stem}_{stamp}_{n}.mtl");
            File.Copy(path, backup);
        }

        string temp = path + ".tmp";
        library.WriteMatFile(temp);
        File.Move(temp, path, overwrite: true);
        library.Name = path; // WriteMatFile stamped the temp path into the library's identity
        return backup;
    }

    /// <summary>
    /// Creates every material in <paramref name="names"/> that the loaded libraries do not already have
    /// and persists the target library. Returns null on success (with the created count and backup
    /// path), else the reason nothing was written.
    /// </summary>
    public static string? CreateMissing(IEnumerable<string> names, out int created, out string? backupPath)
    {
        created = 0;
        backupPath = null;
        MaterialCollection? collection = MafiaMaterials.Collection;
        if (collection == null || collection.Libraries.Count == 0)
            return "no MTL libraries are loaded (is the game folder configured?)";

        // default.mtl is the base library every game edition loads — the natural home for new names.
        string? targetPath = null;
        MaterialLibrary? target = null;
        foreach ((string path, MaterialLibrary library) in collection.Libraries)
        {
            if (targetPath == null
                || string.Equals(Path.GetFileName(path), "default.mtl", StringComparison.OrdinalIgnoreCase))
            {
                targetPath = path;
                target = library;
            }
        }
        if (target == null || targetPath == null) return "no MTL library to write to";

        foreach (string name in names)
        {
            if (MafiaMaterials.FindHashByName(name) != null) continue; // appeared meanwhile — fine
            if (AddDefault(target, name) != null) created++;
        }
        if (created == 0) return null; // nothing missing anymore — no write, no backup

        try
        {
            backupPath = BackupAndWrite(target, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "could not write " + Path.GetFileName(targetPath) + " — " + ex.Message;
        }
        return null;
    }
}
