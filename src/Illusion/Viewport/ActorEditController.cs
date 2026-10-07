using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Assets.Frames;
using Illusion.Domain;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Rendering.Gpu;
using Illusion.Scene;
using ClonedPrototype = Illusion.Assets.Frames.ActorPrototypeCloner.ClonedPrototype;

namespace Illusion.Viewport;

/// <summary>
/// Actor editing of the viewport: deleting selected actors as one undoable edit. Actors do not go through the
/// frame path — they have no frame object of their own, and their geometry hangs in the FrameResource branch
/// as a prototype the actor merely places. Deleting one drops its record from the .act pack and hides that
/// geometry; the prototype frame itself stays, which is also what the game does with a prototype nothing
/// spawns.
/// </summary>
internal sealed class ActorEditController
{
    private readonly D3DImageHost _host;

    public ActorEditController(D3DImageHost host) => _host = host;

    /// <summary>Whether the selection contains an actor this controller can delete.</summary>
    public bool HasActorSelection() => SelectedActors().Count > 0;

    /// <summary>Deletes the selected actors as ONE undoable edit.</summary>
    public void DeleteSelected()
    {
        IReadOnlyList<SceneNode> nodes = SelectedActors();
        if (nodes.Count == 0) return;

        var items = new List<DeletedActor>(nodes.Count);
        foreach (SceneNode node in nodes)
        {
            if (node.Source is not ActorNodeAdapter adapter) continue;
            if (node.OwningDocumentNode()?.Source is not ActorDocumentAdapter document) continue;
            ActorPlacements placements = document.Placements;
            if (placements.PackOf(adapter.Actor) is not { } pack) continue;

            items.Add(new DeletedActor
            {
                Node = node,
                Adapter = adapter,
                Document = document,
                Pack = pack,
                Parent = node.Parent,
                TreeIndex = node.Parent?.Children.IndexOf(node) ?? -1,
                PlacementIndex = IndexIn(placements.All, adapter.Actor),
                HadGlyph = placements.HasGlyph(adapter.Actor),
            });
        }
        if (items.Count == 0) return;

        var edit = new DeleteActorsEdit(this, items);
        edit.Redo();
        _host.History.Push(edit);
    }

    // ── Duplicate (undoable) ──

    /// <summary>Whether the selection contains an actor that can be copied.</summary>
    public bool HasDuplicableSelection() => SelectedActors().Count > 0;

