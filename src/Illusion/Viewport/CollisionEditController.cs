using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Collisions;
using Illusion.Domain;
using Illusion.Formats.Collisions;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Undoable add / delete / duplicate of collision placements. These mutate the loaded
/// <c>CollisionFile.Instances</c> directly, so they persist to the .col on save. Edits share the
/// viewport's <see cref="TransformEditController.History"/> so Ctrl+Z unwinds them alongside transform edits, and
/// each keeps the tree layer's child nodes index-aligned with the instance list (the ray-picker maps by index).
/// The overlay repaints via the owning document's <c>RenderDirty</c> flag (streamer picks it up next frame).
/// </summary>
internal sealed class CollisionEditController : ICollisionEditSink
{
    private readonly D3DImageHost _host;

    public CollisionEditController(D3DImageHost host) => _host = host;

    void ICollisionEditSink.Enlist(SceneNode layer) => _host.Persistence.MarkFrameModified(layer);

    void ICollisionEditSink.Refresh() => _host.RaiseSelectionTransformChanged();

    private EditHistory History => _host.Editing.History;

    /// <summary>Whether the selection contains at least one collision placement (delete/duplicate target).</summary>
    public bool HasCollisionSelection() => _host.Selection.Selected.Any(n => n.Source is CollisionInstanceAdapter);

    /// <summary>
    /// How many hulls in the selected collision layer(s) no placement references — the number the "Remove unused
    /// hulls" command would sweep. Zero disables it, so a modder can read the file's state off the menu instead
    /// of guessing whether a delete left anything behind.
    /// </summary>
    public int UnusedHullCount() =>
        SweepUnusedHulls(SelectedCollisionLayers().Select(l => l.Layer), apply: false).Sum(l => l.Unused);

    /// <summary>
    /// Removes every hull no placement references, as one undoable edit per layer. Never runs on its own: a
    /// delete or a resize that orphans a hull leaves it in place, because the hull may be wanted again and undo
    /// has to be able to put the file back exactly as it was found.
    /// </summary>
    public void RemoveUnusedHulls()
    {
        if (SweepUnusedHulls(SelectedCollisionLayers().Select(l => l.Layer), apply: true).Sum(l => l.Unused) == 0)
            _host.RaiseNotice("no unused hulls — every hull in this .col is placed");
    }

    /// <summary>
    /// The same sweep for layers named outright instead of reached through the selection — what the MCP tool
    /// drives, having no selection to go by. Reports each layer's unused hulls; with <paramref name="apply"/>
    /// removes them, all layers as one undoable edit.
    /// </summary>
    public IReadOnlyList<(SceneNode Layer, int Unused)> SweepUnusedHulls(IEnumerable<SceneNode> layers, bool apply)
    {
        var found = new List<(SceneNode Layer, int Unused)>();
        var edits = new List<IEditAction>();
        foreach (SceneNode layer in layers)
        {
            if (layer.Source is not CollisionDocumentAdapter doc) continue;
            CollisionOrphanEdit? edit = CollisionOrphanEdit.Build(this, doc, layer);
            found.Add((layer, edit?.Count ?? 0));
            if (edit == null || !apply) continue;
            edit.Redo();
            edits.Add(edit);
        }
        if (edits.Count == 0) return found;

        History.Push(edits.Count == 1 ? edits[0] : new CompositeEdit(edits.ToArray()));
        _host.RaiseNotice($"removed {found.Sum(l => l.Unused)} unused hull(s) — Ctrl+Z restores them");
        return found;
    }

    // The collision layers the selection reaches: the layer node itself when it is selected, or the layer behind
    // any selected placement. Distinct, so selecting several placements of one .col sweeps it once.
    private IEnumerable<(CollisionDocumentAdapter Doc, SceneNode Layer)> SelectedCollisionLayers()
    {
        var seen = new HashSet<CollisionDocumentAdapter>();
        foreach (SceneNode n in _host.Selection.Selected)
        {
            SceneNode? layer = n.Source is CollisionDocumentAdapter ? n : n.Parent;
            if (layer?.Source is not CollisionDocumentAdapter doc || !seen.Add(doc)) continue;
            yield return (doc, layer);
        }
    }

