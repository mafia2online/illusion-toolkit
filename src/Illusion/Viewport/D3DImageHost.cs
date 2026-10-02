using System.Collections.ObjectModel;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using Illusion.Assets.Adapters;
using Illusion.Assets.Bridge;
using Illusion.Assets.Frames;
using Illusion.Assets.Sds;
using Illusion.Assets.World;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Collisions;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gizmos;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Scene;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// The Mafia map viewport: a <see cref="ViewportControl"/> that owns the Mafia scene tree and loads areas
/// (districts / interiors, whole-map streaming, city_crash). The reusable render pipeline, fly-camera, present
/// and shading modes all live in the base; the Mafia-specific work is split across collaborators —
/// <see cref="SceneTree"/> (tree + filters), <see cref="ViewportCatalogs"/> (map/zones/sky),
/// <see cref="DistrictStreamer"/> (load queue + background pipeline + streaming),
/// <see cref="SelectionController"/> (multi-select + outline + pivot),
/// <see cref="TransformEditController"/> (gizmo drags, undo/redo, delete) and
/// <see cref="ScenePersistence"/> (save/build tracking). This class wires them to the base control and
/// exposes the single facade the UI binds to, including the transform-gizmo host (<see cref="ITransformGizmoHost"/>).
/// </summary>
public sealed class D3DImageHost : ViewportControl, ITransformGizmoHost
{
    internal readonly SceneTree Tree;
    internal readonly ViewportCatalogs Catalogs;
    internal readonly DistrictStreamer Streamer;
    internal readonly SelectionController Selection;
    internal readonly TransformEditController Editing;
    internal readonly CollisionEditController CollisionEditing;
    internal readonly CarCollisionController CarCollisionEditing;
    internal readonly HitBoxController HitBoxes;
    internal readonly CarPartController CarPartEditing;
    internal readonly TranslokatorEditController CrashEditing;
    internal readonly ActorEditController ActorEditing;
    internal readonly ObjectImportController ObjectImporting;
    internal readonly PropertyEditController PropertyEditing;
    internal readonly ScenePersistence Persistence;
    internal readonly GeometryEditController GeometryEditing;
    internal readonly MaterialEditController MaterialEditing;
    internal readonly MaterialThumbnailRenderer MaterialThumbnails;
    internal readonly Bridge.BridgeSessionController BridgeSession;

    public D3DImageHost()
    {
        Tree = new SceneTree();
        Catalogs = new ViewportCatalogs(this);
        Streamer = new DistrictStreamer(this);
        Selection = new SelectionController(this);
        Editing = new TransformEditController(this);
        CollisionEditing = new CollisionEditController(this);
        CarCollisionEditing = new CarCollisionController(this);
        HitBoxes = new HitBoxController(this);
        CarPartEditing = new CarPartController(this);
        CrashEditing = new TranslokatorEditController(this);
        ActorEditing = new ActorEditController(this);
        ObjectImporting = new ObjectImportController(this);
        PropertyEditing = new PropertyEditController(this);
        Persistence = new ScenePersistence(this);
        GeometryEditing = new GeometryEditController(this);
        MaterialEditing = new MaterialEditController(this);
        MaterialThumbnails = new MaterialThumbnailRenderer();
        BridgeSession = new Bridge.BridgeSessionController(this);
        BridgeSession.Notice += (message, isError) => BridgeNotice?.Invoke(message, isError);
    }

    // ── Facade: scene tree ──

    /// <summary>Scene tree roots: folder → SDS → frame hierarchy → mesh. Populated incrementally.</summary>
    public ObservableCollection<SceneNode> Roots => Tree.Roots;

    /// <summary>The same scene without its folder / SDS / FrameResource spine — what a window opened on one
    /// archive shows. See <see cref="SceneTree.StageRoots"/>.</summary>
    public ObservableCollection<SceneNode> StageRoots => Tree.StageRoots;

    public int MeshCount => Tree.MeshCount;

    public event Action? SceneChanged;

    /// <summary>Render tab filters — all off by default: proxy scenes (whole neighbor/proxy districts),
    /// proxy meshes (embedded proxy_ nodes inside a district's main scene), snow scenes (prefix Z).</summary>
    public bool ShowProxyScenes
    {
        get => Tree.ShowProxyScenes;
        set { Tree.ShowProxyScenes = value; Tree.ApplySceneFilters(); }
    }
    public bool ShowProxyMeshes
    {
        get => Tree.ShowProxyMeshes;
        set { Tree.ShowProxyMeshes = value; Tree.ApplySceneFilters(); }
    }
    public bool ShowSnowScenes
    {
        get => Tree.ShowSnowScenes;
        set { Tree.ShowSnowScenes = value; Tree.ApplySceneFilters(); }
    }

    /// <summary>city_crash layer: spawn objects from the Translokator table (instances). Loads and
    /// unloads additively — toggling never resets the rest of the scene.</summary>
    public bool ShowCrash
    {
        get => Streamer.CrashEnabled;
        set
        {
            if (Streamer.CrashEnabled == value) return;
            Streamer.CrashEnabled = value;
            if (Renderer == null) return; // pre-load: LoadArea/EnterStreaming add the layer themselves
            if (value) Streamer.EnqueueCrashLayer();
            else Streamer.RemoveCrashLayer();
        }
    }

    /// <summary>Collision layer: decode each resident district's Collisions (.col) and overlay the hulls as a
    /// translucent, wireframe-edged layer. Loads and unloads additively — toggling never resets the scene.</summary>
    public bool ShowCollision
    {
        get => Streamer.CollisionEnabled;
        set => Streamer.SetCollisionEnabled(value);
    }

    /// <summary>.nov overlay: draw each resident district's AI navigation graph and its AI-mesh boxes (one
    /// toggle). Decoded and uploaded at district load; this only gates drawing (no scene reload).</summary>
    public bool ShowNov
    {
        get => Rnd?.ShowNov ?? false;
        set { if (Rnd != null) Rnd.ShowNov = value; }
    }

    /// <summary>.nav overlay: draw each resident district's AI path objects (cover / vault-over / action
    /// markers) as boxes. Decoded and uploaded at district load; this only gates drawing (no scene reload).</summary>
    public bool ShowNavWorld
    {
        get => Rnd?.ShowNavWorld ?? false;
        set { if (Rnd != null) Rnd.ShowNavWorld = value; }
    }