    /// <summary>Copies the selected actors as ONE undoable edit. An actor that places a scene object is
    /// skipped with a notice — that copy needs its own clone of the object first.</summary>
    public void DuplicateSelected()
    {
        IReadOnlyList<SceneNode> nodes = SelectedActors();
        if (nodes.Count == 0) return;

        var items = new List<CopiedActor>(nodes.Count);
        string? lastReason = null;
        int skipped = 0;

        foreach (SceneNode node in nodes)
        {
            if (node.Source is not ActorNodeAdapter adapter) continue;
            if (node.OwningDocumentNode()?.Source is not ActorDocumentAdapter document) continue;
            ActorPlacements placements = document.Placements;
            if (placements.PackOf(adapter.Actor) is not { } pack) continue;

            // An actor that places an object needs a clone of that object first: a frame is spawned by
            // exactly one actor, so the copy cannot share the original's.
            ClonedPrototype? clone = null;
            ActorPlacedFrame? placed = null;
            if (placements.TargetOf(adapter.Actor) is { } target)
            {
                clone = ActorPrototypeCloner.TryClone(document.Scene, target, out string? cloneReason);
                if (clone == null)
                {
                    skipped++;
                    lastReason = cloneReason;
                    continue;
                }
                placed = new ActorPlacedFrame(clone.Root.Name.String, clone.FrameIndex);
            }

            ActorEntry? copy = pack.Duplicate(adapter.Actor, placed, out string? reason);
            if (copy == null)
            {
                clone?.Detach();
                skipped++;
                lastReason = reason;
                continue;
            }

            ActorNodeAdapter copyAdapter = document.ActorNode(copy);
            var copyNode = new SceneNode(copyAdapter.Name, "Actor", false) { Source = copyAdapter };

            // A cloned object belongs to the archive's FRAME document, which hangs BESIDE the actors rather
            // than under them — marking the actor's own document would save the pack and quietly leave the
            // object out of the scene, giving the game a reference to a row that is not there.
            SceneNode? frameRow = clone == null ? null : FrameDocumentRow(node, document);

            items.Add(new CopiedActor
            {
                Source = adapter.Actor,
                Copy = copy,
                Node = copyNode,
                Parent = node.Parent,
                TreeIndex = (node.Parent?.Children.IndexOf(node) ?? -1) + 1,
                Document = document,
                Pack = pack,
                Clone = clone,
                FrameRow = frameRow,
                Rows = clone == null || node.OwningDocumentNode() is not { } sdsRow
                    ? []
                    : BuildPrototypeRows(document, clone, adapter.Actor, sdsRow),
            });
            if (frameRow != null) _host.Persistence.MarkFrameModified(frameRow);
        }

        if (items.Count > 0)
        {
            var edit = new DuplicateActorsEdit(this, items);
            edit.Redo();
            _host.History.Push(edit);
        }
        if (skipped > 0)
        {
            _host.RaiseNotice($"{skipped} actor(s) not copied — {lastReason}", isError: false);
        }

        // Copies of an object built on collision are allowed but flagged: one such copy — a destructible gate
        // with three hulls — made the game refuse the district on load, and why is still unknown. Everything
        // whose object carries no hull is verified working in the game.
        int withHulls = items.Count(i => i.Clone != null && ActorPrototypeCloner.HullsOf(i.Clone.Root) > 0);
        if (withHulls > 0)
        {
            _host.RaiseNotice($"{withHulls} copy/copies carry collision — test the district in the game before " +
                              "building on it; a copy of this shape crashed on load once", isError: false);
        }
    }

    private sealed class CopiedActor
    {
        public required ActorEntry Source;
        public required ActorEntry Copy;
        public required SceneNode Node;
        public required SceneNode? Parent;
        public required int TreeIndex;
        public required ActorDocumentAdapter Document;
        public required ActorsFile Pack;

        /// <summary>The clone of the object this actor places — null when it places nothing.</summary>
        public ClonedPrototype? Clone;

        /// <summary>The archive's frame-document row, so undo and redo re-enlist the scene as well as the pack.</summary>
        public SceneNode? FrameRow;

        /// <summary>The cloned geometry's tree rows and GPU meshes, so undo can take them off screen.</summary>
        public IReadOnlyList<PrototypeRow> Rows = [];

        // Set while the copy is undone: the token that puts this exact row back into the pack.
        public ActorRemoval? Removal;
    }

    // One cloned mesh of a copied prototype: its tree row, where that row hangs, and its GPU mesh.
    private sealed record PrototypeRow(SceneNode Node, SceneNode Parent, GpuMesh Mesh);

    // Puts a cloned prototype's geometry on screen: a row beside the original's, an uploaded mesh, and an
    // entry in the streamer's frame→row map, which is what lets the new actor move, hide and outline it.
    private IReadOnlyList<PrototypeRow> BuildPrototypeRows(ActorDocumentAdapter document, ClonedPrototype clone,
        ActorEntry source, SceneNode fallbackParent)
    {
        var rows = new List<PrototypeRow>(clone.Renderables.Count);
        // Beside the original's own rows, so a copied bottle appears where its bottle lives in the tree.
        SceneNode parent = (FirstMeshOf(document, source) is { } sourceMesh
            ? _host.Streamer.Actors.MeshRowOf(sourceMesh)?.Parent
            : null) ?? fallbackParent;

        foreach ((FrameObjectSingleMesh frame, MeshData data) in clone.Renderables)
        {
            var leaf = new SceneNode(data.Name, "Mesh", false) { Source = document.Scene.Node(frame) };
            parent.AddChild(leaf);

            GpuMesh mesh = _host.Rnd!.CreateMeshGpu(data);
            mesh.Owner = leaf;
            _host.Rnd.AttachMesh(mesh);
            leaf.Mesh = mesh;
            _host.Tree.MeshCount++;
            _host.Streamer.Actors.AddMeshRow(document.Placements, frame, leaf);
            rows.Add(new PrototypeRow(leaf, parent, mesh));
        }

        // The frame name table is the game's spawn list and is rebuilt from the resource — a copy that
        // inherited membership has to be in the rewritten one, or it is an object the table never mentions.
        if (clone.IsOnNameTable && rows.Count > 0) _host.Persistence.MarkNameTableDirty(rows[0].Node);
        return rows;
    }

