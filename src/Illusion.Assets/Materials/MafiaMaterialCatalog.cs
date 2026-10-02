using Illusion.Assets.Import;
using Illusion.Domain.Materials;
using Illusion.Formats.Hashing;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;

namespace Illusion.Assets.Materials;

/// <summary>
/// The app's <see cref="IMaterialCatalog"/> over the loaded MTL libraries (<see cref="MafiaMaterials"/>):
/// browsing plus the in-memory mutations the material editor performs. Add/remove/rename swap the material
/// dictionary copy-on-write (the <see cref="GameMaterialCreator"/> discipline) so background mesh loaders
/// never observe a half-mutated map; a slot rebind mutates the material record in place — the dictionary
/// stays untouched, and a racing reader merely sees the old or the new texture name. Mutations run on the
/// UI thread; the dirty set has its own lock only because Save may be probed headless.
/// </summary>
public sealed class MafiaMaterialCatalog : IMaterialCatalog
{
    public static MafiaMaterialCatalog Instance { get; } = new();

    private MafiaMaterialCatalog() { }

    private readonly object _dirtySync = new();
    private readonly HashSet<MaterialLibrary> _dirty = new();

    public IReadOnlyList<string> Libraries
    {
        get
        {
            MaterialCollection? collection = MafiaMaterials.Collection;
            if (collection == null) return Array.Empty<string>();
            return collection.Libraries.Keys.Select(Path.GetFileName).Where(n => n != null).ToList()!;
        }
    }

    public IReadOnlyList<MaterialSummary> GetMaterials(string library)
    {
        MaterialLibrary? lib = FindLibrary(library);
        if (lib == null) return Array.Empty<MaterialSummary>();
        // Snapshot the dictionary reference first — a concurrent copy-on-write swap replaces the instance.
        Dictionary<ulong, IMaterial> materials = lib.Materials;
        var result = new List<MaterialSummary>(materials.Count);
        foreach (IMaterial mat in materials.Values)
            result.Add(new MaterialSummary(mat.GetMaterialName(), mat.GetMaterialHash()));
        return result;
    }

    public MaterialInfo? GetMaterial(ulong hash)
    {
        MaterialInfo info = MafiaMaterials.GetMaterialInfo(hash, 0, 0);
        return info.Resolved ? info : null;
    }

    public string? LibraryOf(ulong hash) => Path.GetFileName(FindOwningLibrary(hash)?.Name);

    public string? GetTexture(ulong hash, string slotId)
    {
        IMaterial? mat = MafiaMaterials.Collection?.FindByHash(hash);
        return mat?.GetSamplerByKey(slotId) == null ? null : (mat.GetTextureByID(slotId)?.String ?? "");
    }

    public bool SetTexture(ulong hash, string slotId, string textureName)
    {
        MaterialLibrary? lib = FindOwningLibrary(hash);
        IMaterial? mat = lib?.LookupMaterialByHash(hash);
        if (lib == null || mat?.GetSamplerByKey(slotId) == null) return false;
        mat.SetTextureFor(slotId, textureName);
        // A sampler's TexType is 0 only while its slot is empty; every stock sampler that names a texture
        // carries 2, and one left at 0 samples nothing in game. A slot added in the editor starts empty at
        // 0, so binding a texture is where it has to become 2.
        if (textureName.Length > 0 && mat is Material_v57 v57
            && v57.Samplers.FirstOrDefault(s => s.ID == slotId) is { TexType: 0 } sampler)
        {
            sampler.TexType = 2;
        }
        MarkDirty(lib);
        return true;
    }

    public ulong? CreateMaterial(string library, string name) => CreateMaterial(library, name, normalMapped: false);

    public ulong? CreateMaterial(string library, string name, bool normalMapped)
    {
        MaterialLibrary? lib = FindLibrary(library);
        if (lib == null) return null;
        IMaterial? created = GameMaterialCreator.Add(
            lib, name, normalMapped ? MaterialPreset.DiffuseNormal : MaterialPreset.Default);
        if (created == null) return null;
        MarkDirty(lib);
        return created.GetMaterialHash();
    }

