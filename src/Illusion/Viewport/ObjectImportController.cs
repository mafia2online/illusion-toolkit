using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Rendering.Gpu;
using Illusion.Scene;
using TransplantedObject = Illusion.Assets.Frames.FrameTransplant.TransplantedObject;

namespace Illusion.Viewport;

/// <summary>
/// Puts an object carried in from another archive (<see cref="Assets.Frames.FrameTransplant"/>) into the open
/// scene as ONE undoable edit: its tree rows, its GPU meshes and — when an actor places it — the actor's
/// record in the pack and its row under Actors.
/// <para>
/// The copy's frames are already in the frame resource when this is called; what is done here is everything
/// the editor keeps beside the data, so that the object can be seen, selected, moved and saved like one the
/// archive was loaded with.
/// </para>
/// </summary>
internal sealed class ObjectImportController
{
    private readonly D3DImageHost _host;

    public ObjectImportController(D3DImageHost host) => _host = host;

    /// <summary>Shows a carried piece of scenery and returns its root row.</summary>
    /// <param name="frameRow">The "FrameResource" row of the archive that received it.</param>
    public SceneNode ImportScenery(SceneNode frameRow, TransplantedObject carried)
    {
        var edit = new ImportObjectEdit(this, frameRow, carried, BuildRows(frameRow, carried), actor: null);
        edit.Redo();
        _host.History.Push(edit);
        return edit.RootRow;
    }

    /// <summary>
    /// Takes the actor that places a carried object into the receiving archive's pack and shows both. Null
    /// with a reason when the pack refuses the actor — the carried object is then taken back out, since an
    /// object nothing places is one the game never spawns.
    /// </summary>
    /// <param name="actorsRow">The "Actors" row of the archive that receives it.</param>
    /// <param name="frameRow">The "FrameResource" row of the same archive.</param>
    public SceneNode? ImportPlaced(SceneNode actorsRow, SceneNode frameRow, ActorsFile sourcePack, ActorEntry source,
        string name, Vector3 position, Quaternion? rotation, TransplantedObject carried, out string? reason)
    {
        reason = null;
        if (actorsRow.Source is not ActorDocumentAdapter document || document.Placements.Packs.Count == 0)
        {
            carried.Detach();
            reason = "this archive has no actor pack to add to";
            return null;
        }
        ActorsFile pack = document.Placements.Packs[0].Pack;
        ActorEntry? copy = pack.Import(sourcePack, source, name, position, rotation,
            new ActorPlacedFrame(carried.Root.Name.String, carried.FrameIndex), out reason);
        if (copy == null)
        {
            carried.Detach();
            return null;
        }

        ActorNodeAdapter adapter = document.ActorNode(copy);
        var node = new SceneNode(adapter.Name, "Actor", false) { Source = adapter };
        // Under the row of its own entity type, which a district that never had one of these does not have yet.
        SceneNode? section = actorsRow.Children.FirstOrDefault(
            c => string.Equals(c.Name, adapter.TypeName, StringComparison.OrdinalIgnoreCase));
        bool newSection = section == null;
        section ??= new SceneNode(adapter.TypeName, "Actors", true);

        var placed = new PlacedActor(copy, node, section, newSection ? actorsRow : null, document, pack);
        var edit = new ImportObjectEdit(this, frameRow, carried, BuildRows(frameRow, carried), placed);
        edit.Redo();
        _host.History.Push(edit);
        return node;
    }

    // The carried frames as tree rows, nested the way the loader nests them, with a GPU mesh on every row
    // that draws. Nothing is attached yet — the edit's first Redo does that, the same as every later one.
    private Rows BuildRows(SceneNode frameRow, TransplantedObject carried)
    {
        var document = (SceneDocumentAdapter)frameRow.Source!;
        var meshByFrame = new Dictionary<FrameObjectBase, Domain.MeshData>();
        foreach ((FrameObjectSingleMesh frame, Domain.MeshData mesh) in carried.Renderables) meshByFrame[frame] = mesh;

        var all = new List<(FrameObjectBase, SceneNode)>();
        var meshes = new List<(SceneNode, GpuMesh)>();
        SceneNode root = BuildRow(carried.Root, document, meshByFrame, all, meshes, new HashSet<FrameObjectBase>());

        // A scenery object hangs under its scene's row, as the loader would put it; a prototype is a true
        // top-level frame and hangs under the FrameResource row itself.
        SceneNode parent = frameRow;
        if (carried.Anchor != null)
        {
            parent = frameRow.Children.FirstOrDefault(
                c => c.Source is FrameSceneAdapter scene && ReferenceEquals(scene.Scene, carried.Anchor)) ?? frameRow;
        }
        return new Rows(root, parent, all, meshes);
    }

    private SceneNode BuildRow(FrameObjectBase frame, SceneDocumentAdapter document,
        Dictionary<FrameObjectBase, Domain.MeshData> meshByFrame, List<(FrameObjectBase, SceneNode)> all,
        List<(SceneNode, GpuMesh)> meshes, HashSet<FrameObjectBase> seen)
    {
        seen.Add(frame);
        List<FrameObjectBase> children = [.. frame.Children.Where(seen.Add)];
        var row = new SceneNode(SdsMeshLoader.TreeName(frame), SdsMeshLoader.KindOf(frame), children.Count > 0)
        {
            Source = document.Node(frame),
        };
        all.Add((frame, row));
        if (meshByFrame.TryGetValue(frame, out Domain.MeshData? data))
        {
            GpuMesh mesh = _host.Rnd!.CreateMeshGpu(data);
            mesh.Owner = row;
            row.Mesh = mesh;
            meshes.Add((row, mesh));
        }
        foreach (FrameObjectBase child in children) row.AddChild(BuildRow(child, document, meshByFrame, all, meshes, seen));
        return row;
    }

