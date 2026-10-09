using Illusion.Assets;
using Illusion.Assets.Bridge;
using Illusion.Assets.Materials;
using Illusion.Domain;
using Illusion.Domain.Materials;
using Illusion.Rendering.Gpu;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Editing of game materials (the material editor window): texture-slot rebinding, create/delete, and
/// repointing a mesh's material slot — each applied through the <see cref="IMaterialCatalog"/> /
/// <see cref="IMaterialSlotEditor"/> ports, recorded as one undoable edit on the shared history, and
/// followed by a live re-resolve of every loaded mesh part that renders the touched material. MTL edits
/// are global (not per node), so they persist through <see cref="SaveDirtyMaterials"/> in the common
/// Save flow rather than the frame documents; only the slot reassignment marks a frame modified.
/// </summary>
internal sealed class MaterialEditController
{
    private readonly D3DImageHost _host;

    public MaterialEditController(D3DImageHost host) => _host = host;

    public IMaterialCatalog Catalog => MafiaMaterialCatalog.Instance;

    /// <summary>Whether any MTL library has in-memory edits not yet written (feeds the title '*').</summary>
    public bool HasUnsavedMaterials => Catalog.HasUnsavedChanges;

    /// <summary>Writes every dirty MTL library (timestamped backup + atomic replace). Null on success.</summary>
    public string? SaveDirtyMaterials(out int saved) => Catalog.SaveDirty(out saved);

    /// <summary>Loaded mesh parts currently rendering the material — the delete confirmation's blast radius.</summary>
    public int CountLoadedUses(ulong hash)
    {
        if (_host.Rnd is not { } renderer || hash == 0) return 0;
        int uses = 0;
        foreach (GpuMesh gm in renderer.Meshes)
            foreach (GpuPart part in gm.Parts)
                if (part.MaterialHash == hash)
                    uses++;
        return uses;
    }

    // ── Texture slot rebinding ──

    /// <summary>Rebinds one texture slot of a material (undoable). False when the material/slot is unknown.</summary>
    public bool SetTexture(ulong hash, string slotId, string textureName)
    {
        string? before = Catalog.GetTexture(hash, slotId);
        if (before == null) return false;
        if (before == textureName) return true; // no-op — nothing to record
        if (!Catalog.SetTexture(hash, slotId, textureName)) return false;
        _host.Editing.History.Push(new TextureEdit(this, hash, slotId, before, textureName));
        AfterMaterialChanged(hash);
        return true;
    }

    private void ApplyTexture(ulong hash, string slotId, string value)
    {
        if (Catalog.SetTexture(hash, slotId, value)) AfterMaterialChanged(hash);
    }

    // ── Sampler slots (add / remove) ──

    /// <summary>Adds an empty texture slot to a material (undoable). False when it already exists.</summary>
    public bool AddTextureSlot(ulong hash, string slotId)
    {
        if (!Catalog.AddSampler(hash, slotId)) return false;
        _host.Editing.History.Push(new AddSlotEdit(this, hash, slotId));
        AfterMaterialChanged(hash);
        return true;
    }

    /// <summary>Removes a texture slot from a material (undoable — the exact sampler is restorable).</summary>
    public bool RemoveTextureSlot(ulong hash, string slotId)
    {
        object? token = Catalog.RemoveSampler(hash, slotId);
        if (token == null) return false;
        _host.Editing.History.Push(new RemoveSlotEdit(this, hash, slotId, token));
        AfterMaterialChanged(hash);
        return true;
    }

    // ── Shader parameters ──

    /// <summary>Replaces one parameter's float payload (undoable). False when the count differs or the
    /// material/parameter is unknown.</summary>
    public bool SetParameter(ulong hash, string paramId, IReadOnlyList<float> values)
    {
        IReadOnlyList<float>? before = Catalog.GetParameter(hash, paramId);
        if (before == null) return false;
        if (before.SequenceEqual(values)) return true; // no-op — nothing to record
        if (!Catalog.SetParameter(hash, paramId, values)) return false;
        _host.Editing.History.Push(new ParamEdit(this, hash, paramId, before, values));
        AfterMaterialChanged(hash);
        return true;
    }

