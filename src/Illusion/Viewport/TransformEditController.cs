using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Frames;
using Illusion.Domain;
using Illusion.Rendering.Gizmos;
using Illusion.Rendering.Gpu;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>An edit that references scene nodes — lets the streamer prune edits whose objects have all left the scene.</summary>
internal interface INodeEdit : IEditAction
{
    IEnumerable<SceneNode> Nodes { get; }
}

/// <summary>
/// Transform editing of the viewport: the gizmo drag lifecycle (group snapshots → world delta → one
/// undoable edit), numeric-field commits, undoable delete/restore of selected subtrees, and the shared
/// undo/redo <see cref="History"/>.
/// </summary>
internal sealed class TransformEditController
{
    private readonly D3DImageHost _host;

    public TransformEditController(D3DImageHost host) => _host = host;

    /// <summary>Undo/redo stack for object transforms (gizmo drags + numeric-field commits). Cleared on scene reset.</summary>
    public EditHistory History { get; } = new();

    // Per-object snapshots captured at gizmo drag-start (world matrix to apply the total delta against — never
    // accumulated, so a drag never drifts — and the local matrix, so the whole group drag is one undoable edit).
    private readonly List<(SceneNode Node, Matrix4x4 OriginalWorld, Matrix4x4 BeforeLocal)> _dragGroup = new();

    private bool _gizmoMoved;                       // whether GizmoEdited already fired for the current drag
    private GizmoMode _dragMode = GizmoMode.None;   // what the grabbed handle does, NOT what the tool shelf says
    private SceneNode? _baselineNode;               // the active object, and where it stood before this drag
    private Matrix4x4 _baselineLocal;

    /// <summary>Where the active object stood before the last gizmo drag started, decomposed. The viewport
    /// overlay shows the change against it, and an amount typed into that overlay is applied to it — so the
    /// number on screen always means "this much of a change", however many times it is retyped.</summary>
    public sealed record GizmoBaseline(SceneNode Node, Vector3 Position, Vector3 RotationDeg, Vector3 Scale);

    /// <summary>The baseline of the last drag, or null when none has happened for the current selection.</summary>
    public GizmoBaseline? LastGizmoBaseline { get; private set; }

    /// <summary>Forgets the baseline — the overlay is about one object's one transform, and a different object
    /// is a different story.</summary>
    public void ClearGizmoBaseline() => LastGizmoBaseline = null;

    // ── Gizmo drag ──

    /// <summary>Snapshots every selected frame's world + local matrices, so the whole group drag applies against
    /// fixed originals (no drift) and becomes a single undoable edit. Only the TOP-MOST selected frame of each
    /// parent chain is transformed: a frame's SetWorldTransform cascade already moves its selected descendants,
    /// so transforming a descendant too would apply the delta twice and break the group's rigidity.</summary>
    public void GizmoBeginDrag(GizmoMode mode)
    {
        _dragGroup.Clear();
        _gizmoMoved = false;
        _dragMode = mode;

        // Snapshot the ACTIVE object — the one whose numbers the panels show — before anything moves. It is
        // not always in the drag group: a selected descendant is carried by its ancestor's cascade and its own
        // local transform never changes, which is exactly the "no change" the overlay should then report.
        _baselineNode = _host.SelectedNode;
        _baselineLocal = _baselineNode?.Source is IFrameNode active ? active.LocalTransform : Matrix4x4.Identity;
        var frames = new HashSet<IFrameNode>();
        foreach (SceneNode n in _host.Selection.Selected) if (n.Source is IFrameNode fn) frames.Add(fn);
        foreach (SceneNode n in _host.Selection.Selected)
        {
            if (n.Source is not IFrameNode fn || HasSelectedAncestor(fn, frames)) continue;
            _dragGroup.Add((n, fn.WorldTransform, fn.LocalTransform));
        }

        // An object carried in from another archive drags the collision it was given along with it: nothing in
        // the file ties the two, so without this the hull would stay where the object used to stand.
        var dragged = new HashSet<SceneNode>(_dragGroup.Select(g => g.Node));
        // The placements the user took hold of themselves, before the ones that only ride along are added:
        // changed on their own, they are still their object's, and its record has to be kept true.
        _dragLinks = _host.LinksOf(dragged);
        foreach (List<D3DImageHost.LinkedPlacement> riding in _host.LinkedSlots(dragged.ToList()).Values)
        {
            foreach (D3DImageHost.LinkedPlacement placement in riding)
            {
                if (placement.Hull.Source is not IFrameNode h || !dragged.Add(placement.Hull)) continue;
                _dragGroup.Add((placement.Hull, h.WorldTransform, h.LocalTransform));
                _dragLinks.Add(placement);
            }
        }
    }

    // The linked placements a drag has hold of — riding with their object, or taken on their own.
    private List<D3DImageHost.LinkedPlacement> _dragLinks = [];