    private sealed record Rows(
        SceneNode Root, SceneNode Parent, List<(FrameObjectBase Frame, SceneNode Row)> All,
        List<(SceneNode Row, GpuMesh Mesh)> Meshes);

    // The actor half of an import, when there is one.
    private sealed class PlacedActor(ActorEntry actor, SceneNode node, SceneNode section, SceneNode? sectionParent,
        ActorDocumentAdapter document, ActorsFile pack)
    {
        public ActorEntry Actor { get; } = actor;
        public SceneNode Node { get; } = node;
        public SceneNode Section { get; } = section;
        public SceneNode? SectionParent { get; } = sectionParent;   // set when the import had to create the type's row
        public ActorDocumentAdapter Document { get; } = document;
        public ActorsFile Pack { get; } = pack;
        public ActorRemoval? Removal { get; set; }                  // set while undone
    }

    private sealed class ImportObjectEdit : INodeEdit
    {
        private readonly ObjectImportController _owner;
        private readonly SceneNode _frameRow;
        private readonly TransplantedObject _carried;
        private readonly Rows _rows;
        private readonly PlacedActor? _actor;
        private bool _applied;

        public ImportObjectEdit(ObjectImportController owner, SceneNode frameRow, TransplantedObject carried,
            Rows rows, PlacedActor? actor)
        {
            _owner = owner;
            _frameRow = frameRow;
            _carried = carried;
            _rows = rows;
            _actor = actor;
        }

        public SceneNode RootRow => _rows.Root;

        public IEnumerable<SceneNode> Nodes { get { yield return _actor?.Node ?? _rows.Root; } }

        public void Redo()
        {
            D3DImageHost host = _owner._host;
            // The object first — frames, blocks and buffers back in the resource — so the actor below has
            // something to claim. The first Redo finds it there already, from the transplant that made it.
            if (!_carried.IsAttached) _carried.Reattach();
            if (!_rows.Parent.Children.Contains(_rows.Root)) _rows.Parent.AddChild(_rows.Root);

            var placements = ((SceneDocumentAdapter)_frameRow.Source!).Placements;
            foreach ((FrameObjectBase frame, SceneNode row) in _rows.All) host.Streamer.Actors.AddFrameRow(placements, frame, row);
            foreach ((SceneNode row, GpuMesh mesh) in _rows.Meshes)
            {
                host.Rnd?.AttachMesh(mesh);
                host.Tree.MeshCount++;
                if (row.Source is Domain.IFrameNode fn) mesh.SetWorld(fn.WorldTransform);
                if (row.Source is FrameNodeAdapter adapter) host.Streamer.Actors.AddMeshRow(placements, adapter.Frame, row);
            }
            host.Persistence.MarkFrameModified(_frameRow);
            // The name table is the game's spawn list and is rebuilt from the resource: an object that is on
            // it has to be in the rewritten one, and one that left has to be out of it.
            if (_carried.IsOnNameTable) host.Persistence.MarkNameTableDirty(_frameRow);

            if (_actor != null)
            {
                if (_actor.Removal != null)
                {
                    _actor.Pack.Restore(_actor.Removal);
                    _actor.Removal = null;
                }
                _actor.Document.Placements.AddImported(_actor.Actor, _actor.Pack, _carried.Root);
                if (_actor.SectionParent != null && !_actor.SectionParent.Children.Contains(_actor.Section))
                {
                    _actor.SectionParent.AddChild(_actor.Section);
                }
                if (!_actor.Section.Children.Contains(_actor.Node)) _actor.Section.AddChild(_actor.Node);
                host.Streamer.Actors.AddActorRow(_actor.Document.Placements, _actor.Actor, _actor.Node);
                // The meshes were made with the PROTOTYPE's own world, which is the origin — the placement
                // only exists once the actor is registered.
                host.Streamer.Actors.SyncMeshes(_actor.Node);
                host.Persistence.MarkFrameModified(_actor.Node);
            }

            _applied = true;
            AfterChange([_actor?.Node ?? _rows.Root]);
        }

        public void Undo()
        {
            D3DImageHost host = _owner._host;
            if (_actor != null)
            {
                // The behaviour row the import added stays in the pack: rows are addressed by position, and
                // one nothing points at costs a few bytes rather than every later index.
                _actor.Removal = _actor.Pack.Remove(_actor.Actor);
                _actor.Document.Placements.Detach(_actor.Actor);
                _actor.Section.Children.Remove(_actor.Node);
                host.Persistence.MarkFrameModified(_actor.Section);
            }

            foreach ((SceneNode _, GpuMesh mesh) in _rows.Meshes)
            {
                host.Rnd?.DetachMeshes(new[] { mesh });
                host.Tree.MeshCount--;
            }
            _rows.Parent.Children.Remove(_rows.Root);
            _carried.Detach();
            host.Persistence.MarkFrameModified(_frameRow);
            if (_carried.IsOnNameTable) host.Persistence.MarkNameTableDirty(_frameRow);

            _applied = false;
            AfterChange([]);
        }

        public void Discard()
        {
            if (_applied) return;
            foreach ((SceneNode _, GpuMesh mesh) in _rows.Meshes) mesh.Dispose(); // detached — ours to release
        }

        private void AfterChange(IReadOnlyList<SceneNode> selection)
        {
            D3DImageHost host = _owner._host;
            host.Streamer.Actors.MarkAllDirty();
            host.Selection.SetSelection(selection, selection.Count > 0 ? selection[^1] : null);
            host.RaiseSceneChanged();
        }
    }
}