    // The tree row that carries the archive's FRAME document. The actors branch sits beside it under the same
    // SDS, so an edit to the objects has to be enlisted through this row — walking up from an actor only ever
    // reaches the actors' own document.
    private static SceneNode? FrameDocumentRow(SceneNode actorRow, ActorDocumentAdapter document)
    {
        if (actorRow.OwningDocumentNode()?.Parent is not { } sds) return null;
        foreach (SceneNode child in sds.Children)
        {
            if (ReferenceEquals(child.Source, document.Scene)) return child;
        }
        return null;
    }

    // Any mesh of the actor's own prototype — used only to find which branch of the tree its copy belongs in.
    private static FrameObjectBase? FirstMeshOf(ActorDocumentAdapter document, ActorEntry actor) =>
        document.Placements.TargetOf(actor) is { } target
            ? FirstMesh(target, new HashSet<FrameObjectBase>())
            : null;

    private static FrameObjectBase? FirstMesh(FrameObjectBase frame, HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return null;
        if (frame is FrameObjectSingleMesh) return frame;
        foreach (FrameObjectBase child in frame.Children)
        {
            if (FirstMesh(child, seen) is { } found) return found;
        }
        return null;
    }

    private sealed class DuplicateActorsEdit : INodeEdit
    {
        private readonly ActorEditController _owner;
        private readonly List<CopiedActor> _items;

        public DuplicateActorsEdit(ActorEditController owner, List<CopiedActor> items)
        {
            _owner = owner;
            _items = items;
        }

        public IEnumerable<SceneNode> Nodes
        {
            get { foreach (CopiedActor item in _items) yield return item.Node; }
        }

        public void Redo()
        {
            // A redo puts the very rows the undo took out back into the pack — in reverse, since each removal
            // recorded the index the list had at that moment (the first Redo has them there already, from the
            // Duplicate that created this edit).
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Removal is not { } removal) continue;
                _items[i].Pack.Restore(removal);
                _items[i].Removal = null;
            }