    /// <summary>
    /// Brings the records of imported objects in line with their placements after a change to either. A
    /// resize re-cooks a hull under a new hash; a placement moved on its own stands somewhere else in its
    /// object's space. The record names both, and unless it follows, the object has lost its collision by the
    /// next operation — or, worse, claims some other placement of the old hull that happens to stand near.
    /// Rewritten as edits, to go into the same undo step as what caused them. Nothing is written for a
    /// placement that only moved together with its object: its place in the object's space is what it was.
    /// </summary>
    internal List<IEditAction> Relink(IReadOnlyList<D3DImageHost.LinkedPlacement> links)
    {
        var edits = new List<IEditAction>();
        foreach (var record in links.GroupBy(l => (l.Dir, l.Name)))
        {
            List<Assets.Sds.ImportLinks.Link> entries = [.. Assets.Sds.ImportLinks.HullsOf(record.Key.Dir, record.Key.Name)];
            bool changed = false;
            foreach (D3DImageHost.LinkedPlacement link in record)
            {
                if (link.Slot >= entries.Count || link.Hull.Source is not CollisionInstanceAdapter placed) continue;
                Assets.Sds.ImportLinks.Link now = D3DImageHost.LinkFor(link.Owner, placed.Instance);
                Assets.Sds.ImportLinks.Link was = entries[link.Slot];
                // A record from before places were kept gains one here, the first time anything is done to
                // the placement: found by nearness this once, it is found by its place from now on — and
                // without it a hull of such a record dragged a little too far on its own was lost.
                bool samePlace = was.At is { } before && now.At is { } after && Vector3.Distance(before, after) < 0.01f;
                if (was.Hull == now.Hull && (samePlace || now.At == null)) continue;
                entries[link.Slot] = now.At == null ? was with { Hull = now.Hull } : now;
                changed = true;
            }
            if (!changed) continue;
            var edit = new ImportLinkEdit(record.Key.Dir, record.Key.Name, entries);
            edit.Redo();
            edits.Add(edit);
        }
        return edits;
    }

    /// <summary>
    /// Moves placements that ride with an object by the change <paramref name="delta"/> made to it, and
    /// re-cooks the ones a resize reached (a placement cannot store a scale). Returns what moved, for the
    /// caller's undo entry; <paramref name="minted"/> are the re-cook edits, already applied.
    /// </summary>
    /// <param name="skip">Placements that are not to be carried: ones the caller places itself.</param>
    internal List<(SceneNode Node, Matrix4x4 Before, Matrix4x4 After)> MoveRiders(
        IEnumerable<D3DImageHost.LinkedPlacement> riders, Matrix4x4 delta, Func<SceneNode, bool>? skip,
        out List<IEditAction> minted)
    {
        var hulls = new List<(SceneNode Node, Matrix4x4 Before)>();
        foreach (D3DImageHost.LinkedPlacement link in riders)
        {
            if (link.Hull.Source is not IFrameNode h || skip?.Invoke(link.Hull) == true
                || hulls.Any(x => ReferenceEquals(x.Node, link.Hull)))
            {
                continue;
            }
            hulls.Add((link.Hull, h.LocalTransform));
            h.LocalTransform = TransformOps.WorldDeltaToLocal(h.WorldTransform, h.ParentWorldTransform, delta);
            SyncNodeMeshes(link.Hull);
        }
        minted = hulls.Count == 0 ? [] : [.. _host.CollisionEditing.MintPreviewedScales(hulls.Select(x => x.Node).ToList())];
        return [.. hulls.Select(x => (x.Node, x.Before, ((IFrameNode)x.Node.Source!).LocalTransform))];
    }

    // True if any frame-graph ancestor of fn is itself selected (its cascade will move fn).
    private static bool HasSelectedAncestor(IFrameNode fn, HashSet<IFrameNode> selected)
    {
        for (IFrameNode? p = fn.Parent; p != null; p = p.Parent)
            if (selected.Contains(p)) return true;
        return false;
    }

    /// <summary>Applies the total (from drag-start) world-space delta to EVERY selected frame — the group moves,
    /// rotates or scales rigidly about the shared pivot — and resyncs their meshes.</summary>
    public void GizmoApplyWorldDelta(Matrix4x4 totalWorldDelta)
    {
        if (_dragGroup.Count == 0) return;
        foreach ((SceneNode node, Matrix4x4 originalWorld, _) in _dragGroup)
        {
            if (node.Source is not IFrameNode fn) continue;
            fn.LocalTransform = TransformOps.WorldDeltaToLocal(originalWorld, fn.ParentWorldTransform, totalWorldDelta);
            SyncNodeMeshes(node);
        }
        _host.Selection.UpdateSelectionHighlight();
        _host.RaiseSelectionTransformChanged();
        // A car's collision overlay is drawn where its STUB stands, so dragging the stub has to redraw it.
        // Without this the shape sits still while the gizmo moves away from it, which reads as "the gizmo
        // does nothing" — the frame really did move, only nothing repainted the lines.
        _host.CarCollisionEditing.RefreshOverlayWhileDragging();
        // First real move → publish the baseline and reveal the panel. The mode is the DRAG's, not the tool
        // shelf's: a keyboard-started scale leaves the shelf on whatever it was, and asking the shelf is what
        // used to label a resize "Position".
        if (!_gizmoMoved)
        {
            _gizmoMoved = true;
            CaptureGizmoBaseline();
            _host.RaiseGizmoEdited(_dragMode);
        }
    }