    private void ApplyParameter(ulong hash, string paramId, IReadOnlyList<float> values)
    {
        if (Catalog.SetParameter(hash, paramId, values)) AfterMaterialChanged(hash);
    }

    // ── Flags ──

    /// <summary>Replaces a material's flag word (undoable). False when the material is unknown.</summary>
    public bool SetFlags(ulong hash, Illusion.Formats.Materials.MaterialFlags flags)
    {
        MafiaMaterialCatalog catalog = MafiaMaterialCatalog.Instance;
        if (catalog.GetFlags(hash) is not { } before) return false;
        if (before == flags) return true; // no-op — nothing to record
        if (!catalog.SetFlags(hash, flags)) return false;
        _host.Editing.History.Push(new FlagsEdit(this, hash, before, flags));
        AfterMaterialChanged(hash);
        return true;
    }

    private void ApplyFlags(ulong hash, Illusion.Formats.Materials.MaterialFlags flags)
    {
        if (MafiaMaterialCatalog.Instance.SetFlags(hash, flags)) AfterMaterialChanged(hash);
    }

    /// <summary>Adds a shader parameter the material does not carry yet (undoable). False when the code
    /// is already present or the float count contradicts the loaded libraries' canonical length.</summary>
    public bool AddParameter(ulong hash, string paramId, IReadOnlyList<float> values)
    {
        if (!Catalog.AddParameter(hash, paramId, values)) return false;
        _host.Editing.History.Push(new AddParamEdit(this, hash, paramId, values));
        AfterMaterialChanged(hash);
        return true;
    }

    // ── Create / delete ──

    /// <summary>Creates a default-preset material in <paramref name="library"/> (undoable). Null when the
    /// name is empty/taken or the library is unknown.</summary>
    public ulong? CreateMaterial(string library, string name)
    {
        ulong? hash = Catalog.CreateMaterial(library, name);
        if (hash == null) return null;
        _host.Editing.History.Push(new CreateEdit(this, hash.Value));
        AfterMaterialChanged(hash.Value);
        return hash;
    }

    // ── Materials made in Blender (the bridge's IAuthoredMaterialHost, with history) ──

    /// <summary>
    /// Creates a material that arrived from Blender, complete — its shader chosen by whether it carries a
    /// normal map, its textures bound, its specular set. ONE history entry: undoing it removes the
    /// material, bindings and all, and redo restores that same instance.
    /// </summary>
    public ulong? CreateAuthored(string library, AuthoredMaterial material)
    {
        MafiaMaterialCatalog catalog = MafiaMaterialCatalog.Instance;
        ulong? hash = catalog.CreateMaterial(library, material.Name, material.NormalMapped);
        if (hash == null) return null;
        CatalogAuthoredMaterials.Apply(catalog, hash.Value, material);
        _host.Editing.History.Push(new CreateEdit(this, hash.Value));
        AfterMaterialChanged(hash.Value);
        return hash;
    }

    /// <summary>Brings a Blender-made material of the same shape up to date — each change through the path
    /// the editor itself uses, so each is its own undoable edit and a value that did not change records
    /// nothing.</summary>
    public bool UpdateAuthored(ulong hash, AuthoredMaterial material)
    {
        if (!SetTexture(hash, "S000", material.Diffuse)) return false;
        if (MafiaMaterialCatalog.Instance.GetFlags(hash) is not { } flags
            || !SetFlags(hash, AuthoredAlphaFlags.Apply(flags, material.Alpha)))
        {
            return false;
        }
        if (material.NormalSpecular == null) return true;
        if (!SetTexture(hash, "S001", material.NormalSpecular)) return false;
        return SetParameter(hash, "D013", [material.SpecularPower, material.SpecularLevel]);
    }

