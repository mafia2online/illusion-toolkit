using Illusion.Assets.Textures;
using Illusion.Formats.Archive;
using Illusion.Formats.Hashing;
using Illusion.Formats.ItemDesc;
using Illusion.Formats.Materials.Versions;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Sds;

/// <summary>
/// Carries what an object needs from its own archive BESIDES its scene frames into another archive's
/// working copy: the textures its materials name, the item descriptions its collision frames name, and the
/// prefab entry its actor's definition names.
///
/// <para>
/// None of the three is part of the frame resource, and each is found by a name the object already carries —
/// a texture by file name through the material library, an item description by the hash a collision frame
/// stores, a prefab entry by the FNV64 of the definition name. So the bytes travel verbatim and answer under
/// the same key on the other side; nothing is renamed, and what the destination already has under a key is
/// left alone and shared.
/// </para>
/// <para>
/// This writes the working copy at once, not at Save. Carried resources are inert until something names
/// them, so an import that is later undone leaves a few unused files behind rather than a scene that names
/// resources a discarded edit took away.
/// </para>
/// </summary>
public static class ArchiveCarry
{
    /// <summary>What a carry brought over, and what it could not find.</summary>
    /// <param name="Textures">Textures added to the destination.</param>
    /// <param name="ItemDescriptions">Item descriptions added to the destination, by hash.</param>
    /// <param name="Prefab">Whether a prefab entry was added.</param>
    /// <param name="Elsewhere">Textures neither archive carries — they live in an archive of their own that
    /// the game loads beside the source's, and may not be loaded where the destination is.</param>
    /// <param name="Unresolved">Collision hashes no item description of the source answers to.</param>
    public sealed record Report(
        IReadOnlyList<string> Textures, IReadOnlyList<ulong> ItemDescriptions, bool Prefab,
        IReadOnlyList<string> Elsewhere, IReadOnlyList<ulong> Unresolved);

    /// <summary>Carries the three kinds from one extracted folder to another.</summary>
    /// <param name="definition">The actor's definition name, or null/empty for an object no actor places.</param>
    public static Report Carry(string fromDir, string toDir, IReadOnlyCollection<ulong> materialHashes,
        IReadOnlyCollection<ulong> collisionHashes, string? definition)
    {
        ArgumentException.ThrowIfNullOrEmpty(fromDir);
        ArgumentException.ThrowIfNullOrEmpty(toDir);
        ArgumentNullException.ThrowIfNull(materialHashes);
        ArgumentNullException.ThrowIfNull(collisionHashes);

        SdsManifest from = SdsManifest.Load(fromDir);
        SdsManifest to = SdsManifest.Load(toDir);

        var textures = new List<string>();
        var elsewhere = new List<string>();
        CarryTextures(from, to, fromDir, toDir, materialHashes, textures, elsewhere);

        var descriptions = new List<ulong>();
        var unresolved = new List<ulong>();
        CarryItemDescriptions(from, to, fromDir, toDir, collisionHashes, descriptions, unresolved);

        bool prefab = !string.IsNullOrEmpty(definition) && CarryPrefab(from, to, fromDir, toDir, Fnv64.Hash(definition));
        return new Report(textures, descriptions, prefab, elsewhere, unresolved);
    }

    private static void CarryTextures(SdsManifest from, SdsManifest to, string fromDir, string toDir,
        IReadOnlyCollection<ulong> materialHashes, List<string> added, List<string> elsewhere)
    {
        MafiaMaterials.EnsureLoaded();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ulong hash in materialHashes)
        {
            IMaterial? material = MafiaMaterials.Collection?.FindByHash(hash);
            foreach (string texture in material?.CollectTextures() ?? [])
            {
                if (texture.Length > 0) wanted.Add(texture);
            }
        }

