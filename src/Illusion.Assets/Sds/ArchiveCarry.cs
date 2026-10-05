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
/// This writes the working copy at once, not at Save — the viewport looks a texture up the moment it builds
/// a mesh. So an import that is undone, or a scene closed without saving, leaves textures nothing uses. Every
/// texture carried is therefore written down beside the working copy (<see cref="RegisterName"/>), and each
/// save of the scene sweeps the ones no material of it names any more (<see cref="SweepUnused"/>). Only
/// what this class brought is ever swept: an archive's own textures may be named by things a scene does not
/// show (effects, decals, scripts) and are never touched.
/// </para>
/// <para>
/// Swept is not deleted. The import that brought a texture can still be redone, and a deleted object's
/// delete undone — neither carries anything a second time — so a swept texture is taken out of the manifest
/// and parked in <see cref="ParkedFolder"/>, and the next sweep that finds the scene naming it again puts it
/// back. What an earlier run of the program parked has no undo stack left to return to and is dropped then.
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
        IReadOnlyCollection<ulong> collisionHashes, string? definition) =>
        Carry(fromDir, toDir, materialHashes, collisionHashes, definition, directTextures: []);

    /// <summary>The same, with the textures the object's meshes name themselves rather than through a
    /// material — the occlusion map a mesh carries by name (<c>OMTextureHash</c>). The copy keeps the name,
    /// so the file has to come along like any other.</summary>
    public static Report Carry(string fromDir, string toDir, IReadOnlyCollection<ulong> materialHashes,
        IReadOnlyCollection<ulong> collisionHashes, string? definition, IReadOnlyCollection<string> directTextures)
    {
        ArgumentNullException.ThrowIfNull(directTextures);
        ArgumentException.ThrowIfNullOrEmpty(fromDir);
        ArgumentException.ThrowIfNullOrEmpty(toDir);
        ArgumentNullException.ThrowIfNull(materialHashes);
        ArgumentNullException.ThrowIfNull(collisionHashes);

        SdsManifest from = SdsManifest.Load(fromDir);
        SdsManifest to = SdsManifest.Load(toDir);

        var textures = new List<string>();
        var elsewhere = new List<string>();
        CarryTextures(from, to, fromDir, toDir, materialHashes, directTextures, textures, elsewhere);

        var descriptions = new List<ulong>();
        var unresolved = new List<ulong>();
        CarryItemDescriptions(from, to, fromDir, toDir, collisionHashes, descriptions, unresolved);

        bool prefab = !string.IsNullOrEmpty(definition) && CarryPrefab(from, to, fromDir, toDir, Fnv64.Hash(definition));
        return new Report(textures, descriptions, prefab, elsewhere, unresolved);
    }

    private static void CarryTextures(SdsManifest from, SdsManifest to, string fromDir, string toDir,
        IReadOnlyCollection<ulong> materialHashes, IReadOnlyCollection<string> directTextures, List<string> added,
        List<string> elsewhere)
    {
        MafiaMaterials.EnsureLoaded();
        var wanted = new HashSet<string>(directTextures.Where(t => !string.IsNullOrWhiteSpace(t)),
            StringComparer.OrdinalIgnoreCase);
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
            // A texture stored split keeps its top level in a companion entry, and its own entry says so —
            // one without the other is a chain the game streams and does not find. So the companion goes
            // FIRST, and a texture whose companion cannot be brought is not brought either.
            string companion = SdsImportTypes.MipNameFor(texture);
            bool withCompanion = from.HasFile(companion) && !to.HasFile(companion);
            if (withCompanion && !CopyEntry(from, to, fromDir, toDir, companion))
            {
                elsewhere.Add(texture);
                continue;
            }
            if (!CopyEntry(from, to, fromDir, toDir, texture))
            {
                if (withCompanion && to.RemoveEntry(companion)) File.Delete(Path.Combine(toDir, companion));
                elsewhere.Add(texture);
                continue;
            }
            added.Add(texture);
            Remember(toDir, texture);
            TextureSearchIndex.Register(Path.Combine(toDir, texture));
        }
    }

    /// <summary>What a working copy's lists held before a carry, so that a carry whose import then fails can
    /// be taken back whole (<see cref="TakeBack"/>).</summary>
    public sealed class Before
    {
        internal Before(string dir, byte[] manifest, HashSet<string> files, Dictionary<string, byte[]> prefabs, byte[]? register)
        {
            Dir = dir;
            Manifest = manifest;
            Files = files;
            Prefabs = prefabs;
            Register = register;
        }

        internal string Dir { get; }
        internal byte[] Manifest { get; }
        internal HashSet<string> Files { get; }
        internal Dictionary<string, byte[]> Prefabs { get; }
        internal byte[]? Register { get; }
    }

    /// <summary>Notes what <paramref name="toDir"/>'s manifest, prefab containers and carried register hold now.</summary>
    public static Before Note(string toDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(toDir);
        SdsManifest manifest = SdsManifest.Load(toDir);
        string register = Path.Combine(toDir, RegisterName);
        return new Before(
            toDir,
            File.ReadAllBytes(Path.Combine(toDir, "SDSContent.xml")),
            new HashSet<string>(manifest.Entries.Select(e => e.File), StringComparer.OrdinalIgnoreCase),
            manifest.GetFiles("PREFAB").ToDictionary(p => p, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase),
            File.Exists(register) ? File.ReadAllBytes(register) : null);
    }

    /// <summary>
    /// Takes a carry back out of the working copy: the files it added are removed and the manifest, the prefab
    /// containers and the carried register are what they were at <see cref="Note"/>. For an import that was
    /// refused or failed after its carry — the object never arrived, and what was brought for it would
    /// otherwise stay in the working copy, and in every archive built from it, for nothing.
    /// Only for the moment right after the carry: it puts the lists back as a whole. It goes by what the
    /// manifest gained since the note, not by the carry's report — a carry that threw half-way has no report.
    /// </summary>
    public static void TakeBack(Before before)
    {
        ArgumentNullException.ThrowIfNull(before);
        string dir = before.Dir;
        List<string> gained = [.. SdsManifest.Load(dir).Entries.Select(e => e.File).Where(f => !before.Files.Contains(f))];

        AtomicFile.WriteAllBytes(Path.Combine(dir, "SDSContent.xml"), before.Manifest);
        foreach ((string path, byte[] bytes) in before.Prefabs) AtomicFile.WriteAllBytes(path, bytes);
        string register = Path.Combine(dir, RegisterName);
        if (before.Register is { } kept) AtomicFile.WriteAllBytes(register, kept);
        else File.Delete(register);

        // Files the manifest did not list before, and so nothing of the archive's own.
        foreach (string file in gained) File.Delete(Path.Combine(dir, file.TrimStart('/', '\\')));
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
            AtomicFile.WriteAllBytes(Path.Combine(toDir, CarriedPrefabName), fresh.ToBytes());
            return to.AddEntry(fields[0].Value, CarriedPrefabName, version, [.. fields.Skip(2).Take(fields.Count - 3)]);
        }
        return false;
    }

    // The container a destination with no prefab of its own is given.
    private const string CarriedPrefabName = "PREFAB_carried.prf";

    // Beside the working copy, never in its manifest: packing goes by the manifest, so the game never sees it.
    private const string RegisterName = "illusion_carried.json";

    private static HashSet<string> ReadRegister(string dir)
    {
        string path = Path.Combine(dir, RegisterName);
        try
        {
            return File.Exists(path)
                ? new HashSet<string>(System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [],
                    StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteRegister(string dir, HashSet<string> names)
    {
        string path = Path.Combine(dir, RegisterName);
        if (names.Count == 0)
        {
            File.Delete(path);
            return;
        }
        AtomicFile.WriteAllBytes(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(names.Order(StringComparer.OrdinalIgnoreCase).ToList()));
    }

    private static void Remember(string dir, string texture)
    {
        HashSet<string> names = ReadRegister(dir);
        if (names.Add(texture)) WriteRegister(dir, names);
    }

    // Inside the working copy and outside its manifest, under a name no texture scan answers to (they look
    // for *.dds): what is parked is on nobody's list until it is put back.
    internal const string ParkedFolder = "illusion_parked";
    private const string ParkedIndex = "parked.json";
    private const string ParkedSuffix = ".parked";

    /// <summary>One file of a parked texture — the texture itself or its MIP companion — with the manifest
    /// entry it had, field for field, so that it can be announced again exactly as it was.</summary>
    public sealed class ParkedFile
    {
        public string File { get; set; } = "";
        public List<string[]> Fields { get; set; } = [];
    }

    // What THIS run of the program parked, as "folder|texture". Only these can still be asked for by an undo
    // stack; anything else found parked is left from a session that has ended.
    private static readonly HashSet<string> ParkedHere = new(StringComparer.OrdinalIgnoreCase);

    private static string ParkedKey(string dir, string texture) => Path.GetFullPath(dir) + "|" + texture;

    private static string ParkedPath(string dir, string file) => Path.Combine(dir, ParkedFolder, file + ParkedSuffix);

    /// <summary>The textures parked beside <paramref name="extracted"/>, by name (for the probes).</summary>
    public static IReadOnlyCollection<string> ParkedIn(string extracted) => ReadParked(extracted).Keys;

    /// <summary>Forgets which textures this run parked, as a restart of the program would (for the probes).</summary>
    internal static void ForgetParkedHere()
    {
        lock (ParkedHere) ParkedHere.Clear();
    }

    private static Dictionary<string, List<ParkedFile>> ReadParked(string dir)
    {
        string path = Path.Combine(dir, ParkedFolder, ParkedIndex);
        var empty = new Dictionary<string, List<ParkedFile>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return empty;
            Dictionary<string, List<ParkedFile>>? read =
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<ParkedFile>>>(File.ReadAllText(path));
            return read == null ? empty : new Dictionary<string, List<ParkedFile>>(read, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return empty;
        }
    }

    private static void WriteParked(string dir, Dictionary<string, List<ParkedFile>> parked)
    {
        string folder = Path.Combine(dir, ParkedFolder);
        if (parked.Count == 0)
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return;
        }
        Directory.CreateDirectory(folder);
        AtomicFile.WriteAllBytes(Path.Combine(folder, ParkedIndex), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(parked));
    }

    // Parking, first half: both files copied aside, with the manifest entries they had. Nothing of the
    // working copy is changed; a failure here leaves the texture exactly as it was.
    private static List<ParkedFile> CopyAside(SdsManifest manifest, string dir, string texture)
    {
        var files = new List<ParkedFile>();
        foreach (string file in new[] { texture, SdsImportTypes.MipNameFor(texture) })
        {
            string path = Path.Combine(dir, file);
            IReadOnlyList<(string Name, string Value)>? fields = manifest.EntryFields(file);
            if (fields == null || !File.Exists(path)) continue;
            string parkedPath = ParkedPath(dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(parkedPath)!);
            File.Copy(path, parkedPath, overwrite: true);
            files.Add(new ParkedFile { File = file, Fields = [.. fields.Select(f => new[] { f.Name, f.Value })] });
        }
        return files;
    }

    // Parking, second half — only once the copies are on record ON DISK: the entries dropped and the
    // originals removed. False when either could not be; the texture then stays carried and is tried again
    // at the next save, its copies already safe.
    private static bool RemoveOriginals(SdsManifest manifest, string dir, string texture, List<string> dropped)
    {
        bool whole = true;
        foreach (string file in new[] { texture, SdsImportTypes.MipNameFor(texture) })
        {
            try
            {
                if (manifest.RemoveEntry(file)) dropped.Add(file);
                File.Delete(Path.Combine(dir, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                whole = false;
            }
        }
        return whole;
    }

    // Back where it was, announced as it was. False when a file of it could not be put back — it stays
    // parked and the next sweep tries again.
    private static bool Unpark(SdsManifest manifest, string dir, List<ParkedFile> files)
    {
        bool whole = true;
        foreach (ParkedFile parked in files)
        {
            string parkedPath = ParkedPath(dir, parked.File);
            // Brought again since by another import: that copy is the one in use.
            if (!manifest.HasFile(parked.File))
            {
                List<string[]> fields = parked.Fields;
                if (!File.Exists(parkedPath) || fields.Count < 3 || fields.Any(f => f.Length != 2)
                    || fields[^1][0] != "Version" || !int.TryParse(fields[^1][1], out int version))
                {
                    whole = false;
                    continue;
                }
                File.Copy(parkedPath, Path.Combine(dir, parked.File), overwrite: true);
                if (!manifest.AddEntry(fields[0][1], parked.File, version,
                        [.. fields.Skip(2).Take(fields.Count - 3).Select(f => (f[0], f[1]))]))
                {
                    whole = false;
                    continue;
                }
            }
            File.Delete(parkedPath);
        }
        return whole;
    }

    private static void DropParked(string dir, List<ParkedFile> files)
    {
        foreach (ParkedFile parked in files) File.Delete(ParkedPath(dir, parked.File));
    }

    /// <summary>
    /// Takes the textures this class carried into <paramref name="extracted"/> that no material of
    /// <paramref name="scene"/> names out of the working copy — left behind by an import that was undone, or by
    /// a scene closed without saving it — and puts back the ones taken out earlier that the scene names again:
    /// an import redone, a delete undone. Their MIP companions go and come with them. A texture taken out is
    /// parked, not deleted, for as long as this run of the program lasts. Returns the manifest entries taken out.
    /// </summary>
    public static IReadOnlyList<string> SweepUnused(string extracted, Formats.Frames.FrameResource scene)
    {
        ArgumentException.ThrowIfNullOrEmpty(extracted);
        ArgumentNullException.ThrowIfNull(scene);
        try
        {
            return Sweep(extracted, scene, park: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Formats.SdsFormatException
                                       or System.Xml.XmlException)
        {
            // Housekeeping, run in the middle of a save — after the scene is written and before its buffer
            // pools are. A manifest that cannot be read just now must not leave the save half done.
            return [];
        }
    }

    /// <summary>
    /// Puts back the parked textures the scene names again, and nothing else — for the moment an import is
    /// redone, so that the working copy has the object's textures as soon as the object is back rather than
    /// only after the next save.
    /// </summary>
    public static void ReturnParked(string extracted, Formats.Frames.FrameResource scene)
    {
        ArgumentException.ThrowIfNullOrEmpty(extracted);
        ArgumentNullException.ThrowIfNull(scene);
        try
        {
            Sweep(extracted, scene, park: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Formats.SdsFormatException
                                       or System.Xml.XmlException)
        {
            // then they come back with the next save
        }
    }

    private static IReadOnlyList<string> Sweep(string extracted, Formats.Frames.FrameResource scene, bool park)
    {
        HashSet<string> carried = ReadRegister(extracted);
        Dictionary<string, List<ParkedFile>> parked = ReadParked(extracted);
        if (carried.Count == 0 && parked.Count == 0) return [];

        MafiaMaterials.EnsureLoaded();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Formats.Frames.Resources.FrameMaterial block in scene.FrameMaterials.Values)
        {
            foreach (Formats.Frames.Resources.MaterialStruct[] lod in block.Materials)
            {
                foreach (Formats.Frames.Resources.MaterialStruct slot in lod)
                {
                    foreach (string texture in MafiaMaterials.Collection?.FindByHash(slot.MaterialHash)?.CollectTextures() ?? [])
                    {
                        named.Add(texture);
                    }
                }
            }
        }

        // And the ones a mesh names itself — its occlusion map — which no material leads to.
        foreach (Formats.Frames.ObjectTypes.FrameObjectSingleMesh mesh in
                 scene.FrameObjects.Values.OfType<Formats.Frames.ObjectTypes.FrameObjectSingleMesh>())
        {
            if (mesh.OMTextureHash is { Hash: not 0, String.Length: > 0 } direct) named.Add(direct.String);
        }

        SdsManifest manifest = SdsManifest.Load(extracted);
        var dropped = new List<string>();
        lock (ParkedHere)
        {
            // Back first: what the scene names again. A file that cannot be moved this time (the viewport, or
            // the game, holding it) leaves the texture where it is, to be tried at the next save — a sweep is
            // housekeeping and must not fail the save it runs in.
            foreach ((string texture, List<ParkedFile> files) in parked.ToList())
            {
                try
                {
                    if (named.Contains(texture))
                    {
                        if (!Unpark(manifest, extracted, files)) continue;
                        carried.Add(texture);
                        TextureSearchIndex.Register(Path.Combine(extracted, texture));
                    }
                    else if (!park || ParkedHere.Contains(ParkedKey(extracted, texture)))
                    {
                        continue; // an undo stack of this run can still bring its object back
                    }
                    else
                    {
                        DropParked(extracted, files);
                    }
                    parked.Remove(texture);
                    ParkedHere.Remove(ParkedKey(extracted, texture));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // stays parked
                }
            }

            foreach (string texture in park ? carried.ToList() : [])
            {
                if (named.Contains(texture)) continue;
                try
                {
                    List<ParkedFile> files = CopyAside(manifest, extracted, texture);
                    if (files.Count > 0)
                    {
                        // What an earlier, interrupted attempt already set aside stays on record beside it.
                        if (parked.TryGetValue(texture, out List<ParkedFile>? earlier))
                        {
                            files.AddRange(earlier.Where(e => !files.Any(f => string.Equals(f.File, e.File, StringComparison.OrdinalIgnoreCase))));
                        }
                        parked[texture] = files;
                        ParkedHere.Add(ParkedKey(extracted, texture));
                        // Written down before anything is taken out of the working copy: with the index
                        // written only at the end, a failure there left textures removed and nowhere on
                        // record, and the next sweep cleared the folder they were parked in.
                        WriteParked(extracted, parked);
                    }
                    if (RemoveOriginals(manifest, extracted, texture, dropped)) carried.Remove(texture);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // stays carried
                }
            }
            WriteParked(extracted, parked);
        }
        WriteRegister(extracted, carried);
        return dropped;
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