    /// <summary>Replaces a Blender-made material that gained or lost its normal map — a different shader,
    /// so a different record under the same name and hash. A delete and a create, both undoable.</summary>
    public ulong? ReplaceAuthored(string library, ulong hash, AuthoredMaterial material) =>
        DeleteMaterial(hash) ? CreateAuthored(library, material) : null;

    /// <summary>
    /// A texture FILE was rewritten under the name the material already uses: nothing in the catalog
    /// changed, so the renderer is told to forget its copy and every part drawing the material re-reads it.
    /// </summary>
    public void ReloadTexture(ulong hash, string textureName) => ReloadTextureFiles([textureName]);

    /// <summary>
    /// Several texture files were rewritten under their names — by a push, or by undoing one. Each is
    /// forgotten once, and each material that names any of them rebinds its parts once: a push of a hundred
    /// objects sharing a material used to invalidate and rebind a hundred times over, every time across
    /// every loaded mesh.
    /// </summary>
    public void ReloadTextureFiles(IEnumerable<string> textureNames)
    {
        var names = new HashSet<string>(textureNames, StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0 || _host.Rnd is not { } renderer) return;
        foreach (string name in names) renderer.Textures.Invalidate(name);

        var drawn = new HashSet<ulong>();
        foreach (GpuMesh gm in renderer.Meshes)
        {
            foreach (GpuPart part in gm.Parts)
            {
                if (part.MaterialHash != 0) drawn.Add(part.MaterialHash);
            }
        }
        foreach (ulong hash in drawn)
        {
            MafiaMaterials.MaterialTextures tex = MafiaMaterials.GetMaterialTextures(hash);
            if ((tex.Diffuse != null && names.Contains(tex.Diffuse)) || (tex.Normal != null && names.Contains(tex.Normal))
                || (tex.Specular != null && names.Contains(tex.Specular)))
            {
                RefreshMeshesUsing(hash);
            }
        }
        _host.RaiseMaterialsChanged();
    }

    /// <summary>Renames a material — the FNV64 hash re-derives from the name (undoable). Null when the
    /// name is empty/taken or the hash is unknown. Loaded meshes keep the OLD hash and fall back to
    /// placeholder textures, exactly like after a delete.</summary>
    public ulong? RenameMaterial(ulong hash, string newName)
    {
        string? oldName = Catalog.GetMaterial(hash)?.Name;
        if (oldName == null) return null;
        if (newName == oldName) return hash; // no-op — nothing to record
        ulong? newHash = Catalog.RenameMaterial(hash, newName);
        if (newHash == null) return null;
        _host.Editing.History.Push(new RenameEdit(this, hash, oldName, newHash.Value, newName));
        AfterMaterialRenamed(hash, newHash.Value);
        return newHash;
    }

    // Undo/redo pin the exact target hash: a vanilla material's stored hash is not always the FNV64 of
    // its name, and undo must restore it byte-exactly.
    private void ApplyRename(ulong fromHash, string toName, ulong toHash)
    {
        if (Catalog.RenameMaterial(fromHash, toName, toHash) != null) AfterMaterialRenamed(fromHash, toHash);
    }

    /// <summary>Deletes a material from its library (undoable — the exact instance is restorable).
    /// Loaded meshes that still reference the hash fall back to placeholder textures.</summary>
    public bool DeleteMaterial(ulong hash)
    {
        object? token = Catalog.RemoveMaterial(hash);
        if (token == null) return false;
        _host.Editing.History.Push(new DeleteEdit(this, hash, token));
        AfterMaterialChanged(hash);
        return true;
    }

    // ── Mesh slot reassignment ──