    private void CaptureGizmoBaseline()
    {
        LastGizmoBaseline =
            _baselineNode is { } node
            && TransformMath.TryDecompose(_baselineLocal, out Vector3 scale, out Quaternion rot, out Vector3 pos)
                ? new GizmoBaseline(node, pos, TransformOps.QuatToEulerDeg(rot), scale)
                : null;
    }

    /// <summary>Abandons the drag: drops the snapshots without recording anything. The caller has already put
    /// the objects back by applying an identity delta — which also unwinds a collision placement's previewed
    /// scale, so there is nothing left to mint either.</summary>
    public void GizmoCancelDrag()
    {
        _dragGroup.Clear();
        _dragLinks = [];
    }

    /// <summary>Ends the drag: pushes the whole group move as ONE undoable edit.</summary>
    public void GizmoEndDrag()
    {
        // A collision placement cannot store a scale, so a resize is previewed during the drag and only becomes
        // real here, by minting a rescaled hull and repointing the placement at it. It has to happen BEFORE the
        // "after" matrices below are read: minting resets the preview, so the recorded transform comes out
        // scale-free — recorded with the scale still in it, undo/redo would re-apply a size the new hull already
        // has and the hull would grow on every cycle.
        var nodes = new List<SceneNode>(_dragGroup.Count);
        foreach ((SceneNode node, _, _) in _dragGroup) nodes.Add(node);
        List<IEditAction> mints = [.. _host.CollisionEditing.MintPreviewedScales(nodes)];
        // After the mints in the composite, so before them on undo: the record goes back to the old hashes
        // while the placements still carry the new ones, and then the placements follow.
        mints.AddRange(Relink(_dragLinks));
        _dragLinks = [];

        var items = new List<(SceneNode Node, Matrix4x4 Before, Matrix4x4 After)>(_dragGroup.Count);
        foreach ((SceneNode node, _, Matrix4x4 beforeLocal) in _dragGroup)
            if (node.Source is IFrameNode fn) items.Add((node, beforeLocal, fn.LocalTransform));
        _dragGroup.Clear();
        RecordGroupTransform(items, mints);
    }

    // ── Recording edits ──

    /// <summary>Records a single object's local-transform change as one undoable edit (no-op when unchanged).</summary>
    public void RecordTransform(SceneNode node, Matrix4x4 before, Matrix4x4 after)
    {
        if (before == after) return;
        var items = new List<(SceneNode Node, Matrix4x4 Before, Matrix4x4 After)> { (node, before, after) };
        List<IEditAction> applied = [];

        // A number typed into the Transform panel comes through here, not through a drag — and the collision
        // an imported object was given has to follow it all the same. It used to stay where the object had
        // stood; past a few metres it was then no longer found as the object's, and a later delete left it in
        // the file as an obstacle nobody can see. Everything is looked for from where it WAS: the change has
        // already been made by the time it is recorded.
        if (node.Source is IFrameNode moved)
        {
            Matrix4x4 parent = moved.ParentWorldTransform;
            Matrix4x4 worldBefore = before * parent, worldAfter = after * parent;
            List<D3DImageHost.LinkedPlacement> links;
            if (node.Source is CollisionInstanceAdapter)
            {
                // A placement changed on its own: nothing rides with it, but its object's record follows.
                links = _host.LinksOf([node], new Dictionary<SceneNode, Vector3> { [node] = worldBefore.Translation });
            }
            else
            {
                links = _host.LinkedSlots([node], new Dictionary<SceneNode, Matrix4x4> { [node] = worldBefore })
                    .TryGetValue(node, out List<D3DImageHost.LinkedPlacement>? mine) ? mine : [];
                if (links.Count > 0 && Matrix4x4.Invert(worldBefore, out Matrix4x4 back))
                {
                    items.AddRange(MoveRiders(links, back * worldAfter, null, out List<IEditAction> minted));
                    applied.AddRange(minted);
                }
            }
            applied.AddRange(Relink(links));
        }
        RecordGroupTransform(items, applied);
    }