    /// <summary>Actor glyphs: mark the actors a scene cannot draw — sounds, lights, particles, triggers,
    /// script hooks — where the .act pack puts them, coloured per category. Built and uploaded at district
    /// load; this only gates drawing (no scene reload).</summary>
    public bool ShowActors
    {
        get => Rnd?.ShowActors ?? false;
        set { if (Rnd != null) Rnd.ShowActors = value; }
    }

    /// <summary>Skeletons of skinned models: one glyph per bone, with quiet lines to its parent and to
    /// whatever hangs off it. A car's doors, covers and axles ARE bones — the mesh is one solid body — so
    /// this is the only view in which its parts exist. Built at load; this only gates drawing.</summary>
    public bool ShowSkeleton
    {
        get => Rnd?.ShowSkeleton ?? false;
        set { if (Rnd != null) Rnd.ShowSkeleton = value; }
    }

    /// <summary>Helper nodes — dummies as their own bounding box, points as axes that show which way they
    /// face. These place things without being anything to look at, so without this they exist only in the
    /// tree. Built at load; this only gates drawing.</summary>
    public bool ShowHelpers
    {
        get => Rnd?.ShowHelpers ?? false;
        set { if (Rnd != null) Rnd.ShowHelpers = value; }
    }

    /// <summary>Helper nodes not drawn because they are unnamed placeholders parked at the origin (a car
    /// carries seventeen) — reported rather than silently dropped.</summary>
    public int HiddenHelperCount => Rnd?.HiddenHelperCount ?? 0;

    // ── Facade: catalogs ──

    /// <summary>Main catalog: map areas (districts + interiors from cityareas) for the selector.</summary>
    public IReadOnlyList<MapArea> Areas => Catalogs.Areas;

    public event Action? CatalogReady;

    // ── Facade: loading / streaming ──

    /// <inheritdoc cref="DistrictStreamer.LoadArea"/>
    public void LoadArea(MapArea? area, bool winter, bool wholeMap) => Streamer.LoadArea(area, winter, wholeMap);

    /// <inheritdoc cref="DistrictStreamer.IsBusy"/>
    public bool IsLoading => Streamer.IsBusy;

    /// <inheritdoc cref="DistrictStreamer.LoadStage"/>
    public void LoadStage(FileInfo sds, string label) => Streamer.LoadStage(sds, label);

    // ── Facade: selection ──

    /// <summary>The active selected node (last clicked) — drives the property panel; null when nothing is selected.</summary>
    public SceneNode? SelectedNode => Selection.Active;

    /// <summary>Every selected node (multi-select). The gizmo transforms all of them as a group.</summary>
    public IReadOnlyList<SceneNode> SelectedNodes => Selection.Selected;

    /// <summary>Raised after the selection changes (either source) so the UI can swap property tabs.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised after a transform edit (gizmo drag or numeric field) so the property fields refresh.</summary>
    public event Action? SelectionTransformChanged;

    /// <summary>Raised once per gizmo drag, the first time it actually moves the selection, with the tool used.
    /// The viewport overlay panel uses it to appear (only on a real gizmo edit) showing that tool's vector.</summary>
    public event Action<GizmoMode>? GizmoEdited;

    /// <summary>Nearest mesh under a screen pixel (viewport ray-pick), resolved to its scene-tree node; null on a miss.</summary>
    public SceneNode? Pick(Point screenPos)
    {
        GpuMesh? gm = PickMesh(screenPos, out _);
        return gm?.Owner as SceneNode;
    }

    /// <inheritdoc cref="SelectionController.Select"/>
    /// <remarks>While a Blender edit session is active the mode is modal: ghosted (non-edited)
    /// objects cannot be selected — leave the session (Tab/Esc) first.</remarks>
    public void Select(SceneNode? node)
    {
        if (node != null && BridgeEditedCount > 0 && !BridgeSession.IsEditedNode(node)) return;
        Selection.Select(node);
    }

    /// <inheritdoc cref="SelectionController.ToggleSelect"/>
    public void ToggleSelect(SceneNode node)
    {
        if (BridgeEditedCount > 0 && !BridgeSession.IsEditedNode(node)) return;
        Selection.ToggleSelect(node);
    }

    // ── Facade: editing ──

    /// <summary>Undo/redo stack for object transforms (gizmo drags + numeric-field commits). Cleared on scene reset.</summary>
    public EditHistory History => Editing.History;

    /// <summary>Reverts the last transform edit (gizmo drag or numeric field).</summary>
    public void Undo() => Unwind(Editing.History.Undo, "Undo");

    /// <summary>Re-applies the last undone transform edit.</summary>
    public void Redo() => Unwind(Editing.History.Redo, "Redo");

    /// <summary>
    /// One step of the history, taken while a Blender push cannot be landing.
    ///
    /// <para>
    /// A step is a WRITE to the same frame graph a push reads on its own thread and rewrites on this one — it
    /// puts a mesh back, restores a car's prefab bytes, gives a deleted frame its parents again — so the two
    /// take the same gate the component-level edits take. It is refused rather than made to wait, for the
    /// reason the gate itself gives: this thread is the one the push comes back to.
    /// </para>
    /// </summary>
    private void Unwind(Action step, string what)
    {
        if (!BridgeSession.TryHoldForEdit())
        {
            RaiseNotice($"a push from Blender is landing — {what} again in a moment");
            return;
        }
        try { step(); }
        finally { BridgeSession.ReleaseAfterEdit(); }
    }

    /// <summary>Whether the selection has anything deletable — a frame object or a collision placement.</summary>
    public bool CanDeleteSelection() =>
        Editing.CanDeleteSelection() || CollisionEditing.HasCollisionSelection() || CrashEditing.HasCrashSelection()
        || ActorEditing.HasActorSelection();

    /// <summary>Deletes the selection: collision placements from their .col, frame objects from their
    /// FrameResource — both undoable and both persisted by Save/Build.</summary>
    public void DeleteSelected()
    {
        // An object carried in from another archive takes the collision it was given along — those hulls
        // stand where it stood and are nothing without it.
        List<SceneNode> selected = [.. Selection.Selected];
        List<SceneNode> linked = [.. selected.SelectMany(LinkedCollisionNodes).Where(n => !selected.Contains(n)).Distinct()];
        if (linked.Count > 0) Selection.SetSelection([.. selected, .. linked], SelectedNode);

        int before = Editing.History.UndoCount;
        ActorEditing.DeleteSelected();     // actors (drops their record from the .act pack)
        CollisionEditing.DeleteSelected(); // collision instances (drops them from the selection)
        CrashEditing.DeleteSelected();     // city_crash placements, in both seasons when linked
        Editing.DeleteSelected();          // frame objects (its DeletableRoots excludes collision)
        // One Delete is one Ctrl+Z, however many kinds of thing it took out.
        Editing.History.SquashSince(before, edits => new CompositeEdit(edits));
    }