    /// <summary>Deletes the selected collision placements from their .col (undoable, persists on save).</summary>
    public void DeleteSelected()
    {
        var items = new List<CollisionListEdit.Item>();
        foreach (SceneNode n in _host.Selection.Selected)
        {
            if (n.Source is not CollisionInstanceAdapter ca || n.Parent is not { } layer
                || layer.Source is not CollisionDocumentAdapter doc) continue;
            int idx = layer.Children.IndexOf(n);
            if (idx < 0) continue;
            items.Add(new CollisionListEdit.Item(doc, layer, idx, ca.Instance, n));
        }
        if (items.Count == 0) return;
        var edit = new CollisionListEdit(_host, items, added: false);
        edit.Redo();
        History.Push(edit);
    }

    /// <summary>
    /// Makes any previewed hull resizes in a finished gizmo drag real, returning the edits ALREADY APPLIED so the
    /// caller can fold them into the drag's single history entry. Placements at identity scale are left alone.
    /// <para>
    /// Must run BEFORE the drag's "after" matrices are captured: a minted hull carries the size itself and the
    /// preview is reset here, so a transform recorded afterwards is scale-free. Recorded the other way round,
    /// undo/redo would re-apply a scale the hull already has and grow it on every cycle.
    /// </para>
    /// <para>Refusals (a hull that cannot be rescaled, a scale the patcher declines) snap the preview back and
    /// say so in the viewport — nothing is written and no history entry is made.</para>
    /// </summary>
    public IReadOnlyList<IEditAction> MintPreviewedScales(IEnumerable<SceneNode> nodes)
    {
        var edits = new List<IEditAction>();
        foreach (SceneNode n in nodes)
        {
            if (n.Source is not CollisionInstanceAdapter ca || n.Parent is not { } layer
                || layer.Source is not CollisionDocumentAdapter doc) continue;

            Vector3 scale = ca.PreviewScale;
            if (CollisionMeshMinter.IsIdentityScale(scale)) continue;

            MintedHull minted = CollisionMeshMinter.Mint(doc.Collision, ca.Instance.Hash, scale,
                static (blob, s) => CookedMeshScaler.Scale(blob, s));
            if (minted.SkipReason != null)
            {
                Snap(ca, doc, "this hull could not be resized — " + minted.SkipReason);
                continue;
            }

            var edit = new CollisionMintEdit(
                this, doc, layer, n, ca, ca.Instance.Hash, minted.Hash, minted.Added, scale);
            edit.Redo();   // applied inline; the caller pushes it without redoing
            edits.Add(edit);
        }
        return edits;
    }

    // A refused resize leaves the file untouched, so the only correct thing to show is the size it still has.
    private void Snap(CollisionInstanceAdapter adapter, CollisionDocumentAdapter doc, string reason)
    {
        adapter.PreviewScale = Vector3.One;
        doc.RenderDirty = true;
        _host.RaiseNotice(reason);
    }

    /// <summary>
    /// Builds (without applying) the edits that add a brand-new hull and a placement for it — the Blender
    /// authoring path. Returns null when the layer is no longer in the scene.
    /// <para>
    /// The mesh and the placement are separate edits on purpose: the placement one is the same
    /// <c>CollisionListEdit</c> every other add/remove goes through, which is what keeps
    /// <c>CollisionFile.Instances</c> and the layer's children index-aligned — the ray-picker pairs them by
    /// position, so a placement appended without its node would make later clicks resolve to the wrong hull.
    /// </para>
    /// </summary>
    public IReadOnlyList<IEditAction>? BuildCreateHull(
        CollisionDocumentAdapter doc, SceneNode layer, CollisionMesh? mesh, CollisionInstance placement, string name)
    {
        if (!_host.Tree.IsInScene(layer)) return null;

        var node = new SceneNode(name, "CollisionInstance", false) { Source = doc.Node(placement) };
        var edits = new List<IEditAction>();

        // The hull first, so undo takes the placement off it before it is collected.
        if (mesh != null)
        {
            edits.Add(new CollisionMintEdit(this, doc, layer, node, doc.Node(placement),
                placement.Hash, placement.Hash, mesh, Vector3.One));
        }
        edits.Add(new CollisionListEdit(_host, new[]
        {
            new CollisionListEdit.Item(doc, layer, -1, placement, node),
        }, added: true));
        return edits;
    }