    // Records a group's local-transform changes as ONE undoable edit (keeping only the objects that moved),
    // together with any edits the drag already applied alongside the move (hull mints). One drag stays one
    // Ctrl+Z: the transform goes FIRST in the composite, which undoes in reverse, so the mints unwind before
    // the move does — a placement must be repointed back to its original hull while that hull is still there.
    private void RecordGroupTransform(
        IReadOnlyList<(SceneNode Node, Matrix4x4 Before, Matrix4x4 After)> items,
        IReadOnlyList<IEditAction>? applied = null)
    {
        var changed = new List<(SceneNode Node, Matrix4x4, Matrix4x4)>(items.Count);
        foreach (var it in items) if (it.Before != it.After) changed.Add((it.Node, it.Before, it.After));
        int extra = applied?.Count ?? 0;
        if (changed.Count == 0 && extra == 0) return;

        var children = new List<IEditAction>(1 + extra);
        if (changed.Count > 0) children.Add(new TransformEdit(this, changed));
        if (applied != null) children.AddRange(applied);
        History.Push(children.Count == 1 ? children[0] : new CompositeEdit(children.ToArray()));

        foreach (var it in changed)
            if (Persists(it.Node, it.Item2, it.Item3)) _host.Persistence.MarkFrameModified(it.Node);
    }

    // Whether a transform change is something the file can actually store. A collision placement has no scale
    // field, so its scale is session-only until a derived hull is minted for it — enlisting the .col for save on a
    // scale-only drag would mark the document dirty and then save a file with no trace of the resize. Undo/redo is
    // unaffected: history is pushed either way.
    private static bool Persists(SceneNode node, Matrix4x4 before, Matrix4x4 after)
    {
        if (node.Source is not CollisionInstanceAdapter) return true;
        if (!TransformMath.TryDecompose(before, out _, out Quaternion rotBefore, out Vector3 posBefore)
            || !TransformMath.TryDecompose(after, out _, out Quaternion rotAfter, out Vector3 posAfter))
        {
            return true; // cannot tell them apart — assume it is a real move
        }
        return Vector3.Distance(posBefore, posAfter) > 1e-5f
            || MathF.Abs(Quaternion.Dot(Quaternion.Normalize(rotBefore), Quaternion.Normalize(rotAfter))) < 0.9999995f;
    }

    // Applies recorded local transforms on undo/redo: re-selects the affected group so the change is visible,
    // sets each LOCAL matrix (cascades world) and resyncs meshes. Skips nodes that left the scene (streaming
    // unload) — a defensive backstop to the history pruning.
    private void ApplyRecordedTransforms(IReadOnlyList<(SceneNode Node, Matrix4x4 Before, Matrix4x4 After)> items, bool undo)
    {
        var live = new List<SceneNode>(items.Count);
        foreach ((SceneNode node, Matrix4x4 before, Matrix4x4 after) in items)
        {
            if (node.Source is not IFrameNode fn || !_host.Tree.IsInScene(node)) continue;
            fn.LocalTransform = undo ? before : after;
            SyncNodeMeshes(node);
            live.Add(node);
        }
        if (live.Count == 0) return;
        foreach (SceneNode n in live) _host.Persistence.MarkFrameModified(n); // undo/redo re-dirties the frame vs. the last save

        _host.Selection.SetSelection(live, live[^1]);  // re-select the group; also refreshes outline + pivot
        _host.RaiseSelectionTransformChanged();        // and the numeric fields of the active node
        _host.CarCollisionEditing.RefreshOverlay();    // …and the collision lines, which follow the stub
    }

    // Pushes fresh world matrices (already cascaded by the LocalTransform setter) onto a node's GPU meshes.
    private void SyncNodeMeshes(SceneNode node)
    {
        foreach (SceneNode leaf in node.DescendantMeshLeaves())
            if (leaf.Mesh != null && leaf.Source is IFrameNode fn)
                leaf.Mesh.SetWorld(fn.WorldTransform);

        // An actor's geometry does not hang under its tree node — it lives in the FrameResource branch, placed
        // by the actor's matrix. Moving the actor has to push those meshes' new worlds too.
        if (node.Source is ActorNodeAdapter) _host.Streamer.Actors.SyncMeshes(node);

        // A bone draws as part of the archive's rig overlay, which is one immutable buffer — so a moved bone
        // (and everything attached to it, which the frame hierarchy has already carried along) only appears in
        // its new place once that buffer is rebuilt.
        if (node.Source is BoneNodeAdapter) _host.Streamer.RefreshRig(node);

        // Same for the helper glyphs: a dummy or point IS its glyph, and a bone carries the climb boxes and
        // locks attached to it. Queued, not rebuilt here — this runs on every mouse move of a drag.
        _host.Streamer.RefreshHelpers(node);
        // The accent layer is small (the selection and the hovered node), so it follows the drag immediately —
        // an accent left behind at the old place would read as the edit not having happened.
        _host.RefreshGlyphHighlight();
    }

    /// <summary>Resyncs a node's GPU meshes to its current world, then refreshes the outline/pivot and the
    /// property fields. Call after a single-object transform edit (numeric field).</summary>
    public void CommitNodeTransform(SceneNode node)
    {
        SyncNodeMeshes(node);
        _host.Selection.UpdateSelectionHighlight();
        _host.RaiseSelectionTransformChanged();
    }

    // ── Delete (undoable) ──