    /// <summary>
    /// The collision placements a piece of scenery this toolkit carried in was given (<see cref="ImportLinks"/>):
    /// for each recorded hull, the placement of it nearest the object, within a few metres. Empty for everything
    /// else — a stock object's collision is not tied to it by anything, and guessing by position would take a
    /// building's hull along with a bench.
    /// </summary>
    internal IReadOnlyList<SceneNode> LinkedCollisionNodes(SceneNode node)
    {
        if (node.Source is not FrameNodeAdapter frame || node.OwningDocumentNode() is not { Source: SceneDocumentAdapter scene } documentNode)
        {
            return [];
        }
        IReadOnlyList<ulong> hulls = Assets.Sds.ImportLinks.HullsOf(Assets.MafiaEnvironment.ExtractedDir(scene.SourceArchive),
            frame.Frame.Name.String);
        if (hulls.Count == 0 || FindCollisionLayer(documentNode) is not { } layer) return [];

        Vector3 at = frame.WorldTransform.Translation;
        var found = new List<SceneNode>();
        foreach (ulong hash in hulls)
        {
            SceneNode? nearest = layer.Children
                .Where(c => c.Source is CollisionInstanceAdapter ci && ci.Instance.Hash == hash
                            && Vector3.Distance(ci.Instance.Position, at) < 5f)
                .MinBy(c => Vector3.Distance(((CollisionInstanceAdapter)c.Source!).Instance.Position, at));
            if (nearest != null) found.Add(nearest);
        }
        return found;
    }

    /// <summary>Whether the selection has anything duplicable — a static mesh or a collision placement.</summary>
    public bool CanDuplicateSelection() =>
        CollisionEditing.HasCollisionSelection() || CrashEditing.HasCrashSelection() || Editing.CanDuplicateSelection()
        || ActorEditing.HasDuplicableSelection();

    /// <summary>Duplicates the selection: collision placements get copies in their .col, static meshes get
    /// deep, independent copies in their FrameResource — both undoable and persisted.</summary>
    public void DuplicateSelected()
    {
        CollisionEditing.DuplicateSelected(); // collision placements (re-selects the copies)
        CrashEditing.DuplicateSelected();     // city_crash placements (re-selects the copies)
        ActorEditing.DuplicateSelected();     // actors (a copy of the record, under a fresh name)
        Editing.DuplicateSelected();          // frame objects (skips collision sources)
    }

    /// <summary>Fills in the placements of a crash row when its tree branch opens. The copies are not
    /// materialised up front — the shipped city has 57 652 of them, and holding a node for every one costs
    /// memory and every later garbage collection for a branch nobody opened.</summary>
    public void ExpandCrashRow(SceneNode rowNode) => Streamer.ExpandCrashRow(rowNode);

    /// <summary>Whether to draw the physics shapes of the SELECTED part — scoped to the selection because a
    /// car's shapes are what bullets hit, not what it looks like.</summary>
    public bool ShowPartShapes
    {
        get => CarCollisionEditing.ShowShapes;
        set => CarCollisionEditing.ShowShapes = value;
    }

    /// <summary>Whether to draw the per-piece hit boxes — the volumes a shot has to pass before it is tested
    /// against a piece's triangles at all. A whole-scene layer, unlike the physics shapes above: the question
    /// it answers is whether new geometry is inside one, and that is asked of the car, not of a selection.
    /// </summary>
    public bool ShowHitBoxes
    {
        get => HitBoxes.Show;
        set => HitBoxes.Show = value;
    }

    /// <summary>Pieces whose own geometry provably escapes their own hit box — geometry that cannot be shot.
    /// Zero on a stock car; anything else is the layer having found something.</summary>
    public int UnshootablePieceCount => HitBoxes.Escapes;

    /// <summary>
    /// Re-reads the car's physics off disk and redraws it.
    ///
    /// <para>
    /// For an edit that changes a collision volume WITHOUT going through the scene: a size or a position
    /// typed on a component's collision row, a volume removed, and the undo of either. The overlay serves
    /// what it read last, so without this the number changes in the panel and the box on screen does not
    /// move — which reads as "the edit did nothing".
    /// </para>
    /// </summary>
    public void RefreshCarCollisionOverlay() => CarCollisionEditing.AdoptPrefabPlacements();

    // A car's collision is AUTHORED on the component tree — a role, a shape, a size and a position in the
    // component's own space — and the aggregate derives the rest. The surfaces that used to hang a box off a
    // raw bone, turn a volume into another kind and mint a part are gone with the Prefab tab they served:
    // each wrote the prefab on its own, which is the seam ticket 13 closes.

    /// <summary>
    /// The names the OPEN graph of <paramref name="archive"/> answers to, for resolving prefab references.
    /// A frame minted this session is not on disk until Save, so the panel has to be told about it or a part
    /// made a moment ago reads as a bare hash and cannot be picked anywhere else.
    /// </summary>
    public IReadOnlyDictionary<ulong, string> LiveFrameNames(System.IO.FileInfo archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return Staged(archive)?.FrameNames() ?? new Dictionary<ulong, string>();
    }

    /// <summary>
    /// Where the staged car's bones stand — what turns a collision volume's stored position into the car's
    /// own axes for the property panel, and back again when a number is typed in.
    /// </summary>
    public IReadOnlyDictionary<ulong, System.Numerics.Matrix4x4> LiveBoneWorlds(System.IO.FileInfo archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return Staged(archive)?.BoneWorlds() ?? new Dictionary<ulong, System.Numerics.Matrix4x4>();
    }