            var selection = new List<SceneNode>(_items.Count);
            foreach (CopiedActor item in _items)
            {
                // The cloned object goes back into the frame resource and onto the screen first, so the
                // placement below has something to claim.
                item.Clone?.Reattach();
                foreach (PrototypeRow row in item.Rows)
                {
                    if (!row.Parent.Children.Contains(row.Node)) row.Parent.AddChild(row.Node);
                    _owner._host.Rnd?.AttachMesh(row.Mesh);
                    _owner._host.Tree.MeshCount++;
                    _owner._host.Persistence.MarkFrameModified(row.Node);
                }

                if (item.FrameRow != null) _owner._host.Persistence.MarkFrameModified(item.FrameRow);
                item.Document.Placements.AddCopy(item.Copy, item.Source, item.Pack, item.Clone?.Root);
                _owner._host.Streamer.Actors.AddActorRow(item.Document.Placements, item.Copy, item.Node);

                // The cloned meshes were uploaded with the PROTOTYPE's own world transform, which for an
                // actor's object is the origin — the placement only exists once AddCopy has registered it.
                // Without this the copy's geometry is drawn at (0,0,0) and there is nothing where the copy
                // was made.
                _owner._host.Streamer.Actors.SyncMeshes(item.Node);
                if (item.Parent != null)
                {
                    // InsertChild, not Children.Insert: the list alone leaves the row without a parent, and a
                    // row with no parent cannot find the document it belongs to — which is what made a copy
                    // impossible to copy again, or to delete.
                    item.Parent.InsertChild(item.TreeIndex, item.Node);
                    _owner._host.Persistence.MarkFrameModified(item.Parent);
                }
                selection.Add(item.Node);
            }
            _owner.AfterChange(selection);
        }

        public void Undo()
        {
            foreach (CopiedActor item in _items)
            {
                item.Removal = item.Pack.RemoveCopy(item.Copy);
                item.Document.Placements.Detach(item.Copy);
                item.Parent?.Children.Remove(item.Node);
                if (item.Parent != null) _owner._host.Persistence.MarkFrameModified(item.Parent);

                foreach (PrototypeRow row in item.Rows)
                {
                    _owner._host.Rnd?.DetachMeshes(new[] { row.Mesh });
                    row.Parent.Children.Remove(row.Node);
                    _owner._host.Tree.MeshCount--;
                    _owner._host.Persistence.MarkFrameModified(row.Parent);
                }
                item.Clone?.Detach();
                if (item.FrameRow != null) _owner._host.Persistence.MarkFrameModified(item.FrameRow);
            }
            _owner.AfterChange(Array.Empty<SceneNode>());
        }
    }

    // ── Import from another archive (undoable) ──

    /// <summary>
    /// Brings an actor in from another archive's pack — a light from an interior into a district that has
    /// none — as one undoable edit, and returns its tree row. Null with a reason when the pack refuses it.
    /// </summary>
    /// <param name="actorsRow">The "Actors" row of the archive that receives it.</param>
    public SceneNode? Import(SceneNode actorsRow, ActorsFile sourcePack, ActorEntry source, string name,
        System.Numerics.Vector3 position, out string? reason)
    {
        reason = null;
        if (actorsRow.Source is not ActorDocumentAdapter document || document.Placements.Packs.Count == 0)
        {
            reason = "this archive has no actor pack to add to";
            return null;
        }
        ActorsFile pack = document.Placements.Packs[0].Pack;
        ActorEntry? copy = pack.Import(sourcePack, source, name, position, out reason);
        if (copy == null) return null;

        ActorNodeAdapter adapter = document.ActorNode(copy);
        var node = new SceneNode(adapter.Name, "Actor", false) { Source = adapter };

        // Under the row of its own entity type, which a district that never had one of these does not have yet.
        SceneNode? section = actorsRow.Children.FirstOrDefault(
            c => string.Equals(c.Name, adapter.TypeName, StringComparison.OrdinalIgnoreCase));
        bool newSection = section == null;
        section ??= new SceneNode(adapter.TypeName, "Actors", true);

        var edit = new ImportActorEdit(this, copy, node, section, newSection ? actorsRow : null, document, pack);
        edit.Redo();
        _host.History.Push(edit);
        return node;
    }

    private sealed class ImportActorEdit : INodeEdit
    {
        private readonly ActorEditController _owner;
        private readonly ActorEntry _actor;
        private readonly SceneNode _node;
        private readonly SceneNode _section;
        private readonly SceneNode? _sectionParent;   // set when the import had to create the type's row
        private readonly ActorDocumentAdapter _document;
        private readonly ActorsFile _pack;
        private ActorRemoval? _removal;               // set while undone

        public ImportActorEdit(ActorEditController owner, ActorEntry actor, SceneNode node, SceneNode section,
            SceneNode? sectionParent, ActorDocumentAdapter document, ActorsFile pack)
        {
            _owner = owner;
            _actor = actor;
            _node = node;
            _section = section;
            _sectionParent = sectionParent;
            _document = document;
            _pack = pack;
        }

        public IEnumerable<SceneNode> Nodes { get { yield return _node; } }

        public void Redo()
        {
            if (_removal != null)
            {
                _pack.Restore(_removal);
                _removal = null;
            }
            _document.Placements.AddImported(_actor, _pack);
            if (_sectionParent != null && _section.Parent == null) _sectionParent.AddChild(_section);
            if (_node.Parent == null) _section.AddChild(_node);
            else if (!_section.Children.Contains(_node)) _section.InsertChild(_section.Children.Count, _node);
            _owner._host.Streamer.Actors.AddActorRow(_document.Placements, _actor, _node);
            _owner._host.Persistence.MarkFrameModified(_node);
            _owner.AfterChange(new[] { _node });
        }

        public void Undo()
        {
            // The behaviour row the import added stays in the pack: rows are addressed by position, and one
            // nothing points at costs a few bytes rather than every later index.
            _removal = _pack.Remove(_actor);
            _document.Placements.Detach(_actor);
            _section.Children.Remove(_node);
            _owner._host.Persistence.MarkFrameModified(_section);
            _owner.AfterChange();
        }
    }

    private static int IndexIn(IReadOnlyList<ActorEntry> list, ActorEntry actor)
    {
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], actor)) return i;
        return list.Count;
    }

    private List<SceneNode> SelectedActors()
    {
        var list = new List<SceneNode>();
        foreach (SceneNode n in _host.Selection.Selected)
            if (n.Source is ActorNodeAdapter) list.Add(n);
        return list;
    }

    // One deleted actor and everything needed to put it back.
    private sealed class DeletedActor
    {
        public required SceneNode Node;
        public required ActorNodeAdapter Adapter;
        public required ActorDocumentAdapter Document;
        public required ActorsFile Pack;
        public required SceneNode? Parent;
        public required int TreeIndex;
        public required int PlacementIndex;
        public required bool HadGlyph;

        public ActorRemoval? Removal;          // what the pack gave back, for the restore
        public FrameObjectBase? PlacedFrame;   // the prototype it used to place (its geometry gets hidden)
    }

    private sealed class DeleteActorsEdit : INodeEdit
    {
        private readonly ActorEditController _owner;
        private readonly List<DeletedActor> _items;

        public DeleteActorsEdit(ActorEditController owner, List<DeletedActor> items)
        {
            _owner = owner;
            _items = items;
        }

        public IEnumerable<SceneNode> Nodes
        {
            get { foreach (DeletedActor item in _items) yield return item.Node; }
        }

        public void Redo()
        {
            foreach (DeletedActor item in _items)
            {
                item.Removal = item.Pack.Remove(item.Adapter.Actor);
                item.PlacedFrame = item.Document.Placements.Detach(item.Adapter.Actor);
                _owner.SetSubtreeVisible(item.PlacedFrame, false);
                item.Parent?.Children.Remove(item.Node);
                _owner._host.Persistence.MarkFrameModified(item.Parent ?? item.Node);
            }
            _owner.AfterChange();
        }

        public void Undo()
        {
            // The pack's rows go back in REVERSE. Each removal recorded the index the row list had at that
            // moment, so putting several back in the order they were taken lands every one after the first in
            // the wrong slot. The tree rows and the placement slots recorded their ORIGINAL indices up front,
            // before anything was removed, so those go back in order.
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Removal is { } removal) _items[i].Pack.Restore(removal);
            }

            foreach (DeletedActor item in _items)
            {
                if (item.Removal == null) continue;
                item.Document.Placements.Attach(item.Adapter.Actor, item.Pack, item.PlacedFrame,
                    item.PlacementIndex, item.HadGlyph);
                _owner.SetSubtreeVisible(item.PlacedFrame, true);
                if (item.Parent != null)
                {
                    // InsertChild rather than the list: a restored row has to come back with its parent, or it
                    // can no longer reach the document that saves it (see the copy path).
                    item.Parent.InsertChild(item.TreeIndex, item.Node);
                    _owner._host.Persistence.MarkFrameModified(item.Parent);
                }
                item.Removal = null;
            }
            _owner.AfterChange();
        }
    }

    // Shows or hides the geometry an actor placed. The frame objects stay in the FrameResource either way —
    // only the actor decides whether the game ever spawns them.
    private void SetSubtreeVisible(FrameObjectBase? frame, bool visible)
    {
        if (frame != null) _host.Streamer.Actors.SetPlacedVisible(frame, visible);
    }

    // Common tail of both directions: the glyph buffers, the selection and the panels are all stale now.
    private void AfterChange() => AfterChange(Array.Empty<SceneNode>());

    private void AfterChange(IReadOnlyList<SceneNode> selection)
    {
        _host.Streamer.Actors.MarkAllDirty();
        _host.Selection.SetSelection(selection, selection.Count > 0 ? selection[^1] : null);
        _host.RaiseSceneChanged();
    }
}