    /// <summary>Whether the current selection has at least one deletable (transformable) object.</summary>
    public bool CanDeleteSelection() => DeletableRoots().Count > 0;

    /// <summary>Deletes the selected transformable objects (Del / context menu) as ONE undoable edit. The removed
    /// meshes are detached (not disposed) so undo can re-attach them.</summary>
    public void DeleteSelected()
    {
        INodeEdit? edit = BuildDeleteEdit(DeletableRoots());
        if (edit == null) return;
        edit.Redo();          // performs the delete (also clears the selection)
        History.Push(edit);
    }

    // ── Duplicate (undoable) ──

    /// <summary>Whether the selection has at least one frame object the duplicator can copy.</summary>
    public bool CanDuplicateSelection() =>
        _host.Selection.Selected.Any(n => n.Source is IFrameNode fn
            && n.Source is not (CollisionInstanceAdapter or TranslokatorInstanceAdapter or ActorNodeAdapter)
            && FrameDuplicator.CanDuplicate(fn));

    /// <summary>Duplicates the selected static meshes (Ctrl+D / context menu) as ONE undoable edit: deep,
    /// independent copies at the source transform, selected afterwards. Unsupported objects are skipped
    /// with a notice.</summary>
    public void DuplicateSelected()
    {
        LastDuplicates = [];
        var items = new List<DuplicatedItem>();
        var pairs = new List<(SceneNode Source, SceneNode Copy)>();
        int skipped = 0;
        string? lastReason = null;
        foreach (SceneNode n in _host.Selection.Selected.ToList())
        {
            if (n.Source is not IFrameNode fn) continue;
            if (n.Source is CollisionInstanceAdapter or TranslokatorInstanceAdapter or ActorNodeAdapter) continue;
            if (n.Parent is null || n.OwningDocumentNode()?.Source is not ISceneDocument doc) continue;
            FrameDuplicator.DuplicatedObject? dup = FrameDuplicator.TryDuplicate(doc, fn, out string? reason);
            if (dup == null)
            {
                skipped++;
                lastReason = reason;
                continue;
            }

            var leaf = new SceneNode(dup.Mesh.Name, "Mesh", false) { Source = dup.Node };
            n.Parent.AddChild(leaf);
            GpuMesh mesh = _host.Rnd!.CreateMeshGpu(dup.Mesh);
            mesh.Owner = leaf;
            _host.Rnd.AttachMesh(mesh);
            leaf.Mesh = mesh;
            _host.Tree.MeshCount++;
            _host.Persistence.MarkFrameModified(leaf);
            if (dup.IsOnNameTable) _host.Persistence.MarkNameTableDirty(leaf);
            items.Add(new DuplicatedItem(leaf, n.Parent, dup, mesh));
            pairs.Add((n, leaf));
        }

        if (skipped > 0)
            _host.RaiseNotice($"{skipped} object(s) not duplicated — {lastReason ?? "unsupported object"}");
        if (items.Count == 0) return;

        LastDuplicates = pairs;
        History.Push(new DuplicateEdit(this, items.ToArray())); // already applied above — pushed, not redone
        _host.Selection.SetSelection(items.Select(i => i.Node).ToList(), items[^1].Node);
        _host.RaiseSceneChanged();
    }

    /// <summary>What the last <see cref="DuplicateSelected"/> copied: each source row and the row of its copy —
    /// for the host, which gives a copy of an imported object copies of the collision it was given.</summary>
    public IReadOnlyList<(SceneNode Source, SceneNode Copy)> LastDuplicates { get; private set; } = [];

    private sealed record DuplicatedItem(
        SceneNode Node, SceneNode Parent, FrameDuplicator.DuplicatedObject Duplicate, GpuMesh Mesh);

    /// <summary>A duplication's undo unit (the NewObjectEdit pattern): pulls the cloned frame object, its
    /// blocks/buffers, its scene node and its GPU mesh out together; redo puts them all back.</summary>
    private sealed class DuplicateEdit : INodeEdit
    {
        private readonly TransformEditController _owner;
        private readonly DuplicatedItem[] _items;
        private bool _applied = true;

        public DuplicateEdit(TransformEditController owner, DuplicatedItem[] items)
        {
            _owner = owner;
            _items = items;
        }

        public IEnumerable<SceneNode> Nodes { get { foreach (DuplicatedItem i in _items) yield return i.Node; } }

        public void Undo()
        {
            D3DImageHost host = _owner._host;
            if (_items.Any(i => host.SelectedNodes.Contains(i.Node))) host.Selection.Select(null);
            foreach (DuplicatedItem it in _items)
            {
                host.Rnd?.DetachMeshes(new[] { it.Mesh });
                it.Parent.Children.Remove(it.Node);
                host.Tree.MeshCount--;
                it.Duplicate.Detach();
                host.Persistence.MarkFrameModified(it.Parent);
                if (it.Duplicate.IsOnNameTable) host.Persistence.MarkNameTableDirty(it.Parent);
            }
            host.RaiseSceneChanged();
            _applied = false;
        }