    private Assets.Adapters.SceneDocumentAdapter? Staged(System.IO.FileInfo archive)
    {
        foreach (Assets.Adapters.SceneDocumentAdapter document in CarCollisionEditing.StageDocuments())
        {
            if (string.Equals(document.SourceArchive.FullName, archive.FullName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return document;
            }
        }
        return null;
    }

    /// <summary>Whether a city_crash archive is loaded, so props can be placed into it.</summary>
    public bool CanPlaceCrashObject => Streamer.CrashLayer != null;

    /// <summary>Whether the loaded crash archive has a seasonal counterpart — without one, "place in all
    /// seasons" has nowhere to place into.</summary>
    public bool HasCrashSeasonTwin => Streamer.CrashLayer?.Document.Twin != null;

    /// <summary>The props the loaded crash archive can place: name, how many copies already stand in the world,
    /// the distance they draw at, and the table row itself (opaque to the UI).</summary>
    public IReadOnlyList<(string Name, int Count, float Distance, object Row)> CrashObjectChoices()
    {
        var choices = new List<(string, int, float, object)>();
        foreach (Formats.Translokator.Object row in CrashEditing.AvailableObjects)
        {
            choices.Add((row.Name.String, row.Instances.Count, row.GridMax, row));
        }
        return choices;
    }

    /// <summary>Places a new copy of a crash prop at a world position. <paramref name="row"/> comes from
    /// <see cref="CrashObjectChoices"/>. Returns false when the archive has no free placement id left.</summary>
    public bool PlaceCrashObject(object row, Vector3 position, bool bothSeasons) =>
        row is Formats.Translokator.Object table
        && CrashEditing.PlaceObject(table, position, bothSeasons) != null;

    /// <summary>
    /// The world point under a screen pixel: where the viewport ray first meets scene geometry (a mesh or a crash
    /// prop), or a point 10 m in front of the camera when it meets nothing — so a click on the sky still places
    /// something the user can see and drag.
    /// </summary>
    public Vector3 PickWorldPoint(Point screenPos)
    {
        (Vector3 origin, Vector3 dir) = BuildViewportRay(screenPos);
        PickMesh(screenPos, out float meshT);
        Streamer.PickCrash(origin, dir, out float crashT);
        float t = MathF.Min(meshT, crashT);
        return origin + dir * (float.IsFinite(t) && t > 0f ? t : 10f);
    }

    /// <inheritdoc cref="CollisionEditController.UnusedHullCount"/>
    public int UnusedHullCount() => CollisionEditing.UnusedHullCount();

    /// <inheritdoc cref="CollisionEditController.RemoveUnusedHulls"/>
    public void RemoveUnusedHulls() => CollisionEditing.RemoveUnusedHulls();

    /// <inheritdoc cref="HitBoxController.RebuildTargetCount"/>
    public int HitBoxRebuildTargetCount() => HitBoxes.RebuildTargetCount();

    /// <inheritdoc cref="HitBoxController.RebuildSelected"/>
    public void RebuildHitBoxes() => HitBoxes.RebuildSelected();

    /// <inheritdoc cref="TransformEditController.Reparent"/>
    public void Reparent(SceneNode node, SceneNode newParent) => Editing.Reparent(node, newParent);

    /// <inheritdoc cref="TransformEditController.RecordTransform"/>
    public void RecordTransform(SceneNode node, Matrix4x4 before, Matrix4x4 after) =>
        Editing.RecordTransform(node, before, after);

    /// <inheritdoc cref="TransformEditController.CommitNodeTransform"/>
    public void CommitNodeTransform(SceneNode node) => Editing.CommitNodeTransform(node);

    /// <inheritdoc cref="PropertyEditController.Commit"/>
    public void CommitPropertyEdit(SceneNode node, PropertyDescriptor descriptor, object? before, object? after) =>
        PropertyEditing.Commit(node, descriptor, before, after);

    /// <summary>Raised after a property edit (or its undo/redo) so a second on-screen editor of the same object
    /// refreshes its values in place.</summary>
    public event Action? SelectionPropertiesChanged;

    internal void RaiseSelectionPropertiesChanged() => SelectionPropertiesChanged?.Invoke();

    // ── Facade: material editing (the Materials tab tiles + the material editor window) ──

    /// <summary>The loaded MTL libraries port — browsing for the material editor window.</summary>
    public Domain.Materials.IMaterialCatalog MaterialCatalog => MaterialEditing.Catalog;

    /// <summary>Raised after any material edit (texture rebind, create/delete, slot reassignment) or its
    /// undo/redo, so the Materials tab tiles and the editor window refresh. UI thread.</summary>
    public event Action? MaterialsChanged;

    internal void RaiseMaterialsChanged() => MaterialsChanged?.Invoke();

    /// <inheritdoc cref="MaterialEditController.SetTexture"/>
    public bool SetMaterialTexture(ulong hash, string slotId, string textureName) =>
        MaterialEditing.SetTexture(hash, slotId, textureName);

    /// <inheritdoc cref="MaterialEditController.AddTextureSlot"/>
    public bool AddMaterialTextureSlot(ulong hash, string slotId) => MaterialEditing.AddTextureSlot(hash, slotId);

    /// <inheritdoc cref="MaterialEditController.RemoveTextureSlot"/>
    public bool RemoveMaterialTextureSlot(ulong hash, string slotId) =>
        MaterialEditing.RemoveTextureSlot(hash, slotId);

    /// <inheritdoc cref="MaterialEditController.SetParameter"/>
    public bool SetMaterialParameter(ulong hash, string paramId, IReadOnlyList<float> values) =>
        MaterialEditing.SetParameter(hash, paramId, values);

    /// <inheritdoc cref="MaterialEditController.AddParameter"/>
    public bool AddMaterialParameter(ulong hash, string paramId, IReadOnlyList<float> values) =>
        MaterialEditing.AddParameter(hash, paramId, values);

    /// <inheritdoc cref="MaterialEditController.CreateMaterial"/>
    public ulong? CreateMaterial(string library, string name) => MaterialEditing.CreateMaterial(library, name);

    /// <inheritdoc cref="MaterialEditController.RenameMaterial"/>
    public ulong? RenameMaterial(ulong hash, string newName) => MaterialEditing.RenameMaterial(hash, newName);

    /// <inheritdoc cref="MaterialEditController.DeleteMaterial"/>
    public bool DeleteMaterial(ulong hash) => MaterialEditing.DeleteMaterial(hash);

    /// <inheritdoc cref="MaterialEditController.AssignSlotMaterial"/>
    public bool AssignSlotMaterial(SceneNode node, int slotIndex, ulong newHash) =>
        MaterialEditing.AssignSlotMaterial(node, slotIndex, newHash);

    /// <summary>Whether <paramref name="node"/> is still part of the loaded scene tree — actions pinned
    /// to a node across scene reloads (the material editor's assign target) validate with this.</summary>
    public bool IsNodeInScene(SceneNode node) => Tree.IsInScene(node);

    /// <inheritdoc cref="MaterialEditController.CountLoadedUses"/>
    public int CountLoadedMaterialUses(ulong hash) => MaterialEditing.CountLoadedUses(hash);

    /// <summary>The map viewport's registered texture folders — the search scope thumbnails and the
    /// editor's preview sphere resolve their .dds names against.</summary>
    public IReadOnlyList<string> TextureFolders => Rnd?.Textures.Folders ?? Array.Empty<string>();

    /// <summary>A cached sphere thumbnail of one material (null when the GPU stack is unavailable).</summary>
    public System.Windows.Media.ImageSource? RenderMaterialThumbnail(Domain.Materials.MaterialInfo info) =>
        MaterialThumbnails.Render(info, TextureFolders);

    // ── Facade: import (File → Import…) ──

    /// <summary>Loaded frame documents (SDS scenes) an import can land in. A hull payload goes to the
    /// .col layer under the same document.</summary>
    public IReadOnlyList<SceneNode> FrameDocumentNodes()
    {
        var result = new List<SceneNode>();
        void Walk(SceneNode node)
        {
            if (node.Source is ISceneDocument and not CollisionDocumentAdapter) result.Add(node);
            foreach (SceneNode c in node.Children) Walk(c);
        }
        foreach (SceneNode root in Tree.Roots) Walk(root);
        return result;
    }

    /// <summary>Whether the archive behind <paramref name="documentNode"/> has a loaded .col layer —
    /// the dialog disables collision rows for a target that cannot take them.</summary>
    public bool HasCollisionLayer(SceneNode documentNode) => FindCollisionLayer(documentNode) != null;

    /// <summary>A sensible landing point for an imported object: a few metres in front of the camera.</summary>
    public Vector3 ImportDropPoint() =>
        Renderer is { } r ? r.Camera.Position + r.Camera.Forward * 15f : Vector3.Zero;

    /// <summary>Outcome of one import: objects landed, hulls landed, and per-item skip reasons.</summary>
    public sealed record ImportReport(int MeshesApplied, int HullsApplied, IReadOnlyList<string> Skipped);

    /// <summary>
    /// Lands a whole imported file — render meshes into the document, hulls into its .col — as ONE
    /// undoable edit, through the same creation pipelines a Blender push uses. Hull cooking happens here
    /// (a subprocess, can take seconds); the caller shows a wait cursor.
    /// </summary>
    public ImportReport ImportBatch(
        SceneNode documentNode,
        IReadOnlyList<MeshObjectPayload> meshes,
        IReadOnlyList<CollisionObjectPayload> hulls)
    {
        var skipped = new List<string>();
        if (documentNode.Source is not ISceneDocument doc || !Tree.IsInScene(documentNode))
        {
            skipped.Add("the target document is no longer loaded");
            return new ImportReport(0, 0, skipped);
        }

        // Hulls first (cooking): each becomes a mint + placement edit pair for the batch below.
        var collisionEdits = new List<IEditAction>();
        int hullsBuilt = 0;
        SceneNode? layer = hulls.Count > 0 ? FindCollisionLayer(documentNode) : null;
        if (hulls.Count > 0 && layer?.Source is not CollisionDocumentAdapter)
        {
            skipped.Add("this archive has no loaded collision (.col) layer — hulls were not imported");
        }
        else if (layer?.Source is CollisionDocumentAdapter colDoc)
        {
            foreach (CollisionObjectPayload payload in hulls)
            {
                CollisionPushAcceptor.Result accepted = CollisionPushAcceptor.TryAccept(colDoc, payload);
                if (accepted.Minted is not { } minted)
                {
                    skipped.Add($"{payload.Name}: {accepted.Refusal ?? "the hull could not be cooked"}");
                    continue;
                }
                if (!TransformMath.TryDecompose(payload.World, out _, out Quaternion rotation, out Vector3 position))
                {
                    skipped.Add($"{payload.Name}: the placement transform could not be read");
                    continue;
                }
                var placement = new CollisionInstance
                {
                    Position = position,
                    Rotation = TransformMath.CollisionEulerFromQuaternion(rotation),
                    Hash = minted.Hash,
                    // A fresh placement owns no visible object; 128 is the stock-data default group (the
                    // same choices the Blender new-hull path makes).
                    Unk4 = -1,
                    Group = 128,
                };
                IReadOnlyList<IEditAction>? edits =
                    CollisionEditing.BuildCreateHull(colDoc, layer, minted.Added, placement, payload.Name);
                if (edits == null)
                {
                    skipped.Add($"{payload.Name}: the collision layer left the scene");
                    continue;
                }
                collisionEdits.AddRange(edits);
                hullsBuilt++;
            }
        }

        var creations = new List<GeometryEditController.CreationItem>(meshes.Count);
        foreach (MeshObjectPayload payload in meshes)
            creations.Add(new GeometryEditController.CreationItem(payload.Id, payload, doc, documentNode));

        List<GeometryEditController.CreationOutcome> outcomes = GeometryEditing.ApplyPushBatch(
            Array.Empty<GeometryEditController.GeometryItem>(),
            Array.Empty<GeometryEditController.TransformItem>(),
            creations, delete: null, collisionEdits);

        int meshesApplied = 0;
        var created = new List<SceneNode>();
        foreach (GeometryEditController.CreationOutcome outcome in outcomes)
        {
            if (outcome.Node != null)
            {
                meshesApplied++;
                created.Add(outcome.Node);
            }
            else
            {
                MeshObjectPayload? source = meshes.FirstOrDefault(m => m.Id == outcome.Id);
                skipped.Add($"{source?.Name ?? outcome.Id}: {outcome.SkipReason ?? "creation failed"}");
            }
        }
        if (created.Count > 0)
        {
            Selection.SetSelection(created, created[^1]);
            created[^1].ExpandAncestors();
        }
        return new ImportReport(meshesApplied, hullsBuilt, skipped);
    }

    // The .col layer loaded under the same document subtree (collision streams in beside its archive).
    private SceneNode? FindCollisionLayer(SceneNode documentNode)
    {
        SceneNode? found = null;
        void Walk(SceneNode node)
        {
            if (found != null) return;
            if (node.Source is CollisionDocumentAdapter) { found = node; return; }
            foreach (SceneNode c in node.Children) Walk(c);
        }
        // The layer may hang beside the frame document under the shared SDS wrapper — search from the
        // document's parent when it has one.
        Walk(documentNode.Parent ?? documentNode);
        return found;
    }

    // ── Facade: Blender bridge ──

    /// <summary>Exports the current selection (children included) into a live Blender session —
    /// the Tab action. Launches Blender when none is connected.</summary>
    public void OpenInBlender() => BridgeSession.OpenInBlender();

    /// <summary>Bridge notices for the UI: (message, isError). Raised on background threads.</summary>
    public event Action<string, bool>? BridgeNotice;

    /// <summary>
    /// A short-lived message for the viewport's notice surface: (message, isError). This is how an edit says it
    /// refused to do something — a hull that cannot be resized, a cook that failed — without a modal dialog
    /// interrupting the drag that caused it. May be raised from any thread.
    /// </summary>
    public event Action<string, bool>? TransientNotice;

    internal void RaiseNotice(string message, bool isError = false) => TransientNotice?.Invoke(message, isError);

    /// <summary>Raised when the bridge edit set changes (objects opened/closed in Blender) so the
    /// title indicator can refresh. May fire on background threads.</summary>
    public event Action? BridgeStateChanged;

    internal void RaiseBridgeStateChanged() => BridgeStateChanged?.Invoke();

    /// <summary>
    /// A push from Blender is about to change the scene. UI thread, and always paired with
    /// <see cref="PushLanded"/> — the push raises the second one even when it fails part way through.
    ///
    /// <para>
    /// Views that describe the scene rather than draw it use the pair as a transaction: a car's components
    /// are stitched out of the frame graph a push rewrites, and stitching one in the middle of the apply
    /// would describe a car whose frames are half swapped. What the modder had selected is worth remembering
    /// here, too — by the time the push has landed, the tree can no longer say what it was looking at.
    /// </para>
    /// </summary>
    public event Action? PushLanding;

    /// <summary>…and it has landed, with the names of the bones it moved. UI thread.</summary>
    public event Action<IReadOnlyList<string>>? PushLanded;

    internal void RaisePushLanding() => PushLanding?.Invoke();

    /// <param name="movedBones">The bones this push wrote a new rest transform into, by name. Empty for a
    /// push that was about geometry alone, which is most of them.</param>
    internal void RaisePushLanded(IReadOnlyList<string> movedBones)
    {
        // Every snapshot on the redo branch was taken against the scene as it stood BEFORE the push, and a
        // push rewrites the very geometry one of them would put its bytes back over. The undo stack stays: it
        // is the way back out, and it is what a modder reaches for when a push went wrong.
        //
        // In a finally, because dropping the branch runs Discard() over actions that release GPU meshes: one
        // of those throwing must not swallow the end of the transaction, or a view that stopped following the
        // scene at PushLanding would never start again.
        try { History.ClearRedo(); }
        finally { PushLanded?.Invoke(movedBones); }
    }

    /// <summary>How many objects are currently open in Blender (0 = no active edit session).</summary>
    public int BridgeEditedCount => BridgeSession.ExportedCount;

    /// <inheritdoc cref="Bridge.BridgeSessionController.RequestPush"/>
    public bool RequestBridgePush() => BridgeSession.RequestPush();

    /// <summary>Ends the Blender edit session (un-ghosts the scene); the Blender side stays open.</summary>
    public void EndBridgeEditSession() => BridgeSession.EndEditSession();

    // ── Facade: persistence (save / build) ──

    /// <summary>Whether there are edits not yet written to disk — frame documents or MTL libraries
    /// (the title shows a '*' while true).</summary>
    public bool HasUnsavedEdits => Persistence.HasUnsavedEdits || MaterialEditing.HasUnsavedMaterials;

    /// <summary>Whether any archive has edits to repack (saved or not) — gates the Build action.</summary>
    public bool HasBuildableEdits => Persistence.HasBuildableEdits;

    /// <summary>Raised when the unsaved/edited state may have changed, so the title '*' and menus can refresh.</summary>
    public event Action? DirtyChanged;

    /// <summary>Writes the edited frame documents AND every dirty MTL library (each .mtl gets a timestamped
    /// backup + atomic replace, like an .sds build). An MTL failure is reported through the notice surface —
    /// not thrown — so the frame save always completes.</summary>
    public int SaveEdits()
    {
        string? materialError = MaterialEditing.SaveDirtyMaterials(out int savedLibraries);
        int saved = Persistence.SaveEdits();
        if (materialError != null) RaiseNotice("Materials not saved: " + materialError, isError: true);
        return saved + savedLibraries;
    }

    /// <inheritdoc cref="ScenePersistence.PendingBuildArchives"/>
    public IReadOnlyList<FileInfo> PendingBuildArchives() => Persistence.PendingBuildArchives();

    /// <inheritdoc cref="ScenePersistence.MarkArchiveModified"/>
    public void MarkArchiveModified(FileInfo sds) => Persistence.MarkArchiveModified(sds);

    /// <summary>One archive that failed to pack during a build (kept buildable so the user can retry).</summary>
    public readonly record struct BuildFailure(string Archive, string Error);

    /// <summary>Outcome of <see cref="BuildEdits"/>: the archives packed (each with its backup) and any that failed.
    /// A build is resilient — one archive failing does not abort the others (already-packed archives are real,
    /// on-disk changes), and a failed archive stays in the edited set so a later Build retries just it.</summary>
    public readonly record struct BuildReport(
        IReadOnlyList<SdsWriter.PackResult> Packed,
        IReadOnlyList<BuildFailure> Failed);

    /// <inheritdoc cref="ScenePersistence.BuildEdits"/>
    public BuildReport BuildEdits(bool createBackup = true) => Persistence.BuildEdits(createBackup);

    /// <inheritdoc cref="DistrictStreamer.ResetForExternalChange"/>
    public void PrepareForArchiveRestore() => Streamer.ResetForExternalChange();

    /// <summary>
    /// Forgets an archive's pending build after it has been rolled back to a backup: its extracted folder has
    /// just been deleted and an older .sds put in its place, so whatever was queued for packing no longer
    /// exists. Without this the Build button would keep offering to repack a working copy that is gone.
    /// </summary>
    public void ForgetPendingBuild(FileInfo sds) => Persistence.ForgetArchive(sds);

    // ── ViewportControl hooks ──

    // Environment (sky) + map catalogs, once the renderer exists. Content arrives via LoadArea, not here.
    /// <summary>
    /// Whether this viewport is a MAP: it offers districts and can stream them by camera position. The
    /// resource editor's stage is not — it is handed one archive at a time, so the city catalogs would be
    /// built for nobody. Set before the control loads; changing it afterwards does nothing.
    /// </summary>
    public bool IsMapViewport { get; set; } = true;

    protected override void OnSceneInitialized() => Catalogs.InitAsync(IsMapViewport);

    // Per-frame scene advancement (before the base moves the camera) — the streamer's pipeline.
    protected override void OnFrameUpdate(float dt) => Streamer.Tick(dt);

    // Left-click on the render surface selects the mesh under the cursor. Ctrl+click toggles it in the
    // multi-selection (a miss is ignored); a plain click replaces the selection (or clears it on a miss).
    protected override void OnViewportLeftClick(Point pos)
    {
        SceneNode? hit = PickNode(pos);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (hit != null) ToggleSelect(hit);
        }
        else Select(hit);
    }