    /// <summary>Appends a copy of each selected placement (same mesh/transform) to its .col and selects the copies.</summary>
    public void DuplicateSelected()
    {
        var items = new List<CollisionListEdit.Item>();
        foreach (SceneNode n in _host.Selection.Selected)
        {
            if (n.Source is not CollisionInstanceAdapter ca || n.Parent is not { } layer
                || layer.Source is not CollisionDocumentAdapter doc) continue;
            CollisionInstance src = ca.Instance;
            var clone = new CollisionInstance
            {
                Position = src.Position,
                Rotation = src.Rotation,
                Hash = src.Hash,
                // A fresh placement owns nothing: copying the source's owner index would leave two placements
                // claiming the same frame object. -1 ("none") is what the game's own authoring uses.
                Unk4 = -1,
                Group = src.Group,
            };
            var node = new SceneNode($"{n.Name} (copy)", "CollisionInstance", false) { Source = doc.Node(clone) };
            items.Add(new CollisionListEdit.Item(doc, layer, -1, clone, node));
        }
        if (items.Count == 0) return;
        var edit = new CollisionListEdit(_host, items, added: true);
        edit.Redo();
        History.Push(edit);
        _host.Selection.SetSelection(items.Select(i => i.Node).ToList(), items[^1].Node);
    }

    // One undoable add-or-remove of a set of placements. For an add edit Redo appends + Undo removes; for a delete
    // edit Redo removes + Undo restores at the captured index. Both keep CollisionFile.Instances and the tree
    // layer's Children index-aligned, mark the document dirty (persist + repaint), and refresh the scene.
    private sealed class CollisionListEdit : INodeEdit
    {
        public readonly record struct Item(
            CollisionDocumentAdapter Doc, SceneNode Layer, int Index, CollisionInstance Inst, SceneNode Node);

        private readonly D3DImageHost _host;
        private readonly Item[] _items;
        private readonly bool _added;

        public CollisionListEdit(D3DImageHost host, IReadOnlyList<Item> items, bool added)
        {
            _host = host;
            _items = items.ToArray();
            _added = added;
        }

        public IEnumerable<SceneNode> Nodes { get { foreach (Item i in _items) yield return i.Node; } }

        public void Redo() { if (_added) Add(); else Remove(); }
        public void Undo() { if (_added) Remove(); else Restore(); }

        private void Add()
        {
            foreach (Item it in _items)
            {
                it.Doc.Collision.Instances.Add(it.Inst);
                it.Layer.AddChild(it.Node);
            }
            Finish();
        }

        private void Remove()
        {
            foreach (Item it in _items)
            {
                it.Doc.Collision.Instances.Remove(it.Inst);
                it.Layer.Children.Remove(it.Node);
            }
            DropFromSelection();
            Finish();
        }

        // Re-insert at the captured index, ascending, so multiple restores land in their original slots (both lists
        // grow in lockstep, so the same clamped index keeps them aligned).
        private void Restore()
        {
            foreach (Item it in _items.OrderBy(i => i.Index))
            {
                // The instance list and the tree layer must be re-populated at the SAME index — the ray-picker
                // maps a hit to a placement by position. Clamp once and use it for both; feeding the tree the
                // unclamped index desynchronises them and makes later picks resolve to the wrong hull.
                int idx = Math.Clamp(it.Index, 0, it.Doc.Collision.Instances.Count);
                it.Doc.Collision.Instances.Insert(idx, it.Inst);
                it.Layer.InsertChild(idx, it.Node);
            }
            Finish();
        }

        private void DropFromSelection()
        {
            if (!_host.Selection.Selected.Any(n => _items.Any(i => ReferenceEquals(i.Node, n)))) return;
            var keep = _host.Selection.Selected.Where(n => _items.All(i => !ReferenceEquals(i.Node, n))).ToList();
            _host.Selection.SetSelection(keep, keep.Count > 0 ? keep[^1] : null);
        }

        private void Finish()
        {
            foreach (Item it in _items)
            {
                it.Doc.RenderDirty = true;          // repaint the overlay from the new instance set (next frame)
                _host.Persistence.MarkFrameModified(it.Layer); // enlist the .col for save/build
            }
            _host.RaiseSceneChanged();
        }
    }
}
