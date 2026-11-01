using Illusion.Assets;
using Illusion.Assets.Bridge;
using Illusion.Assets.Compiled;
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

    private bool AllowDependencyMutation(ulong hash)
    {
        if (CompiledProxyMaterialGuard.ValidateMaterialMutation(hash) is not { } refusal) return true;
        _host.RaiseNotice(refusal, isError: true); return false;
    }

    private static void EnsureDependencyMutation(ulong hash)
    {
        if (CompiledProxyMaterialGuard.ValidateMaterialMutation(hash) is { } refusal) throw new EditRefusedException(refusal);
    }

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
        if (!AllowDependencyMutation(hash)) return false;
        if (!Catalog.SetTexture(hash, slotId, textureName)) return false;
        _host.Editing.History.Push(new TextureEdit(this, hash, slotId, before, textureName));
        AfterMaterialChanged(hash);
        return true;
    }

    private void ApplyTexture(ulong hash, string slotId, string value)
    {
        EnsureDependencyMutation(hash);
        if (Catalog.SetTexture(hash, slotId, value)) AfterMaterialChanged(hash);
    }

    // ── Sampler slots (add / remove) ──

    /// <summary>Adds an empty texture slot to a material (undoable). False when it already exists.</summary>
    public bool AddTextureSlot(ulong hash, string slotId)
    {
        if (!AllowDependencyMutation(hash)) return false;
        if (!Catalog.AddSampler(hash, slotId)) return false;
        _host.Editing.History.Push(new AddSlotEdit(this, hash, slotId));
        AfterMaterialChanged(hash);
        return true;
    }

    /// <summary>Removes a texture slot from a material (undoable — the exact sampler is restorable).</summary>
    public bool RemoveTextureSlot(ulong hash, string slotId)
    {
        if (!AllowDependencyMutation(hash)) return false;
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
        if (!AllowDependencyMutation(hash)) return null;
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
        EnsureDependencyMutation(fromHash); EnsureDependencyMutation(toHash);
        if (Catalog.RenameMaterial(fromHash, toName, toHash) != null) AfterMaterialRenamed(fromHash, toHash);
    }

    /// <summary>Deletes a material from its library (undoable — the exact instance is restorable).
    /// Loaded meshes that still reference the hash fall back to placeholder textures.</summary>
    public bool DeleteMaterial(ulong hash)
    {
        if (!AllowDependencyMutation(hash)) return false;
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
        if ((node.GeometrySource ?? node.Source) is not IMaterialSlotEditor editor || !_host.Tree.IsInScene(node)) return false;
        if (SlotAssignObstacle(node) != null) return false;
        var compiledSource = (node.GeometrySource ?? node.Source) as Assets.Compiled.ICompiledMeshSource;
        IReadOnlyList<ulong[]> tableBefore = editor.GetSlotTable();
        int level = compiledSource is null || tableBefore.Count == 0 ? 0 : Math.Clamp(node.Lod, 0, tableBefore.Count - 1);
        ulong? before = compiledSource is not null
            ? tableBefore.Count > level && (uint)slotIndex < tableBefore[level].Length ? tableBefore[level][slotIndex] : null
            : editor.GetSlotMaterial(slotIndex);
        if (before == null) return false;
        if (before == newHash) return true;
        IEditAction? ownership = null;
        if (compiledSource?.GeometryBinding is { } binding
            && node.OwningDocumentNode()?.Source is Assets.Compiled.CompiledSceneDocument document
            && document.GetLinkedProxyMaterialVisualTargets(compiledSource.CanonicalFrame, binding.Visual).Count > 0)
        {
            ownership = document.EnsurePreparedLinkedProxyMaterialOwnership(compiledSource.CanonicalFrame, binding.Visual, out var refusal);
            if (refusal is not null) return false;
        }
        bool applied;
        try
        {
            if (compiledSource is not null)
            {
                var table = tableBefore.Select(row => row.ToArray()).ToArray();
                for (int lod = 0; lod < table.Length; lod++)
                    for (int slot = 0; slot < table[lod].Length; slot++) if (table[lod][slot] == before.Value) table[lod][slot] = newHash;
                applied = editor.SetSlotTable(table);
            }
            else applied = editor.SetSlotMaterial(slotIndex, newHash);
        }
        catch { ownership?.Undo(); throw; }
        if (!applied) { ownership?.Undo(); return false; }
        var edit = new SlotEdit(this, node, slotIndex, before.Value, newHash, tableBefore, editor.GetSlotTable());
        _host.Editing.History.Push(ownership is null ? edit : new CompositeEdit([ownership, edit]));
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
        var source = node.GeometrySource ?? node.Source;
        var frame = source is Assets.Compiled.ICompiledMeshSource visual ? visual.CanonicalFrame : source as IFrameNode;
        if (frame is not null && node.OwningDocumentNode()?.Source is Assets.Compiled.CompiledSceneDocument document
            && document.ValidateLinkedProxyMaterialSlots(frame) is { } refusal) return refusal;
        if ((node.GeometrySource ?? node.Source) is Assets.Compiled.ICompiledMeshSource && (node.GeometrySource ?? node.Source) is IMaterialSlotEditor compiled
            && compiled.GetSlotTable().Count == 0)
            return "Select an independent compiled visual with supported face groups.";
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
        if ((node.GeometrySource ?? node.Source) is not IMaterialSlotEditor editor || !_host.Tree.IsInScene(node)) return;
        bool compiled = (node.GeometrySource ?? node.Source) is Assets.Compiled.ICompiledMeshSource;
        if (editor.SetSlotTable(table) || !compiled && editor.SetSlotMaterial(slotIndex, hash)) AfterSlotChanged(node, slotIndex, hash);
        else if (compiled)
            throw new EditRefusedException((node.OwningDocumentNode()?.Source as Assets.Compiled.CompiledSceneDocument)?.LinkedProxyMaterialRefusal
                ?? "The native material slot action is no longer valid.");
    }

    // ── Shared after-effects ──

    // A material's slots changed (or it appeared/disappeared): re-resolve every loaded part bound to the
    // hash, then let the UI (Materials tab, editor window) and the title '*' refresh.
    private void AfterMaterialChanged(ulong hash)
    {
        RefreshMeshesUsing(hash);
        RefreshNativeWaterRuntime(hash);
        _host.RaiseMaterialsChanged();
        _host.RaiseDirtyChanged();
    }

    private void RefreshNativeWaterRuntime(ulong hash)
    {
        if (hash == 0x1A227ADE53424B91 && _host.Rnd is { } renderer)
        {
            var detail = Catalog.GetParameter(hash, "D267");
            renderer.SetNativeWaterFineNormalParameters(detail is { Count: >= 4 } ? detail[1] : float.NaN,
                detail is { Count: >= 4 } ? detail[3] : float.NaN);
            var depth = Catalog.GetParameter(hash, "D261");
            renderer.SetNativeWaterDepthThresholds(depth is { Count: 2 } ? new(depth[0], depth[1]) : new(float.NaN));
        }
        if (hash is 0x1A227ADE53424B91 or 0x0B2348BC137E2526 or 0x304B24141FEA1E4B && _host.Rnd is { } foamRenderer)
        {
            foamRenderer.InvalidateNativeWaterFoamInputs();
            if (foamRenderer.NativeWaterFoamEnabled && _host.ReloadNativeWaterFoamInputs() is { } foamError)
                _host.RaiseNotice(foamError, isError: true);
        }
    }

    // A rename moves the material to a new hash identity: parts bound to either side re-resolve (the old
    // hash falls back to placeholders, the new one picks the material up).
    private void AfterMaterialRenamed(ulong oldHash, ulong newHash)
    {
        RefreshMeshesUsing(oldHash);
        RefreshMeshesUsing(newHash);
        RefreshNativeWaterRuntime(oldHash);
        if (newHash != oldHash) RefreshNativeWaterRuntime(newHash);
        _host.RaiseMaterialsChanged();
        _host.RaiseDirtyChanged();
    }

    private void AfterSlotChanged(SceneNode node, int slotIndex, ulong hash)
    {
        _host.Persistence.MarkFrameModified(node);
        var materialSource = node.GeometrySource ?? node.Source;
        if (materialSource is Assets.Compiled.ICompiledMeshSource && materialSource is IMaterialSlotEditor compiled && _host.Rnd is { } renderer)
        {
            var targets = new HashSet<Formats.Compiled.CompiledElement>();
            if (materialSource is Assets.Compiled.ICompiledMeshSource { GeometryBinding: { } fineBinding } fineSource
                && node.OwningDocumentNode()?.Source is Assets.Compiled.CompiledSceneDocument nativeDocument)
                targets.UnionWith(nativeDocument.GetLinkedProxyMaterialVisualTargets(fineSource.CanonicalFrame, fineBinding.Visual));
            bool Matches(SceneNode row) => ReferenceEquals(row.GeometrySource ?? row.Source, materialSource)
                || (row.GeometrySource ?? row.Source) is Assets.Compiled.ICompiledMeshSource { GeometryBinding: { } binding } && targets.Contains(binding.Visual);
            IReadOnlyList<ulong[]> Table(SceneNode row) => (row.GeometrySource ?? row.Source) is IMaterialSlotEditor target ? target.GetSlotTable() : [];
            void RefreshPending(SceneNode row)
            {
                var table = Table(row);
                if (Matches(row) && row.Pending is { } pending && table.Count > 0)
                {
                    var hashes = table[Math.Clamp(row.Lod, 0, table.Count - 1)];
                    for (int slot = 0; slot < Math.Min(hashes.Length, pending.Parts.Length); slot++)
                    {
                        var part = pending.Parts[slot];
                        pending.Parts[slot] = Appearance(hashes[slot], row, pending.Colors is not null, part.StartIndex, part.IndexCount);
                    }
                }
                foreach (SceneNode child in row.Children) RefreshPending(child);
            }
            if (node.OwningDocumentNode() is { } document) RefreshPending(document);
            foreach (GpuMesh mesh in renderer.Meshes)
                if (mesh.Owner is SceneNode owner && Matches(owner) && Table(owner) is { Count: > 0 } table)
                {
                    var hashes = table[Math.Clamp(owner.Lod, 0, table.Count - 1)];
                    for (int slot = 0; slot < hashes.Length; slot++)
                    {
                        var appearance = Appearance(hashes[slot], owner, mesh.HasVertexColors);
                        mesh.SetPartMaterial(slot, hashes[slot], appearance.DiffuseTexture, appearance.NormalTexture,
                            appearance.SpecularTexture, appearance.Tint, appearance.Blended, appearance.Surface);
                    }
                }
        }
        else if (node.Mesh is { } gm)
        {
            var appearance = Appearance(hash, node, gm.HasVertexColors);
            gm.SetPartMaterial(slotIndex, hash, appearance.DiffuseTexture, appearance.NormalTexture,
                appearance.SpecularTexture, appearance.Tint, appearance.Blended, appearance.Surface);
        }
        _host.RaiseMaterialsChanged();
        _host.RaiseSelectionPropertiesChanged();
    }

    private static SurfaceMaterial? Surface(ulong hash) => MafiaEnvironment.Profile == Illusion.Formats.GameProfile.MafiaIII
        ? Assets.Compiled.CompiledSurfaceMaterials.Terrain(hash, slot =>
        {
            var texture = MafiaMaterials.Collection?.FindByHash(hash)?.GetTextureByID(slot);
            return texture is null ? null : Assets.Textures.TextureSearchIndex.FindPath(texture.String) ?? texture.String;
        }) : null;

    private static MeshPart Appearance(ulong hash, SceneNode? node, bool hasColors, int start = 0, int count = 0)
    {
        var textures = MafiaMaterials.GetMaterialTextures(hash);
        var part = new MeshPart(start, count, textures.Diffuse, textures.Normal, textures.Specular,
            hash, textures.Tint, textures.Blended, Surface(hash));
        if (node?.OwningDocumentNode()?.Source is Assets.Compiled.CompiledSceneDocument document)
        {
            var source = node.GeometrySource ?? node.Source;
            var owner = source is Assets.Compiled.ICompiledMeshSource compiled ? compiled.CanonicalFrame : source as IFrameNode;
            part = document.ApplyVehiclePaint(new MeshData { Parts = [part], Colors = hasColors ? [System.Numerics.Vector4.One] : null }, owner).Parts[0];
        }
        return part;
    }

    internal void RefreshMeshesUsing(ulong hash)
    {
        if (_host.Rnd is not { } renderer) return;
        foreach (GpuMesh gm in renderer.Meshes)
        {
            var appearance = Appearance(hash, gm.Owner as SceneNode, gm.HasVertexColors);
            gm.RebindPartTextures(hash, appearance.DiffuseTexture, appearance.NormalTexture,
                appearance.SpecularTexture, appearance.Tint, appearance.Blended, appearance.Surface);
        }
        void RefreshPending(SceneNode row)
        {
            if (row.Pending is { } pending)
                for (int i = 0; i < pending.Parts.Length; i++)
                    if (pending.Parts[i].MaterialHash == hash)
                    {
                        var part = pending.Parts[i];
                        pending.Parts[i] = Appearance(hash, row, pending.Colors is not null, part.StartIndex, part.IndexCount);
                    }
            foreach (var child in row.Children) RefreshPending(child);
        }
        foreach (var row in _host.Roots.Concat(_host.StageRoots).Distinct()) RefreshPending(row);
    }

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
            EnsureDependencyMutation(_hash);
            _token = _owner.Catalog.RemoveSampler(_hash, _slotId);
            _owner.AfterMaterialChanged(_hash);
        }

        public void Redo()
        {
            EnsureDependencyMutation(_hash);
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
            EnsureDependencyMutation(_hash);
            if (_token != null && _owner.Catalog.RestoreSampler(_hash, _token)) _owner.AfterMaterialChanged(_hash);
        }

        public void Redo()
        {
            EnsureDependencyMutation(_hash);
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
            EnsureDependencyMutation(_hash);
            _token = _owner.Catalog.RemoveMaterial(_hash);
            _owner.AfterMaterialChanged(_hash);
        }

        public void Redo()
        {
            EnsureDependencyMutation(_hash);
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
            EnsureDependencyMutation(_hash);
            if (_token != null && _owner.Catalog.RestoreMaterial(_token)) _owner.AfterMaterialChanged(_hash);
        }

        public void Redo()
        {
            EnsureDependencyMutation(_hash);
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
        private void Validate(IReadOnlyList<ulong[]> expected, IReadOnlyList<ulong[]> requested)
        {
            if ((_node.GeometrySource ?? _node.Source) is not Assets.Compiled.ICompiledMeshSource source) return;
            if (!_owner._host.Tree.IsInScene(_node) || source is not IMaterialSlotEditor editor)
                throw new EditRefusedException("The native material owner is no longer in this scene.");
            var current = editor.GetSlotTable();
            if (current.Count != expected.Count || current.Where((row, index) => !row.SequenceEqual(expected[index])).Any())
                throw new EditRefusedException("The native material table changed after this slot edit.");
            if (_node.OwningDocumentNode()?.Source is Assets.Compiled.CompiledSceneDocument document
                && document.ValidatePreparedLinkedProxyMaterialTable(source.CanonicalFrame, requested) is { } refusal)
                throw new EditRefusedException(refusal);
        }
        public void ValidateUndo() => Validate(_tableAfter, _tableBefore);
        public void ValidateRedo() => Validate(_tableBefore, _tableAfter);
        public void Undo() { ValidateUndo(); _owner.ApplySlot(_node, _slotIndex, _before, _tableBefore); }
        public void Redo() { ValidateRedo(); _owner.ApplySlot(_node, _slotIndex, _after, _tableAfter); }
    }
}