    /// <summary>Repoints material slot <paramref name="slotIndex"/> of <paramref name="node"/> at another
    /// material (undoable; marks the frame modified so Save/Build persist it). Refuses a node that has
    /// left the scene — a stale editor context after an area reload would silently edit a dead document
    /// (its GpuMesh is disposed, so nothing visible could ever change) — matching the IsInScene gate the
    /// undo path (<see cref="ApplySlot"/>) and ImportBatch already apply.</summary>
    public bool AssignSlotMaterial(SceneNode node, int slotIndex, ulong newHash)
    {
        if (node.Source is not IMaterialSlotEditor editor || !_host.Tree.IsInScene(node)) return false;
        if (SlotAssignObstacle(node) != null) return false;
        ulong? before = editor.GetSlotMaterial(slotIndex);
        if (before == null) return false;
        if (before == newHash) return true;
        IReadOnlyList<ulong[]> tableBefore = editor.GetSlotTable();
        if (!editor.SetSlotMaterial(slotIndex, newHash)) return false;
        _host.Editing.History.Push(new SlotEdit(this, node, slotIndex, before.Value, newHash, tableBefore, editor.GetSlotTable()));
        AfterSlotChanged(node, slotIndex, newHash);
        return true;
    }

    // The whole table goes back, every level of it: the re-point mirrored itself into further levels by the
    // slot's old hash, and the same rule run the other way turns a level that had two materials into one.
    /// <summary>
    /// Why a slot of this node's mesh cannot be re-pointed, or null. A slot is a row of the mesh's material
    /// block, and a block can serve several objects: all of them would change in the file and in the game,
    /// while the viewport redraws - and the caller is told about - one.
    /// </summary>
    public string? SlotAssignObstacle(SceneNode node)
    {
        if (node.Source is not Assets.Adapters.FrameNodeAdapter { Frame: Formats.Frames.ObjectTypes.FrameObjectSingleMesh mesh } adapter
            || adapter.Document.MaterialSharers(mesh) is not { Count: > 0 } sharers)
        {
            return null;
        }
        return $"'{node.Name}' draws with a material block {sharers.Count} other object(s) use too ("
            + string.Join(", ", sharers.Take(5).Select(s => s.Name.ToString())) + (sharers.Count > 5 ? ", …" : "")
            + ") — re-pointing the slot would change them all";
    }

    private void ApplySlot(SceneNode node, int slotIndex, ulong hash, IReadOnlyList<ulong[]> table)
    {
        if (node.Source is not IMaterialSlotEditor editor || !_host.Tree.IsInScene(node)) return;
        if (editor.SetSlotTable(table) || editor.SetSlotMaterial(slotIndex, hash)) AfterSlotChanged(node, slotIndex, hash);
    }

    // ── Shared after-effects ──

    // A material's slots changed (or it appeared/disappeared): re-resolve every loaded part bound to the
    // hash, then let the UI (Materials tab, editor window) and the title '*' refresh.
    private void AfterMaterialChanged(ulong hash)
    {
        RefreshMeshesUsing(hash);
        _host.RaiseMaterialsChanged();
        _host.RaiseDirtyChanged();
    }

    // A rename moves the material to a new hash identity: parts bound to either side re-resolve (the old
    // hash falls back to placeholders, the new one picks the material up).
    private void AfterMaterialRenamed(ulong oldHash, ulong newHash)
    {
        RefreshMeshesUsing(oldHash);
        RefreshMeshesUsing(newHash);
        _host.RaiseMaterialsChanged();
        _host.RaiseDirtyChanged();
    }

    private void AfterSlotChanged(SceneNode node, int slotIndex, ulong hash)
    {
        _host.Persistence.MarkFrameModified(node);
        if (node.Mesh is { } gm)
        {
            MafiaMaterials.MaterialTextures tex = MafiaMaterials.GetMaterialTextures(hash);
            gm.SetPartMaterial(slotIndex, hash, tex.Diffuse, tex.Normal, tex.Specular, tex.Tint, tex.Blended);
        }
        _host.RaiseMaterialsChanged();
        _host.RaiseSelectionPropertiesChanged();
    }