        foreach (string texture in wanted.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (to.HasFile(texture)) continue;
            if (!from.HasFile(texture))
            {
                elsewhere.Add(texture);
                continue;
            }
            if (!CopyEntry(from, to, fromDir, toDir, texture)) continue;
            added.Add(texture);
            TextureSearchIndex.Register(Path.Combine(toDir, texture));

            // A texture stored split keeps its top level in a companion entry, and its own entry says so —
            // one without the other is a chain the game streams and does not find.
            string companion = SdsImportTypes.MipNameFor(texture);
            if (from.HasFile(companion) && !to.HasFile(companion)) CopyEntry(from, to, fromDir, toDir, companion);
        }
    }

    private static void CarryItemDescriptions(SdsManifest from, SdsManifest to, string fromDir, string toDir,
        IReadOnlyCollection<ulong> collisionHashes, List<ulong> added, List<ulong> unresolved)
    {
        if (collisionHashes.Count == 0) return;
        Dictionary<ulong, string> theirs = ItemDescriptionsOf(from);
        Dictionary<ulong, string> ours = ItemDescriptionsOf(to);

        foreach (ulong hash in collisionHashes.Order())
        {
            if (ours.ContainsKey(hash)) continue;
            if (!theirs.TryGetValue(hash, out string? path))
            {
                unresolved.Add(hash);
                continue;
            }
            // Named by what it is keyed by: the destination's own are numbered by their place in ITS archive,
            // and a number picked here could collide with one a later extraction hands out.
            string name = $"ItemDesc_{hash:x16}.ids";
            if (!CopyEntry(from, to, fromDir, toDir, Path.GetFileName(path), name)) continue;
            ours[hash] = Path.Combine(toDir, name);
            added.Add(hash);
        }
    }

    /// <summary>Every item description an extracted archive lists, by the hash it is keyed under. One that
    /// cannot be read is left out — it cannot be matched either way.</summary>
    public static Dictionary<ulong, string> ItemDescriptionsOf(SdsManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var byHash = new Dictionary<ulong, string>();
        foreach (string path in manifest.GetFiles("ItemDesc"))
        {
            try
            {
                byHash.TryAdd(ItemDescFile.Load(path).Hash, path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                           or Formats.SdsFormatException)
            {
                // unreadable: not a description anything can be pointed at
            }
        }
        return byHash;
    }

    // The entry goes into the destination's first prefab container, or into a container of its own when the
    // destination has none — seven of the shipped districts do not.
    private static bool CarryPrefab(SdsManifest from, SdsManifest to, string fromDir, string toDir, ulong hash)
    {
        IReadOnlyList<string> ours = to.GetFiles("PREFAB");
        foreach (string path in ours)
        {
            if (PrefabFile.Load(path).Contains(hash)) return false;
        }

        foreach (string path in from.GetFiles("PREFAB"))
        {
            PrefabFile source = PrefabFile.Load(path);
            if (!source.Contains(hash)) continue;

            if (ours.Count > 0)
            {
                PrefabFile target = PrefabFile.Load(ours[0]);
                if (!target.Adopt(source, hash)) return false;
                AtomicFile.WriteAllBytes(ours[0], target.ToBytes());
                return true;
            }

            IReadOnlyList<(string Name, string Value)>? fields = from.EntryFields(Path.GetFileName(path));
            if (fields is not { Count: >= 3 } || !int.TryParse(fields[^1].Value, out int version)) return false;
            var fresh = new PrefabFile();
            if (!fresh.Adopt(source, hash)) return false;
            const string name = "PREFAB_carried.prf";
            AtomicFile.WriteAllBytes(Path.Combine(toDir, name), fresh.ToBytes());
            return to.AddEntry(fields[0].Value, name, version, [.. fields.Skip(2).Take(fields.Count - 3)]);
        }
        return false;
    }

    /// <summary>
    /// Copies one file and the manifest entry that announces it, field for field — a packing handler reads
    /// an entry positionally, so the only safe way to move one is verbatim.
    /// </summary>
    /// <param name="name">The entry's file name in the source manifest.</param>
    /// <param name="newName">The name it takes in the destination, or null to keep its own.</param>
    /// <returns>False when the destination already lists that name or the source does not have the entry.</returns>
    internal static bool CopyEntry(SdsManifest from, SdsManifest to, string fromDir, string toDir, string name,
        string? newName = null)
    {
        string target = newName ?? name;
        if (to.HasFile(target)) return false;
        IReadOnlyList<(string Name, string Value)>? fields = from.EntryFields(name);
        string source = Path.Combine(fromDir, name.TrimStart('/', '\\'));
        if (fields is not { Count: >= 3 } || !File.Exists(source)) return false;
        if (!int.TryParse(fields[^1].Value, out int version) || fields[^1].Name != "Version") return false;

        string targetPath = Path.Combine(toDir, target.TrimStart('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        AtomicFile.WriteAllBytes(targetPath, File.ReadAllBytes(source));
        return to.AddEntry(fields[0].Value, target, version, [.. fields.Skip(2).Take(fields.Count - 3)]);
    }
}