    public ulong? RenameMaterial(ulong hash, string newName, ulong? restoreHash = null)
    {
        MaterialLibrary? lib = FindOwningLibrary(hash);
        IMaterial? mat = lib?.LookupMaterialByHash(hash);
        if (lib == null || mat == null || string.IsNullOrWhiteSpace(newName)) return null;

        // Vanilla libraries carry hashes that are not always FNV64(name), so the display name must stay
        // unique on its own — imports and the editor resolve materials by name. Uniqueness is scoped to
        // the owning library: sibling libraries legitimately duplicate names (a mod .mtl carries copies
        // of vanilla materials), so a cross-library check would refuse restoring a material's own name.
        IMaterial? sameName = lib.LookupMaterialByName(newName);
        if (sameName != null && !ReferenceEquals(sameName, mat)) return null;

        ulong newHash = restoreHash ?? Fnv64.Hash(newName); // the same derivation SetName performs
        if (newHash != hash)
        {
            // Key uniqueness is the owning library's dictionary invariant. Across libraries duplicate
            // hashes are game reality (mod libraries copy vanilla materials hash-and-all) and lookups
            // resolve by library order exactly like the game does, so they are not refused here.
            if (lib.Materials.ContainsKey(newHash)) return null;
            // Re-key copy-on-write, keeping the entry's position — save order is part of byte-fidelity.
            var swapped = new Dictionary<ulong, IMaterial>(lib.Materials.Count);
            foreach ((ulong key, IMaterial value) in lib.Materials)
                swapped[key == hash ? newHash : key] = value;
            ApplyIdentity(mat, newName, newHash);
            lib.Materials = swapped;
        }
        else
        {
            ApplyIdentity(mat, newName, newHash); // key unchanged — the dictionary stays valid
        }
        MarkDirty(lib);
        return newHash;
    }

    // SetName re-derives FNV64(name); pinning the hash afterwards lets an undo restore a vanilla
    // material's stored hash exactly even when it never matched the FNV64 of its name.
    private static void ApplyIdentity(IMaterial mat, string name, ulong hash)
    {
        mat.SetName(name);
        mat.MaterialName.Hash = hash;
    }

    public object? RemoveMaterial(ulong hash)
    {
        MaterialLibrary? lib = FindOwningLibrary(hash);
        IMaterial? mat = lib?.LookupMaterialByHash(hash);
        if (lib == null || mat == null) return null;
        var swapped = new Dictionary<ulong, IMaterial>(lib.Materials);
        swapped.Remove(hash);
        lib.Materials = swapped;
        MarkDirty(lib);
        return new RemovalToken(lib, mat);
    }

    public bool RestoreMaterial(object token)
    {
        if (token is not RemovalToken t) return false;
        if (t.Library.Materials.ContainsKey(t.Material.GetMaterialHash())) return false;
        var swapped = new Dictionary<ulong, IMaterial>(t.Library.Materials)
        {
            [t.Material.GetMaterialHash()] = t.Material,
        };
        t.Library.Materials = swapped;
        MarkDirty(t.Library);
        return true;
    }

    private static IReadOnlyList<SlotDescriptor>? _knownSlots;