    internal void RefreshMeshesUsing(ulong hash)
    {
        if (_host.Rnd is not { } renderer) return;
        MafiaMaterials.MaterialTextures tex = MafiaMaterials.GetMaterialTextures(hash);
        foreach (GpuMesh gm in renderer.Meshes)
            gm.RebindPartTextures(hash, tex.Diffuse, tex.Normal, tex.Specular, tex.Tint, tex.Blended);
    }

    // ── Edits ──
    // MTL edits are library-global: NOT INodeEdit, so streaming a district out never prunes them.

    private sealed class TextureEdit : IEditAction
    {
        private readonly MaterialEditController _owner;
        private readonly ulong _hash;
        private readonly string _slotId;
        private readonly string _before;
        private readonly string _after;

        public TextureEdit(MaterialEditController owner, ulong hash, string slotId, string before, string after)
        {
            _owner = owner;
            _hash = hash;
            _slotId = slotId;
            _before = before;
            _after = after;
        }

        public void Undo() => _owner.ApplyTexture(_hash, _slotId, _before);
        public void Redo() => _owner.ApplyTexture(_hash, _slotId, _after);
    }

    private sealed class FlagsEdit : IEditAction
    {
        private readonly MaterialEditController _owner;
        private readonly ulong _hash;
        private readonly Illusion.Formats.Materials.MaterialFlags _before;
        private readonly Illusion.Formats.Materials.MaterialFlags _after;

        public FlagsEdit(MaterialEditController owner, ulong hash,
            Illusion.Formats.Materials.MaterialFlags before, Illusion.Formats.Materials.MaterialFlags after)
        {
            _owner = owner;
            _hash = hash;
            _before = before;
            _after = after;
        }

        public void Undo() => _owner.ApplyFlags(_hash, _before);
        public void Redo() => _owner.ApplyFlags(_hash, _after);
    }

    private sealed class AddSlotEdit : IEditAction
    {
        private readonly MaterialEditController _owner;
        private readonly ulong _hash;
        private readonly string _slotId;
        private object? _token; // captured by the first undo; redo restores the same instance

        public AddSlotEdit(MaterialEditController owner, ulong hash, string slotId)
        {
            _owner = owner;
            _hash = hash;
            _slotId = slotId;
        }

        public void Undo()
        {
            _token = _owner.Catalog.RemoveSampler(_hash, _slotId);
            _owner.AfterMaterialChanged(_hash);
        }

        public void Redo()
        {
            if (_token != null && _owner.Catalog.RestoreSampler(_hash, _token)) _owner.AfterMaterialChanged(_hash);
        }
    }

    private sealed class RemoveSlotEdit : IEditAction
    {
        private readonly MaterialEditController _owner;
        private readonly ulong _hash;
        private readonly string _slotId;
        private object? _token;

        public RemoveSlotEdit(MaterialEditController owner, ulong hash, string slotId, object token)
        {
            _owner = owner;
            _hash = hash;
            _slotId = slotId;
            _token = token;
        }

        public void Undo()
        {
            if (_token != null && _owner.Catalog.RestoreSampler(_hash, _token)) _owner.AfterMaterialChanged(_hash);
        }

        public void Redo()
        {
            _token = _owner.Catalog.RemoveSampler(_hash, _slotId);
            _owner.AfterMaterialChanged(_hash);
        }
    }

    // Adding a parameter is reversible by id — the payload is recorded for redo.
    private sealed class AddParamEdit : IEditAction
    {
        private readonly MaterialEditController _owner;
        private readonly ulong _hash;
        private readonly string _paramId;
        private readonly IReadOnlyList<float> _values;

        public AddParamEdit(MaterialEditController owner, ulong hash, string paramId, IReadOnlyList<float> values)
        {
            _owner = owner;
            _hash = hash;
            _paramId = paramId;
            _values = values;
        }

        public void Undo()
        {
            if (_owner.Catalog.RemoveParameter(_hash, _paramId)) _owner.AfterMaterialChanged(_hash);
        }

        public void Redo()
        {
            if (_owner.Catalog.AddParameter(_hash, _paramId, _values)) _owner.AfterMaterialChanged(_hash);
        }
    }