    // ── Glyph hover: what the cursor is over, highlighted and named ──

    /// <summary>The glyph object under the cursor (helper node or bone), or null. Highlighted in the viewport
    /// and named by the label the window shows.</summary>
    public SceneNode? HoveredGlyph { get; private set; }

    /// <summary>Raised when the glyph under the cursor changes: the node (null when the cursor is over nothing
    /// with a glyph) and where the cursor is, so the window can put a name label there.</summary>
    public event Action<SceneNode?, Point>? GlyphHoverChanged;

    // The accent, and a brighter version of it for the one under the cursor. Selection keeps the app's accent
    // colour; hover is the same hue lifted, so the two read as one family rather than two meanings.
    private static readonly Vector4 SelectedGlyphColor = new(0.91f, 0.53f, 0.24f, 0.95f);
    private static readonly Vector4 HoveredGlyphColor = new(1f, 0.80f, 0.45f, 1f);

    private Point _lastHoverPos;

    /// <summary>How far off a glyph a click may still land, when the projection is the parallel one an axis
    /// snap leaves the view in. -1 (grow the allowance with distance) under a perspective one.</summary>
    private float GlyphSlack => Renderer is { } r ? ActorPicking.ParallelSlack(r.Camera) : -1f;

    protected override void OnViewportHover(Point pos)
    {
        _lastHoverPos = pos;
        (Vector3 origin, Vector3 dir) = BuildViewportRay(pos);
        SceneNode? hit = Streamer.PickGlyph(origin, dir, out _, GlyphSlack);
        if (ReferenceEquals(hit, HoveredGlyph))
        {
            // Same object, new cursor position — the label follows the cursor, the highlight does not change.
            if (hit != null) GlyphHoverChanged?.Invoke(hit, pos);
            return;
        }

        HoveredGlyph = hit;
        RefreshGlyphHighlight();
        GlyphHoverChanged?.Invoke(hit, pos);
    }