        public void Redo()
        {
            D3DImageHost host = _owner._host;
            foreach (DuplicatedItem it in _items)
            {
                it.Duplicate.Reattach();
                it.Parent.AddChild(it.Node);
                host.Rnd?.AttachMesh(it.Mesh);
                host.Tree.MeshCount++;
                host.Persistence.MarkFrameModified(it.Node);
                if (it.Duplicate.IsOnNameTable) host.Persistence.MarkNameTableDirty(it.Node);
            }
            host.RaiseSceneChanged();
            _applied = true;
        }

        public void Discard()
        {
            if (_applied) return; // attached — the renderer owns the meshes
            foreach (DuplicatedItem it in _items) it.Mesh.Dispose();
        }
    }

    // ── Reparent (undoable) ──

    /// <summary>Reparents a frame node under a new parent node (another object, a scene folder, or the document
    /// root), moving it in the tree as ONE undoable edit. No-op when invalid (self / a descendant / no change /
    /// the document rejects it).</summary>
    public void Reparent(SceneNode node, SceneNode newParentNode)
    {
        if (node.Source is not IFrameNode || node.Parent is null) return;
        if (ReferenceEquals(newParentNode, node) || ReferenceEquals(newParentNode, node.Parent)) return;
        if (IsSelfOrAncestor(node, newParentNode)) return;          // can't parent under one's own descendant
        if (node.OwningDocumentNode()?.Source is not ISceneDocument doc) return;

        var edit = new ReparentEdit(this, doc, node, node.Parent, node.Parent.Children.IndexOf(node), newParentNode);
        if (!edit.MoveTo(newParentNode)) return;                    // document rejected it (cycle) — record nothing
        History.Push(edit);
        _host.Persistence.MarkFrameModified(node);
        _host.RaiseSceneChanged();
    }

    // True if 'candidate' is 'node' itself or one of its tree descendants (so it can't become node's parent).
    private static bool IsSelfOrAncestor(SceneNode node, SceneNode candidate)
    {
        for (SceneNode? n = candidate; n != null; n = n.Parent)
            if (ReferenceEquals(n, node)) return true;
        return false;
    }

    /// <summary>Builds (without applying or recording) an undoable delete of the given subtree
    /// roots — the Blender bridge folds it into its per-push composite edit. Null when nothing in
    /// the list can be removed.</summary>
    internal INodeEdit? BuildDeleteEdit(IReadOnlyList<SceneNode> roots)
    {
        if (roots.Count == 0) return null;

        // Materialize any still-streaming meshes under the deleted subtrees first, so they're captured (and
        // undoable) rather than attaching later as ghosts outside the tree.
        foreach (SceneNode n in roots) _host.Streamer.DrainPendingUnder(n);

        var items = new List<DeletedItem>(roots.Count);
        foreach (SceneNode n in roots)
        {
            if (n.Parent is not { } parent) continue; // a root with no parent can't be removed/restored
            var meshes = new List<GpuMesh>();
            foreach (SceneNode leaf in n.DescendantMeshLeaves())
                if (leaf.Mesh != null) meshes.Add(leaf.Mesh);
            items.Add(new DeletedItem(
                n, parent, parent.Children.IndexOf(n), meshes.ToArray(), BuildDetachment(n), MirrorsOf(n)));
        }
        return items.Count == 0 ? null : new DeleteEdit(this, items.ToArray());
    }

    /// <summary>
    /// Every OTHER row in the tree that stands for something inside this subtree.
    ///
    /// <para>
    /// A frame attached to a bone is listed twice: under the bone, which says which part it belongs to, and
    /// in the hierarchy, which says where it sits in the graph. The document knows one frame; the tree shows
    /// two rows. Deleting one of them used to leave the other behind pointing at a frame that no longer
    /// exists — "I delete the collision on Scale Bone and it stays in the unnamed holders".
    /// </para>
    /// </summary>
    private (SceneNode Node, SceneNode Parent, int Index)[] MirrorsOf(SceneNode root)
    {
        var sources = new HashSet<object>();
        CollectSubtreeSources(root, sources);
        if (sources.Count == 0) return [];

        var found = new List<(SceneNode, SceneNode, int)>();
        foreach (SceneNode top in _host.Tree.Roots) Walk(top);
        return [.. found];

        void Walk(SceneNode node)
        {
            foreach (SceneNode child in node.Children.ToList())
            {
                if (child.Source != null && sources.Contains(child.Source) && !IsUnder(child, root))
                {
                    found.Add((child, node, node.Children.IndexOf(child)));
                }
                Walk(child);
            }
        }
    }

    private static void CollectSubtreeSources(SceneNode node, HashSet<object> into)
    {
        if (node.Source != null) into.Add(node.Source);
        foreach (SceneNode c in node.Children) CollectSubtreeSources(c, into);
    }

    private static bool IsUnder(SceneNode node, SceneNode root)
    {
        for (SceneNode? at = node; at != null; at = at.Parent)
        {
            if (ReferenceEquals(at, root)) return true;
        }
        return false;
    }