    private sealed class ParamEdit : IEditAction
    {
        private readonly MaterialEditController _owner;
        private readonly ulong _hash;
        private readonly string _paramId;
        private readonly IReadOnlyList<float> _before;
        private readonly IReadOnlyList<float> _after;

        public ParamEdit(MaterialEditController owner, ulong hash, string paramId,
            IReadOnlyList<float> before, IReadOnlyList<float> after)
        {
            _owner = owner;
            _hash = hash;
            _paramId = paramId;
            _before = before;
            _after = after;
        }

        public void Undo() => _owner.ApplyParameter(_hash, _paramId, _before);
        public void Redo() => _owner.ApplyParameter(_hash, _paramId, _after);
    }

    private sealed class CreateEdit : IEditAction
    {
        private readonly MaterialEditController _owner;
        private readonly ulong _hash;
        private object? _token; // captured by the first undo; redo restores the same instance

        public CreateEdit(MaterialEditController owner, ulong hash)
        {
            _owner = owner;
            _hash = hash;
        }

        public void Undo()
        {
            _token = _owner.Catalog.RemoveMaterial(_hash);
            _owner.AfterMaterialChanged(_hash);
        }

        public void Redo()
        {
            if (_token != null && _owner.Catalog.RestoreMaterial(_token)) _owner.AfterMaterialChanged(_hash);
        }
    }

    // A rename is reversible in place — both identities (name + exact hash) are recorded, no token needed.
    private sealed class RenameEdit : IEditAction
    {
        private readonly MaterialEditController _owner;
        private readonly ulong _oldHash;
        private readonly string _oldName;
        private readonly ulong _newHash;
        private readonly string _newName;

        public RenameEdit(MaterialEditController owner, ulong oldHash, string oldName, ulong newHash, string newName)
        {
            _owner = owner;
            _oldHash = oldHash;
            _oldName = oldName;
            _newHash = newHash;
            _newName = newName;
        }

        public void Undo() => _owner.ApplyRename(_newHash, _oldName, _oldHash);
        public void Redo() => _owner.ApplyRename(_oldHash, _newName, _newHash);
    }

    private sealed class DeleteEdit : IEditAction
    {
        private readonly MaterialEditController _owner;
        private readonly ulong _hash;
        private object? _token;

        public DeleteEdit(MaterialEditController owner, ulong hash, object token)
        {
            _owner = owner;
            _hash = hash;
            _token = token;
        }

        public void Undo()
        {
            if (_token != null && _owner.Catalog.RestoreMaterial(_token)) _owner.AfterMaterialChanged(_hash);
        }

        public void Redo()
        {
            _token = _owner.Catalog.RemoveMaterial(_hash);
            _owner.AfterMaterialChanged(_hash);
        }
    }

    // The slot reassignment targets one node — prunable with its district like any other node edit.
    private sealed class SlotEdit : INodeEdit
    {
        private readonly MaterialEditController _owner;
        private readonly SceneNode _node;
        private readonly int _slotIndex;
        private readonly ulong _before;
        private readonly ulong _after;
        private readonly IReadOnlyList<ulong[]> _tableBefore;
        private readonly IReadOnlyList<ulong[]> _tableAfter;

        public SlotEdit(MaterialEditController owner, SceneNode node, int slotIndex, ulong before, ulong after,
            IReadOnlyList<ulong[]> tableBefore, IReadOnlyList<ulong[]> tableAfter)
        {
            _owner = owner;
            _node = node;
            _slotIndex = slotIndex;
            _before = before;
            _after = after;
            _tableBefore = tableBefore;
            _tableAfter = tableAfter;
        }

        public IEnumerable<SceneNode> Nodes { get { yield return _node; } }
        public void Undo() => _owner.ApplySlot(_node, _slotIndex, _before, _tableBefore);
        public void Redo() => _owner.ApplySlot(_node, _slotIndex, _after, _tableAfter);
    }
}
