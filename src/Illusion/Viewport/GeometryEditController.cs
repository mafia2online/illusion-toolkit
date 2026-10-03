using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Bridge;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Rendering.Gpu;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Applies Blender pushes to the live scene (UI thread): geometry swaps (buffers are immutable — a
/// change means a new GPU mesh), object transforms re-localized from Blender's world matrices, and
/// object deletions — all folded into ONE undoable <see cref="CompositeEdit"/> per push. Replaced
/// GPU meshes are kept alive by the edit (the DeleteEdit precedent) so undo re-attaches them
/// without a re-upload.
/// </summary>
internal sealed class GeometryEditController
{
    private readonly D3DImageHost _host;

    public GeometryEditController(D3DImageHost host) => _host = host;

    /// <summary>One mesh whose geometry the push changed.</summary>
    public sealed record GeometryItem(SceneNode Node, BridgeMeshApplier.ApplyResult Result);

    /// <summary>One object the push moved (local matrices, already re-localized).</summary>
    public sealed record TransformItem(SceneNode Node, Matrix4x4 Before, Matrix4x4 After);

    /// <summary>One brand-new Blender object to become a frame object of <paramref name="Document"/>,
    /// parented under the document's wrapper node in the scene tree.</summary>
    public sealed record CreationItem(
        string Id, Bridge.Payload.MeshObjectPayload Payload, ISceneDocument Document, SceneNode ParentNode);

    /// <summary>Per-creation outcome: the created scene node, or the reason it was skipped.</summary>
    public sealed record CreationOutcome(string Id, SceneNode? Node, string? SkipReason);

    /// <summary>Applies one push as a single undoable edit. UI thread only.</summary>
    /// <param name="collisionEdits">Already-built collision edits (a re-cooked hull, a hull authored in
    /// Blender) to apply as part of this push. They are redone here rather than by the caller so the whole
    /// push stays one history entry.</param>
    public List<CreationOutcome> ApplyPushBatch(
        IReadOnlyList<GeometryItem> geometry, IReadOnlyList<TransformItem> transforms,
        IReadOnlyList<CreationItem> creations, INodeEdit? delete,
        IReadOnlyList<IEditAction>? collisionEdits = null)
    {
        var children = new List<IEditAction>();
        var outcomes = new List<CreationOutcome>();

        // Collision edits go FIRST so that undoing the push unwinds them last: a placement must still point at
        // its hull while everything else is being taken back off it, and a new hull's placement has to come
        // off before the hull it names is collected.
        if (collisionEdits != null)
        {
            foreach (IEditAction edit in collisionEdits)
            {
                edit.Redo();
                children.Add(edit);
            }
        }

        foreach (GeometryItem item in geometry)
        {
            item.Result.ApplyNew();
            GpuMesh? oldMesh = item.Node.Mesh;
            GpuMesh newMesh = _host.Rnd!.CreateMeshGpu(item.Result.NewMesh!);
            newMesh.Owner = item.Node;
            SwapMesh(item.Node, oldMesh, newMesh);
            // A crash prototype's copies live in the instance buffer, and a fresh GPU mesh comes back with
            // none — without this the prop would collapse from tens of thousands of copies to the one at the
            // prototype's own transform.
            if (oldMesh is { Instanced: true }) _host.Streamer.RefreshCrashInstances(item.Node);
            children.Add(new GeometryEdit(this, item.Node, item.Result, oldMesh, newMesh));
            _host.Persistence.MarkFrameModified(item.Node);

            // A frame references its mesh, it does not own it: the shipped districts put ten wanted posters on
            // one geometry block. There is a single buffer in the file, so the game redraws all of them — and
            // the viewport has to agree, or the editor shows a change the game will not honour object by
            // object. Every other frame on the block gets the new shape here, each with its own transform.
            foreach (SceneNode sharer in SharerNodes(item.Node))
            {
                GpuMesh? sharerOld = sharer.Mesh;
                GpuMesh sharerNew = _host.Rnd!.CreateMeshGpu(item.Result.NewMesh!);
                sharerNew.Owner = sharer;
                SwapMesh(sharer, sharerOld, sharerNew);
                children.Add(new SharedMeshEdit(this, sharer, sharerOld, sharerNew));
            }
        }

        foreach (TransformItem item in transforms)
        {
            ApplyTransform(item.Node, item.After);
            children.Add(new TransformSubEdit(this, item));
            _host.Persistence.MarkFrameModified(item.Node);
        }

        foreach (CreationItem item in creations)
        {
            BridgeObjectFactory.CreatedObject? created =
                BridgeObjectFactory.TryCreate(item.Document, item.Payload, out string? reason);
            if (created == null)
            {
                outcomes.Add(new CreationOutcome(item.Id, null, reason ?? "creation failed"));
                continue;
            }
            // The tree mirrors the anchor: a scene-anchored object hangs under its scene's node.
            SceneNode parentNode = item.ParentNode;
            foreach (SceneNode c in item.ParentNode.Children)
                if (created.IsAnchorNode(c.Source)) { parentNode = c; break; }
            var leaf = new SceneNode(created.Geometry.NewMesh!.Name, "Mesh", false) { Source = created.Node };
            parentNode.AddChild(leaf);
            GpuMesh mesh = _host.Rnd!.CreateMeshGpu(created.Geometry.NewMesh!);
            mesh.Owner = leaf;
            _host.Rnd.AttachMesh(mesh);
            leaf.Mesh = mesh;
            _host.Tree.MeshCount++;
            children.Add(new NewObjectEdit(this, leaf, created, mesh));
            _host.Persistence.MarkFrameModified(leaf);
            if (created.OnNameTable) _host.Persistence.MarkNameTableDirty(leaf); // it's a new spawn-list entry
            outcomes.Add(new CreationOutcome(item.Id, leaf, null));
        }

        if (delete != null)
        {
            delete.Redo();
            children.Add(delete);
        }

        if (children.Count > 0)
        {
            _host.Editing.History.Push(new CompositeEdit(children.ToArray()));
            _host.RaiseSelectionTransformChanged();
            _host.RaiseSceneChanged();
        }
        return outcomes;
    }

    /// <summary>
    /// Hides triangles of a mesh (<see cref="TriangleHider"/>) as one undoable edit: the index buffers take
    /// their planned contents and every level of detail the tree shows is redrawn. Refused for a mesh that
    /// shares its geometry — the triangles would vanish from every object drawing it — and for instanced ones.
    /// </summary>
    /// <returns>Null when applied, or why not.</returns>
    public string? HideTriangles(SceneNode node, IReadOnlyList<TriangleHider.Change> changes)
    {
        if (node.Source is not FrameNodeAdapter { Frame: FrameObjectSingleMesh mesh } adapter) return "not a mesh";
        if (changes.Count == 0) return "nothing to hide";
        if (node.Mesh is { Instanced: true }) return "an instanced mesh is drawn many times over — its triangles cannot be hidden in one place";
        if (adapter.Document.GeometrySharers(mesh).Any()) return "other objects draw the same geometry — the triangles would vanish from all of them";
        if (_host.Rnd == null) return "the viewport is not rendering yet";

        var edit = new HiddenTrianglesEdit(this, node, adapter.Document, mesh, changes);
        edit.Redo();
        _host.Editing.History.Push(edit);
        _host.RaiseSceneChanged();
        return null;
    }

    // The rows that draw one mesh: its own (the first level) and a row per further level under it.
    private static IEnumerable<SceneNode> LevelRows(SceneNode node)
    {
        yield return node;
        foreach (SceneNode child in node.Children)
        {
            if (child.Kind == "Lod" && ReferenceEquals(child.Source, node.Source)) yield return child;
        }
    }

    /// <summary>Index buffers swapped between two states, and the GPU meshes that draw each.</summary>
    private sealed class HiddenTrianglesEdit : INodeEdit
    {
        private readonly GeometryEditController _owner;
        private readonly SceneNode _node;
        private readonly SceneDocumentAdapter _document;
        private readonly FrameObjectSingleMesh _mesh;
        private readonly IReadOnlyList<TriangleHider.Change> _changes;
        private readonly List<(SceneNode Row, GpuMesh? Shown, GpuMesh? Hidden)> _rows = [];
        private bool _applied;

        public HiddenTrianglesEdit(GeometryEditController owner, SceneNode node, SceneDocumentAdapter document,
            FrameObjectSingleMesh mesh, IReadOnlyList<TriangleHider.Change> changes)
        {
            _owner = owner;
            _node = node;
            _document = document;
            _mesh = mesh;
            _changes = changes;
            foreach (SceneNode row in LevelRows(node)) _rows.Add((row, row.Mesh, null));
        }

        public IEnumerable<SceneNode> Nodes { get { yield return _node; } }

        public void Undo() => Apply(hidden: false);

        public void Redo() => Apply(hidden: true);

        private void Apply(bool hidden)
        {
            D3DImageHost host = _owner._host;
            if (!host.Tree.IsInScene(_node) || host.Rnd == null) return; // streamed out — pruning backstop
            foreach (TriangleHider.Change change in _changes)
            {
                change.Buffer.SetData(hidden ? change.After : change.Before);
                _document.MarkIndexBufferDirty(change.Buffer.Hash); // a save may have written the other state
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                (SceneNode row, GpuMesh? shown, GpuMesh? gone) = _rows[i];
                if (shown == null) continue; // a level the tree never drew
                if (gone == null && hidden && SdsMeshLoader.BuildMeshData(_mesh, row.Lod) is { } data)
                {
                    gone = host.Rnd.CreateMeshGpu(data);
                    gone.Owner = row;
                    _rows[i] = (row, shown, gone);
                }
                if (gone == null) continue;
                _owner.SwapMesh(row, hidden ? shown : gone, hidden ? gone : shown);
            }
            host.Persistence.MarkFrameModified(_node);
            _applied = hidden;
        }

        // Dropped from history: whichever mesh of each pair is DETACHED belongs to this edit alone.
        public void Discard()
        {
            foreach ((SceneNode _, GpuMesh? shown, GpuMesh? gone) in _rows)
            {
                if (gone == null) continue;
                if (_applied) shown?.Dispose();
                else gone.Dispose();
            }
        }
    }

    /// <summary>The other tree rows drawing the same geometry block as this node's mesh — the frames that just
    /// changed with it. Empty when the mesh is the block's only user, or when the sharers are not resident.</summary>
    private IEnumerable<SceneNode> SharerNodes(SceneNode node)
    {
        if (node.Source is not FrameNodeAdapter { Frame: FrameObjectSingleMesh mesh }) yield break;

        SceneDocumentAdapter? document = null;
        for (SceneNode? n = node; n != null && document == null; n = n.Parent)
        {
            document = n.Source as SceneDocumentAdapter;
        }
        if (document == null) yield break;

        foreach (FrameObjectSingleMesh other in document.GeometrySharers(mesh))
        {
            SceneNode? row = _host.Streamer.Actors.RowOf(other);
            if (row?.Mesh != null && _host.Tree.IsInScene(row)) yield return row;
        }
    }

    // Detach/attach choreography shared by apply, undo and redo. Also resyncs the world matrix —
    // the frame's transform may have been edited between the push and an undo.
    private void SwapMesh(SceneNode node, GpuMesh? detach, GpuMesh attach)
    {
        if (detach != null) _host.Rnd?.DetachMeshes(new[] { detach });
        _host.Rnd?.AttachMesh(attach);
        // AttachMesh ghosts anything outside the edit-focus set — but a swap replaces an EDITED
        // mesh, so the replacement inherits the ghost state instead (undo/redo mid-session would
        // otherwise ghost the very object being edited).
        if (detach != null) attach.Ghost = detach.Ghost;
        node.Mesh = attach;
        if (node.Source is IFrameNode fn) attach.SetWorld(fn.WorldTransform);
        _host.Selection.UpdateSelectionHighlight();
        _host.RaiseSceneChanged();
    }

    // Local-transform set (cascades world through the frame subtree) + GPU world resync.
    private void ApplyTransform(SceneNode node, Matrix4x4 local)
    {
        if (node.Source is not IFrameNode fn) return;
        fn.LocalTransform = local;
        foreach (SceneNode leaf in node.DescendantMeshLeaves())
            if (leaf.Mesh != null && leaf.Source is IFrameNode leafFrame)
                leaf.Mesh.SetWorld(leafFrame.WorldTransform);
        _host.Selection.UpdateSelectionHighlight();
        _host.RaiseSceneChanged();
    }

    /// <summary>A creation's undo unit: pulls the frame object, its buffers, its scene node and its
    /// GPU mesh out together; redo puts them all back.</summary>
    private sealed class NewObjectEdit : INodeEdit
    {
        private readonly GeometryEditController _owner;
        private readonly SceneNode _node;
        private readonly SceneNode _parent;
        private readonly BridgeObjectFactory.CreatedObject _created;
        private readonly GpuMesh _mesh;
        private bool _applied = true;

        public NewObjectEdit(GeometryEditController owner, SceneNode node,
            BridgeObjectFactory.CreatedObject created, GpuMesh mesh)
        {
            _owner = owner;
            _node = node;
            _parent = node.Parent!;
            _created = created;
            _mesh = mesh;
        }

        public IEnumerable<SceneNode> Nodes { get { yield return _node; } }

        public void Undo()
        {
            D3DImageHost host = _owner._host;
            if (host.SelectedNodes.Contains(_node)) host.Selection.Select(null);
            host.Rnd?.DetachMeshes(new[] { _mesh });
            _parent.Children.Remove(_node);
            host.Tree.MeshCount--;
            _created.Detach();
            host.Persistence.MarkFrameModified(_parent);
            if (_created.OnNameTable) host.Persistence.MarkNameTableDirty(_parent); // its spawn-list entry left
            host.RaiseSceneChanged();
            _applied = false;
        }

        public void Redo()
        {
            D3DImageHost host = _owner._host;
            _created.Reattach();
            _created.Geometry.ApplyNew();
            _parent.AddChild(_node);
            host.Rnd?.AttachMesh(_mesh);
            host.Tree.MeshCount++;
            host.Persistence.MarkFrameModified(_node);
            if (_created.OnNameTable) host.Persistence.MarkNameTableDirty(_node);
            host.RaiseSceneChanged();
            _applied = true;
        }

        public void Discard()
        {
            if (!_applied) _mesh.Dispose(); // detached — ours to release
        }
    }

    private sealed class TransformSubEdit : INodeEdit
    {
        private readonly GeometryEditController _owner;
        private readonly TransformItem _item;

        public TransformSubEdit(GeometryEditController owner, TransformItem item)
        {
            _owner = owner;
            _item = item;
        }

        public IEnumerable<SceneNode> Nodes { get { yield return _item.Node; } }

        public void Undo() => Apply(_item.Before);
        public void Redo() => Apply(_item.After);

        private void Apply(Matrix4x4 local)
        {
            if (!_owner._host.Tree.IsInScene(_item.Node)) return;
            _owner.ApplyTransform(_item.Node, local);
            _owner._host.Persistence.MarkFrameModified(_item.Node);
        }
    }

    /// <summary>
    /// A frame that draws a mesh someone else edited. Nothing of its own data changed — one geometry block
    /// serves them all — so this only swaps the GPU mesh, and undo swaps the old one back. The data edit is
    /// the <see cref="GeometryEdit"/> of the frame that was actually pushed.
    /// </summary>
    private sealed class SharedMeshEdit : INodeEdit
    {
        private readonly GeometryEditController _owner;
        private readonly SceneNode _node;
        private readonly GpuMesh? _oldMesh;
        private readonly GpuMesh _newMesh;
        private bool _applied = true;

        public SharedMeshEdit(GeometryEditController owner, SceneNode node, GpuMesh? oldMesh, GpuMesh newMesh)
        {
            _owner = owner;
            _node = node;
            _oldMesh = oldMesh;
            _newMesh = newMesh;
        }

        public IEnumerable<SceneNode> Nodes { get { yield return _node; } }

        public void Undo()
        {
            if (!_owner._host.Tree.IsInScene(_node) || _oldMesh == null) return;
            _owner.SwapMesh(_node, _newMesh, _oldMesh);
            _applied = false;
        }

        public void Redo()
        {
            if (!_owner._host.Tree.IsInScene(_node)) return;
            _owner.SwapMesh(_node, _oldMesh, _newMesh);
            _applied = true;
        }

        public void Discard()
        {
            if (_applied) _oldMesh?.Dispose();
            else _newMesh.Dispose();
        }
    }

    private sealed class GeometryEdit : INodeEdit
    {
        private readonly GeometryEditController _owner;
        private readonly SceneNode _node;
        private readonly BridgeMeshApplier.ApplyResult _result;
        private readonly GpuMesh? _oldMesh;
        private readonly GpuMesh _newMesh;
        private bool _applied = true; // starts applied (ApplyPushBatch already performed the swap)

        public GeometryEdit(GeometryEditController owner, SceneNode node,
            BridgeMeshApplier.ApplyResult result, GpuMesh? oldMesh, GpuMesh newMesh)
        {
            _owner = owner;
            _node = node;
            _result = result;
            _oldMesh = oldMesh;
            _newMesh = newMesh;
        }

        public IEnumerable<SceneNode> Nodes { get { yield return _node; } }

        public void Undo()
        {
            if (!_owner._host.Tree.IsInScene(_node)) return; // streamed out — pruning backstop
            _result.RestoreOriginal();
            if (_oldMesh != null)
            {
                _owner.SwapMesh(_node, _newMesh, _oldMesh);
            }
            _owner._host.Persistence.MarkFrameModified(_node); // memory diverged from the last save again
            _applied = false;
        }

        public void Redo()
        {
            if (!_owner._host.Tree.IsInScene(_node)) return;
            _result.ApplyNew();
            _owner.SwapMesh(_node, _oldMesh, _newMesh);
            _owner._host.Persistence.MarkFrameModified(_node);
            _applied = true;
        }

        // Dropped from history: whichever mesh is currently DETACHED belongs to this edit alone.
        public void Discard()
        {
            if (_applied) _oldMesh?.Dispose();
            else _newMesh.Dispose();
        }
    }
}