    // The document-level half of a delete: every vendor frame under the subtree, handed to DetachedFrames so
    // Save/Build stop writing them. Null (tree-only removal) when the root has no frame document.
    private static DetachedFrames? BuildDetachment(SceneNode root)
    {
        if (root.OwningDocumentNode()?.Source is not ISceneDocument doc) return null;
        var frames = new List<IFrameNode>();
        CollectSubtreeFrames(root, frames);
        return DetachedFrames.Capture(doc, frames);
    }

    private static void CollectSubtreeFrames(SceneNode node, List<IFrameNode> into)
    {
        // Collision placements are IFrameNode too but persist through their own .col path.
        if (node.Source is IFrameNode fn and not CollisionInstanceAdapter) into.Add(fn);
        foreach (SceneNode c in node.Children) CollectSubtreeFrames(c, into);
    }

    // Top-most selected transformable nodes — a selected descendant is already inside a selected ancestor's
    // subtree, so deleting the ancestor covers it. Only IFrameNode-backed nodes (meshes / frames) are
    // deletable; structural containers (folder / SDS / scene) are left to the loading machinery.
    private List<SceneNode> DeletableRoots()
    {
        var sel = new HashSet<SceneNode>(_host.Selection.Selected);
        var roots = new List<SceneNode>();
        foreach (SceneNode n in _host.Selection.Selected)
            // Collision placements and crash props are IFrameNode too, but they delete through their own
            // controllers (the .col and .tra paths), not this one. An actor is an IFrameNode as well and has
            // no frame object at all — this path would drop its tree row while its geometry stayed in the
            // scene, and undo would then try to re-insert the row where nothing removed one. A bone is not a
            // frame either: deleting one would have to renumber every vertex weight in the model.
            if (n.Source is IFrameNode
                    and not (CollisionInstanceAdapter or TranslokatorInstanceAdapter or ActorNodeAdapter
                             or BoneNodeAdapter)
                && !HasSelectedAncestorNode(n, sel))
                roots.Add(n);
        return roots;
    }

    private static bool HasSelectedAncestorNode(SceneNode node, HashSet<SceneNode> selected)
    {
        for (SceneNode? p = node.Parent; p != null; p = p.Parent)
            if (selected.Contains(p)) return true;
        return false;
    }

    // Removes the deleted subtrees from the tree, detaches their meshes (kept alive by the edit), and takes
    // their frame objects out of the loaded document so Save/Build persist the deletion.
    private void ApplyDelete(DeletedItem[] items)
    {
        _host.Selection.Select(null); // the deleted nodes are leaving the scene
        foreach (DeletedItem it in items)
        {
            if (it.Detached != null)
            {
                it.Detached.Detach();
                // Removing objects shifts every later object's index, and the frame name table records
                // objects BY index — so the table must be rebuilt alongside the resource. Marked while the
                // node is still in the tree (both lookups walk up to the owning document).
                _host.Persistence.MarkFrameModified(it.Node);
                _host.Persistence.MarkNameTableDirty(it.Node);
                // A deleted collision stub takes its prefab volume with it, through the aggregate — and that
                // save can refuse. The row goes either way, so a refusal leaves the car solid where nothing
                // is drawn, which nobody can see without being told.
                if (it.Detached.VolumesRefused is { } why)
                {
                    _host.RaiseNotice(
                        "the collision of a deleted shape is still in the car's prefab: " + why, isError: true);
                }
            }
            _host.Rnd?.DetachMeshes(it.Meshes);
            _host.Tree.MeshCount -= it.Meshes.Length;
            it.Parent.Children.Remove(it.Node);
            // …and every other row showing the same object, or the frame is gone from the document while a
            // second row still lists it.
            foreach ((SceneNode row, SceneNode holder, int _) in it.Mirrors) holder.Children.Remove(row);
        }
        _host.RaiseSceneChanged();
    }

    // Re-inserts the deleted subtrees at their old positions, re-attaches their meshes and frame objects,
    // and re-selects them. Ascending original index so each clamped Insert lands correctly even when several
    // siblings were deleted (the selection/click order the items were captured in is arbitrary).
    private void RestoreDelete(DeletedItem[] items)
    {
        var restored = new List<SceneNode>(items.Length);
        foreach (DeletedItem it in items.OrderBy(i => i.Index))
        {
            it.Parent.Children.Insert(Math.Min(it.Index, it.Parent.Children.Count), it.Node);
            if (it.Detached != null)
            {
                it.Detached.Reattach();
                _host.Persistence.MarkFrameModified(it.Node);  // memory diverged from the last save again
                _host.Persistence.MarkNameTableDirty(it.Node);
            }
            foreach (GpuMesh m in it.Meshes) _host.Rnd?.AttachMesh(m);
            _host.Tree.MeshCount += it.Meshes.Length;
            foreach ((SceneNode row, SceneNode holder, int at) in it.Mirrors)
            {
                if (!holder.Children.Contains(row)) holder.Children.Insert(Math.Min(at, holder.Children.Count), row);
            }
            restored.Add(it.Node);
        }
        if (restored.Count > 0) _host.Selection.SetSelection(restored, restored[^1]);
        _host.RaiseSceneChanged();
    }