    protected override void OnViewportHoverLeft()
    {
        if (HoveredGlyph == null) return;
        HoveredGlyph = null;
        RefreshGlyphHighlight();
        GlyphHoverChanged?.Invoke(null, _lastHoverPos);
    }

    /// <summary>
    /// Redraws the accent layer: every selected glyph object plus the one under the cursor. Called on
    /// selection change, on hover change and after an edit moves something — the layer is one immutable
    /// buffer, so it only changes when it is rebuilt.
    /// </summary>
    internal void RefreshGlyphHighlight()
    {
        if (Rnd == null) return;

        var selected = new List<ISceneSource>(Selection.Selected.Count);
        foreach (SceneNode n in Selection.Selected)
        {
            ISceneSource? src = n.Source;
            if (src != null && HelperGlyphBuilder.DrawsGlyph(src)) selected.Add(src);
        }

        HelperGlyphRenderData? accent = selected.Count > 0
            ? HelperGlyphBuilder.BuildHighlight(selected, SelectedGlyphColor)
            : null;
        HelperGlyphRenderData? hover = HoveredGlyph?.Source is { } hovered && !Selection.Contains(HoveredGlyph)
            ? HelperGlyphBuilder.BuildHighlight([hovered], HoveredGlyphColor)
            : null;

        Rnd.SetHelperHighlight(Merge(accent, hover));

        static HelperGlyphRenderData? Merge(HelperGlyphRenderData? a, HelperGlyphRenderData? b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return new HelperGlyphRenderData
            {
                Segments = [.. a.Segments, .. b.Segments],
                GlyphCount = a.GlyphCount + b.GlyphCount,
            };
        }
    }