    public IReadOnlyList<SlotDescriptor> KnownSamplerSlots =>
        _knownSlots ??= MaterialParameterNames.All
            .Where(kv => kv.Key.Length == 4 && kv.Key[0] == 'S')
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new SlotDescriptor(kv.Key, kv.Value))
            .ToList();

    public bool AddSampler(ulong hash, string slotId)
    {
        MaterialLibrary? lib = FindOwningLibrary(hash);
        IMaterial? mat = lib?.LookupMaterialByHash(hash);
        if (lib == null || mat == null || mat.GetSamplerByKey(slotId) != null) return false;

        // The samplers list is swapped copy-on-write — background loaders enumerate it concurrently.
        switch (mat)
        {
            case Material_v57 v57:
                v57.Samplers = new List<MaterialSampler_v57>(v57.Samplers) { new() { ID = slotId } };
                break;
            case Material_v58 v58:
                v58.Samplers = new List<MaterialSampler_v58>(v58.Samplers) { new() { ID = slotId } };
                break;
            default:
                return false;
        }
        MarkDirty(lib);
        return true;
    }

    public object? RemoveSampler(ulong hash, string slotId)
    {
        MaterialLibrary? lib = FindOwningLibrary(hash);
        IMaterial? mat = lib?.LookupMaterialByHash(hash);
        IMaterialSampler? sampler = mat?.GetSamplerByKey(slotId);
        if (lib == null || mat == null || sampler == null) return null;

        switch (mat)
        {
            case Material_v57 v57:
            {
                int index = v57.Samplers.IndexOf((MaterialSampler_v57)sampler);
                var swapped = new List<MaterialSampler_v57>(v57.Samplers);
                swapped.RemoveAt(index);
                v57.Samplers = swapped;
                MarkDirty(lib);
                return new SamplerToken(lib, sampler, index);
            }
            case Material_v58 v58:
            {
                int index = v58.Samplers.IndexOf((MaterialSampler_v58)sampler);
                var swapped = new List<MaterialSampler_v58>(v58.Samplers);
                swapped.RemoveAt(index);
                v58.Samplers = swapped;
                MarkDirty(lib);
                return new SamplerToken(lib, sampler, index);
            }
            default:
                return null;
        }
    }

    public bool RestoreSampler(ulong hash, object token)
    {
        if (token is not SamplerToken t) return false;
        IMaterial? mat = t.Library.LookupMaterialByHash(hash);
        if (mat == null || mat.GetSamplerByKey(t.Sampler.ID) != null) return false;

        switch (mat)
        {
            case Material_v57 v57 when t.Sampler is MaterialSampler_v57 s57:
            {
                var swapped = new List<MaterialSampler_v57>(v57.Samplers);
                swapped.Insert(Math.Min(t.Index, swapped.Count), s57); // original position → byte-faithful undo
                v57.Samplers = swapped;
                break;
            }
            case Material_v58 v58 when t.Sampler is MaterialSampler_v58 s58:
            {
                var swapped = new List<MaterialSampler_v58>(v58.Samplers);
                swapped.Insert(Math.Min(t.Index, swapped.Count), s58);
                v58.Samplers = swapped;
                break;
            }
            default:
                return false;
        }
        MarkDirty(t.Library);
        return true;
    }

    private static IReadOnlyList<ParamDescriptor>? _knownParams;
    private static bool _knownParamsScanned; // built before the libraries loaded → lengths missing, rebuild

    public IReadOnlyList<ParamDescriptor> KnownParameters
    {
        get
        {
            MaterialCollection? collection = MafiaMaterials.Collection;
            if (_knownParams == null || (!_knownParamsScanned && collection != null))
            {
                // Canonical payload lengths come from the game data itself: first sighting of a code
                // across every loaded material fixes its float count.
                var lengths = new Dictionary<string, int>(StringComparer.Ordinal);
                if (collection != null)
                    foreach (MaterialLibrary lib in collection.Libraries.Values)
                        foreach (IMaterial mat in lib.Materials.Values)
                            foreach (MaterialParameter p in mat.Parameters)
                                lengths.TryAdd(p.ID, p.Paramaters.Length);

                var all = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach ((string id, string name) in MaterialParameterNames.All)
                    if (!(id.Length == 4 && id[0] == 'S'))
                        all[id] = name;
                foreach (string id in lengths.Keys) all.TryAdd(id, id); // codes the name table missed

                _knownParams = all.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new ParamDescriptor(kv.Key, kv.Value,
                        lengths.TryGetValue(kv.Key, out int n) ? n : null))
                    .ToList();
                _knownParamsScanned = collection != null;
            }
            return _knownParams;
        }
    }

    public IReadOnlyList<float>? GetParameter(ulong hash, string paramId)
    {
        IMaterial? mat = MafiaMaterials.Collection?.FindByHash(hash);
        return mat?.GetParameterByKey(paramId)?.Paramaters?.ToArray();
    }

    public bool SetParameter(ulong hash, string paramId, IReadOnlyList<float> values)
    {
        MaterialLibrary? lib = FindOwningLibrary(hash);
        MaterialParameter? param = lib?.LookupMaterialByHash(hash)?.GetParameterByKey(paramId);
        if (lib == null || param == null) return false;
        if (param.Paramaters.Length != values.Count) return false; // byte length is part of the format
        param.Paramaters = values.ToArray(); // whole-array swap — readers see old or new, never a mix
        MarkDirty(lib);
        return true;
    }

    public bool AddParameter(ulong hash, string paramId, IReadOnlyList<float> values)
    {
        MaterialLibrary? lib = FindOwningLibrary(hash);
        IMaterial? mat = lib?.LookupMaterialByHash(hash);
        if (lib == null || mat == null || values.Count == 0) return false;
        if (mat.GetParameterByKey(paramId) != null) return false;
        // The game's shader reads a fixed payload per code — enforce the length the loaded data shows.
        int? canonical = KnownParameters.FirstOrDefault(d => d.Id == paramId)?.Length;
        if (canonical is int n && n != values.Count) return false;

        // The parameter list is swapped copy-on-write — background loaders enumerate it concurrently.
        mat.Parameters = new List<MaterialParameter>(mat.Parameters)
        {
            new() { ID = paramId, Paramaters = values.ToArray() },
        };
        MarkDirty(lib);
        return true;
    }

    public bool RemoveParameter(ulong hash, string paramId)
    {
        MaterialLibrary? lib = FindOwningLibrary(hash);
        IMaterial? mat = lib?.LookupMaterialByHash(hash);
        MaterialParameter? param = mat?.GetParameterByKey(paramId);
        if (lib == null || mat == null || param == null) return false;
        var swapped = new List<MaterialParameter>(mat.Parameters);
        swapped.Remove(param);
        mat.Parameters = swapped;
        MarkDirty(lib);
        return true;
    }

    public bool HasUnsavedChanges
    {
        get { lock (_dirtySync) return _dirty.Count > 0; }
    }

    public string? SaveDirty(out int saved)
    {
        saved = 0;
        List<MaterialLibrary> pending;
        lock (_dirtySync) pending = _dirty.ToList();

        string? firstError = null;
        foreach (MaterialLibrary lib in pending)
        {
            try
            {
                GameMaterialCreator.BackupAndWrite(lib, lib.Name); // Name carries the load path
                lock (_dirtySync) _dirty.Remove(lib);
                saved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                firstError ??= Path.GetFileName(lib.Name) + " — " + ex.Message; // stays dirty for a retry
            }
        }
        return firstError;
    }

    private void MarkDirty(MaterialLibrary lib)
    {
        lock (_dirtySync) _dirty.Add(lib);
    }

    private static MaterialLibrary? FindLibrary(string fileName)
    {
        MaterialCollection? collection = MafiaMaterials.Collection;
        if (collection == null) return null;
        foreach ((string path, MaterialLibrary lib) in collection.Libraries)
            if (string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                return lib;
        return null;
    }

    private static MaterialLibrary? FindOwningLibrary(ulong hash)
    {
        MaterialCollection? collection = MafiaMaterials.Collection;
        if (collection == null) return null;
        foreach (MaterialLibrary lib in collection.Libraries.Values)
            if (lib.Materials.ContainsKey(hash))
                return lib;
        return null;
    }

    /// <summary>The removed material and its home library — restoring re-inserts the exact instance,
    /// so undo of a delete is bit-faithful on the next save.</summary>
    private sealed record RemovalToken(MaterialLibrary Library, IMaterial Material);

    /// <summary>A removed sampler with its home library and original list position — same undo fidelity.</summary>
    private sealed record SamplerToken(MaterialLibrary Library, IMaterialSampler Sampler, int Index);
}