    /// <param name="Mirrors">The OTHER rows that show the same objects. A frame hung off a bone appears
    /// twice — once under that bone and once in its own place in the hierarchy — so deleting the row that was
    /// clicked left the frame gone from the document and still listed under the other one, which reads as
    /// "it deleted here and stayed there".</param>
    private sealed record DeletedItem(
        SceneNode Node, SceneNode Parent, int Index, GpuMesh[] Meshes, DetachedFrames? Detached,
        (SceneNode Node, SceneNode Parent, int Index)[] Mirrors);

    private sealed class DeleteEdit : INodeEdit
    {
        private readonly TransformEditController _owner;
        private readonly DeletedItem[] _items;
        private bool _applied; // true while the delete is in effect (meshes detached & held here)

        public DeleteEdit(TransformEditController owner, DeletedItem[] items) { _owner = owner; _items = items; }

        public IEnumerable<SceneNode> Nodes { get { foreach (DeletedItem i in _items) yield return i.Node; } }

        public void Redo() { _owner.ApplyDelete(_items); _applied = true; }
        public void Undo() { _owner.RestoreDelete(_items); _applied = false; }

        // Dropped from history while still deleted → the detached meshes are ours to release (the deletion
        // itself stays permanent — the document was already marked modified). If undone, they are back in
        // the renderer (it owns them), so leave them alone.
        public void Discard()
        {
            if (!_applied) return;
            foreach (DeletedItem it in _items) foreach (GpuMesh m in it.Meshes) m.Dispose();
        }
    }

    // Moves a node between parents in both the vendor frame graph (via the document) and the scene tree, as one
    // undoable edit. Persistence is automatic — parent indices are recomputed on save.
    private sealed class ReparentEdit : INodeEdit
    {
        private readonly TransformEditController _owner;
        private readonly ISceneDocument _doc;
        private readonly SceneNode _node;
        private readonly SceneNode _oldParent;
        private readonly int _oldIndex;
        private readonly SceneNode _newParent;

        public ReparentEdit(TransformEditController owner, ISceneDocument doc, SceneNode node,
            SceneNode oldParent, int oldIndex, SceneNode newParent)
        {
            _owner = owner;
            _doc = doc;
            _node = node;
            _oldParent = oldParent;
            _oldIndex = oldIndex;
            _newParent = newParent;
        }

        public IEnumerable<SceneNode> Nodes { get { yield return _node; } }

        // Applies the move to a parent node (appended at its end). Returns false if the document rejects it (cycle).
        public bool MoveTo(SceneNode parent)
        {
            if (_node.Source is not IFrameNode child || !_doc.Reparent(child, parent.Source)) return false;
            _node.MoveTo(parent, parent.Children.Count);
            _owner.SyncNodeMeshes(_node);                      // world transforms changed
            _owner._host.Selection.SetSelection(new[] { _node }, _node);
            return true;
        }

        public void Redo()
        {
            if (!_owner._host.Tree.IsInScene(_node)) return;
            if (!MoveTo(_newParent)) return;
            _owner._host.Persistence.MarkFrameModified(_node);
            _owner._host.RaiseSceneChanged();
        }

        public void Undo()
        {
            if (_node.Source is not IFrameNode child || !_owner._host.Tree.IsInScene(_node)) return;
            // A rejected reparent (e.g. it would form a cycle after later edits) must not move the scene
            // node either — the tree and the frame graph would diverge, and a cycle hangs every tree walk.
            if (!_doc.Reparent(child, _oldParent.Source)) return;
            _node.MoveTo(_oldParent, _oldIndex);
            _owner.SyncNodeMeshes(_node);
            _owner._host.Persistence.MarkFrameModified(_node);
            _owner._host.Selection.SetSelection(new[] { _node }, _node);
            _owner._host.RaiseSceneChanged();
        }
    }

    private sealed class TransformEdit : INodeEdit
    {
        private readonly TransformEditController _owner;
        private readonly (SceneNode Node, Matrix4x4 Before, Matrix4x4 After)[] _items;

        public TransformEdit(TransformEditController owner, IReadOnlyList<(SceneNode Node, Matrix4x4 Before, Matrix4x4 After)> items)
        {
            _owner = owner;
            _items = items.ToArray();
        }

        /// <summary>The nodes this edit targets — used to prune the edit when their subtree is unloaded.</summary>
        public IEnumerable<SceneNode> Nodes { get { foreach (var i in _items) yield return i.Node; } }

        public void Undo() => _owner.ApplyRecordedTransforms(_items, undo: true);
        public void Redo() => _owner.ApplyRecordedTransforms(_items, undo: false);
    }
}