    // Right-click on the render surface: report what the click hit (mesh or collision hull, or nothing) and
    // where — MainWindow builds the context menu from it (menus are a Views concern, not the viewport's).
    protected override void OnViewportRightClick(Point pos) =>
        ViewportContextMenuRequested?.Invoke(PickNode(pos), pos);

    /// <summary>Raised by a right-click on the render surface with the node under the cursor (null on a miss)
    /// and the click position, so the window can show a context menu for it.</summary>
    public event Action<SceneNode?, Point>? ViewportContextMenuRequested;

    // Nearest node under the cursor across BOTH the frame-mesh pick and the collision-hull pick (the latter only
    // when its overlay is shown — hidden collision isn't pickable). A collision hull wins a tie so, with the
    // overlay up, a hull coincident with its visual mesh is the one selected.
    /// <summary>
    /// The mesh row the last viewport click resolved THROUGH on its way to an actor, or null when the click
    /// did not go that way. Clicking a placed mesh selects the actor that stands there — which loses WHICH of
    /// the prototype's meshes was under the cursor. Keeping it is what lets Tab send the part that was clicked
    /// instead of every part the actor spawns, exactly as clicking a plain mesh sends that mesh.
    /// </summary>
    internal SceneNode? ClickedPrototypeRow { get; private set; }

    private SceneNode? PickNode(Point pos)
    {
        ClickedPrototypeRow = null;
        GpuMesh? gm = PickMesh(pos, out float meshT);
        (Vector3 origin, Vector3 dir) = BuildViewportRay(pos);
        SceneNode? col = Streamer.PickCollision(origin, dir, out float colT);
        // A crash prop is drawn instanced, so the mesh pick can only ever return its whole cloud (and in fact
        // skips it). Picking one copy is its own pass; it wins over the cloud's prototype at the same spot,
        // which is what makes clicking a street lamp select that lamp.
        SceneNode? crash = Streamer.PickCrash(origin, dir, out float crashT);
        // Actor glyphs draw over everything and have no geometry of their own, so they are tested separately
        // and win outright: clicking a marker you can see selects that actor, whatever stands in front of it.
        SceneNode? actor = Streamer.Actors.Pick(origin, dir, out _, GlyphSlack);
        if (actor != null) return actor;

        // Helper glyphs and bones are drawn over the scene for the same reason, and are picked the same way:
        // what you can see, you can click. Only what is actually drawn is tested (see PickGlyph), so a hidden
        // layer never swallows a click meant for the geometry behind it.
        SceneNode? glyph = Streamer.PickGlyph(origin, dir, out _, GlyphSlack);
        if (glyph != null) return glyph;

        if (col != null && (gm == null || colT <= meshT) && (crash == null || colT <= crashT)) return col;
        if (crash != null && (gm == null || crashT <= meshT)) return crash;

        // A mesh an actor places is that actor's prototype — the actor is what stands there and what governs it,
        // so clicking the bottle selects the bottle's actor, not the frame object it was instanced from. The
        // frame itself is still reachable through the FrameResource branch of the tree.
        if (gm?.Owner is SceneNode meshNode)
        {
            if (meshNode.Source is FrameNodeAdapter fna && Streamer.Actors.ActorRowFor(fna.Frame) is { } owner)
            {
                ClickedPrototypeRow = meshNode;
                return owner;
            }
            return meshNode;
        }
        return null;
    }

    public override void Dispose()
    {
        BridgeSession.Dispose(); // says bye to Blender; the Blender process itself stays up
        MaterialThumbnails.Dispose(); // its own GPU stack, independent of the (possibly deferred) main one
        History.Clear(); // release any still-applied delete's detached-but-held meshes (base.Dispose only frees attached ones)
        if (Streamer.ShutdownDeferred(TearDown)) return; // GPU teardown continues after the stuck loader ends
        base.Dispose(); // Renderer.Dispose releases the attached meshes
    }

    // ── Transform gizmo host (ITransformGizmoHost) ──

    public Matrix4x4 GizmoViewProjection => Renderer?.Camera.ViewProjection ?? Matrix4x4.Identity;
    public Vector3 GizmoCameraPosition => Renderer?.Camera.Position ?? Vector3.Zero;
    public Vector3? GizmoParallelDir => Renderer is { Camera.Orthographic: true } r ? r.Camera.Forward : null;

    /// <summary>Active manipulation tool (driven by the viewport tool shelf). None = select-only.</summary>
    public GizmoMode GizmoMode { get; set; } = GizmoMode.None;

    /// <summary>True when at least one transformable frame object is selected and a manipulation tool is active.</summary>
    public bool HasGizmoTarget => GizmoMode != GizmoMode.None && Selection.AnyTransformable();

    /// <summary>True when something transformable is selected, whatever the tool shelf says — the modal
    /// transforms are keyboard-started and work under the Select tool too.</summary>
    public bool CanTransformSelection => Selection.AnyTransformable();

    /// <summary>World pivot the gizmo sits at (cached group centroid — see SelectionController).</summary>
    public Vector3 GizmoPivot => Selection.GizmoPivot;

    /// <inheritdoc cref="TransformEditController.GizmoBeginDrag"/>
    public void GizmoBeginDrag(GizmoMode mode) => Editing.GizmoBeginDrag(mode);

    /// <inheritdoc cref="TransformEditController.LastGizmoBaseline"/>
    internal TransformEditController.GizmoBaseline? LastGizmoBaseline => Editing.LastGizmoBaseline;

    /// <inheritdoc cref="TransformEditController.ClearGizmoBaseline"/>
    internal void ClearGizmoBaseline() => Editing.ClearGizmoBaseline();

    /// <inheritdoc cref="TransformEditController.GizmoApplyWorldDelta"/>
    public void GizmoApplyWorldDelta(Matrix4x4 totalWorldDelta) => Editing.GizmoApplyWorldDelta(totalWorldDelta);

    /// <inheritdoc cref="TransformEditController.GizmoEndDrag"/>
    public void GizmoEndDrag() => Editing.GizmoEndDrag();

    /// <inheritdoc cref="TransformEditController.GizmoCancelDrag"/>
    public void GizmoCancelDrag() => Editing.GizmoCancelDrag();

    /// <summary>Frames the selection: tweens the camera onto the selected objects' combined bounds and makes
    /// their centre the point the camera orbits and zooms around. False when nothing measurable is selected.</summary>
    public bool FrameSelection()
    {
        if (!Selection.TryGetSelectionBounds(out Vector3 min, out Vector3 max)) return false;
        FrameOn((min + max) * 0.5f, (max - min).Length() * 0.5f);
        return true;
    }

    // ── Collaborator plumbing ──

    /// <summary>The base's protected renderer, surfaced to the collaborators.</summary>
    internal SceneRenderer? Rnd => Renderer;

    // Position the camera so the whole district fits in frame (by world-AABB of ready meshes).
    internal void FrameCameraOver(List<GpuMesh> meshes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (GpuMesh m in meshes)
        {
            min = Vector3.Min(min, m.BoundsMin);
            max = Vector3.Max(max, m.BoundsMax);
        }
        if (min.X > max.X) return; // empty

        Vector3 center = (min + max) * 0.5f;
        float radius = (max - min).Length() * 0.5f;
        Vector3 eye = center + new Vector3(0f, -radius * 1.3f, radius * 0.8f);
        Renderer!.Camera.LookAt(eye, center);
        Renderer.Camera.Far = radius * 8f + 5000f;
        _orbitDistance = MathF.Max(radius * 1.5f, 5f); // pivot ≈ scene center for gizmo snap / orbit
    }

    // The hit-box layer is read straight off the models, so it goes stale whenever the stage does: a car
    // arriving, a scene reset, geometry pushed. Rebuilding it is one walk and one buffer upload.
    internal void RaiseSceneChanged()
    {
        HitBoxes.Redraw();
        SceneChanged?.Invoke();
    }
    internal void RaiseCatalogReady() => CatalogReady?.Invoke();
    // The part-shape overlay follows the selection and the gizmo: it draws one part's shapes, so it is stale
    // the moment either changes. Cheap — a handful of stubs and a dozen small files.
    internal void RaiseSelectionChanged()
    {
        CarCollisionEditing.RefreshOverlay();
        SelectionChanged?.Invoke();
    }

    internal void RaiseSelectionTransformChanged()
    {
        CarCollisionEditing.RefreshOverlay();
        SelectionTransformChanged?.Invoke();
    }
    internal void RaiseGizmoEdited(GizmoMode mode) => GizmoEdited?.Invoke(mode);
    internal void RaiseDirtyChanged() => DirtyChanged?.Invoke();
}
