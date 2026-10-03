using System.IO;
using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Mcp;
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
    /// <param name="also">Edits that belong to the same import — the collision placed for it — applied after
    /// it and undone with it, as one step.</param>
    public SceneNode ImportScenery(SceneNode frameRow, TransplantedObject carried, IReadOnlyList<Domain.IEditAction> also)
    {
        var edit = new ImportObjectEdit(this, frameRow, carried, BuildRows(frameRow, carried), actor: null);
        edit.Redo();
        foreach (Domain.IEditAction extra in also) extra.Redo();
        _host.History.Push(also.Count == 0 ? edit : new CompositeEdit([edit, .. also]));
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

    /// <summary>
    /// Copies an object out of another archive into <paramref name="destination"/>, which must be in the
    /// loaded scene — the whole import as one undoable edit: an actor with the object it places, or a piece of
    /// scenery with its collision. Null on success; otherwise why not.
    /// </summary>
    /// <param name="sourceArchive">The source .sds: a full path, or one relative to the game's sds folder.</param>
    /// <param name="yawDegrees">Heading about the vertical axis, replacing the original's; null keeps it.</param>
    /// <param name="hulls">What collision scenery brings (an actor's object always brings its own).</param>
    public string? Import(FileInfo destination, string sourceArchive, string name, string newName, Vector3 at,
        float? yawDegrees, out Mcp.ObjectImportOutcome? outcome, Assets.Collisions.CollisionChoice hulls = default)
    {
        outcome = null;
        if (_host.BridgeEditedCount > 0) return "a Blender edit session is open — end it first";
        // The receiving archive's two rows: its scene, and — when it has a pack — its actors.
        SceneNode? frameRow = AllNodes().FirstOrDefault(n => n.Source is Assets.Adapters.SceneDocumentAdapter d
            && string.Equals(d.SourceArchive.FullName, destination.FullName, StringComparison.OrdinalIgnoreCase));
        if (frameRow?.Source is not Assets.Adapters.SceneDocumentAdapter scene)
        {
            return $"{destination.Name} is not in the loaded scene yet — wait for the area to finish loading";
        }
        SceneNode? actorsRow = AllNodes().FirstOrDefault(
            n => n.Source is Assets.Adapters.ActorDocumentAdapter a && ReferenceEquals(a.Scene, scene));

        var sds = new FileInfo(Path.IsPathRooted(sourceArchive)
            ? sourceArchive
            : Path.Combine(Assets.MafiaEnvironment.PcFolder, "sds", sourceArchive));
        if (!sds.Exists) return $"no such archive: {sds.FullName}";
        if (string.Equals(sds.FullName, destination.FullName, StringComparison.OrdinalIgnoreCase))
        {
            return "that is the loaded archive itself — duplicate the object instead (scene_duplicate_selected)";
        }

        try
        {
            string sourceDir = Assets.Sds.SdsMeshLoader.EnsureExtracted(sds);
            Formats.Frames.ExtractedSds source = Formats.Frames.ExtractedSds.Load(sourceDir);
            if (source.FrameResource is not { } theirs) return $"{sds.Name} carries no scene";
            Assets.Actors.ActorPlacements theirPlacements = Assets.Actors.ActorPlacements.Load(source.Manifest, theirs);

            Quaternion? facing = yawDegrees is { } yaw
                ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw * MathF.PI / 180f)
                : null;

            Formats.Actors.ActorEntry? actor = theirPlacements.All.FirstOrDefault(
                a => string.Equals(a.EntityName, name, StringComparison.OrdinalIgnoreCase));
            Assets.Frames.FrameTransplant.TransplantedObject? carried;
            Assets.Sds.ArchiveCarry.Report carry;
            string kind;
            string? reason;
            string collision = "its own hulls, carried with it";

            // What the scene does not hold — textures, item descriptions, the prefab entry — goes into the working
            // copy as soon as the frames are copied and BEFORE the viewport builds the meshes, which look their
            // textures up as they are made. Inert until something names them, so a refusal after this point
            // leaves a few unused files rather than a scene that names missing ones.
            // Geometry this object's earlier imports already brought is drawn from rather than copied again.
            Assets.Sds.ImportGeometry shared = Assets.Sds.ImportGeometry.Load(
                Assets.MafiaEnvironment.ExtractedDir(destination),
                Path.GetRelativePath(Path.Combine(Assets.MafiaEnvironment.PcFolder, "sds"), sds.FullName));

            Assets.Sds.ArchiveCarry.Report Carry(Assets.Frames.FrameTransplant.TransplantedObject copied) =>
                Assets.Sds.ArchiveCarry.Carry(sourceDir, Assets.MafiaEnvironment.ExtractedDir(destination),
                    copied.MaterialHashes, copied.CollisionHashes, actor?.LinkedDefinition);
            if (actor != null)
            {
                if (theirPlacements.TargetOf(actor) is not { } prototype)
                {
                    return $"'{name}' places no object of its archive's scene — it travels with actor_import";
                }
                if (actorsRow == null) return $"{destination.Name} has no actor pack to add an actor to";
                if (theirPlacements.PackOf(actor) is not { } theirPack) return $"'{name}' belongs to no pack";

                carried = Assets.Frames.FrameTransplant.TryTransplant(scene, theirs, prototype, newName,
                    Assets.Frames.FrameTransplant.Standing.Prototype, Matrix4x4.Identity, shared, out reason);
                if (carried == null) return reason ?? "the object could not be copied";
                carry = Carry(carried);
                if (ImportPlaced(actorsRow, frameRow, theirPack, actor, newName, at, facing,
                        carried, out reason) is not { Source: ActorNodeAdapter placed })
                {
                    return reason ?? "the pack refused the actor";
                }
                // A door is often opened by its own building's script rather than by the player — a shop's front
                // door has its actions switched off and the shop turns them on in opening hours. Carried out of
                // that building nothing turns them on, so the copy's own row lets the player open it.
                if (actor.Type == EntityType.Door
                    && ((ActorDocumentAdapter)actorsRow.Source!).Placements.PackOf(placed.Actor)?.PropertiesOf(placed.Actor)
                        ?.Fields.FirstOrDefault(f => f.Name == "ActorActionsEnabled") is { Number: 0 } actions)
                {
                    actions.Number = 1;
                }
                kind = "actor";
            }
            else
            {
                Formats.Frames.ObjectTypes.FrameObjectBase? frame = theirs.FrameObjects.Values
                    .OfType<Formats.Frames.ObjectTypes.FrameObjectBase>()
                    .FirstOrDefault(o => string.Equals(o.Name.String, name, StringComparison.OrdinalIgnoreCase));
                if (frame == null) return $"{sds.Name} has neither an actor nor a frame object named '{name}'";

                // Turned and scaled as it was — the matrix an actor gives it folded in, since a prototype's own
                // is the origin — and STANDING where it is asked to: the middle of the bottom of its geometry
                // lands on the point, since a district mesh's origin can be anywhere in or around it.
                Matrix4x4 world = frame.WorldTransform * theirPlacements.For(frame);
                Matrix4x4 sourceWorld = world;
                if (facing is { } heading)
                {
                    Formats.Mathematics.MatrixExtensions.TryDecomposeRS(world, out Vector3 scale, out _, out _);
                    world = Formats.Mathematics.MatrixExtensions.SetMatrix(heading, scale, Vector3.Zero);
                }
                Vector3 standsOn = Assets.Frames.FrameTransplant.BaseOf(frame) ?? Vector3.Zero;
                world.Translation = at - Vector3.TransformNormal(standsOn, world);

                carried = Assets.Frames.FrameTransplant.TryTransplant(scene, theirs, frame, newName,
                    Assets.Frames.FrameTransplant.Standing.Scenery, world, shared, out reason);
                if (carried == null) return reason ?? "the object could not be copied";
                carry = Carry(carried);
                ImportScenery(frameRow, carried,
                    SceneryCollision(destination, source, frame, sourceWorld, carried, hulls, out collision));
                kind = "scenery";
            }

            shared.Save();
            _host.MarkArchiveModified(destination);

            outcome = new ObjectImportOutcome(kind, newName, carried.Pairs.Count, carried.Renderables.Count,
                carry.Textures, carry.ItemDescriptions.Count, carry.Prefab, carry.Elsewhere, carry.Unresolved.Count,
                collision);
            _host.RaiseNotice(
                $"Imported '{name}' from {sds.Name} as '{newName}' ({kind}): {outcome.Frames} frame(s), "
                + $"{outcome.Meshes} mesh(es), {carry.Textures.Count} texture(s), {carry.ItemDescriptions.Count} item "
                + $"description(s){(carry.Prefab ? ", a prefab entry" : "")} carried"
                + (carry.Borrowed.Count > 0
                    ? $"; {carry.Borrowed.Count} of the textures came from {string.Join(", ", carry.Borrowed.Select(b => b.Archive).Distinct())}"
                    : "")
                + (carry.Elsewhere.Count > 0 ? $"; {carry.Elsewhere.Count} texture(s) are in no extracted archive" : "")
                + (carry.Unresolved.Count > 0 ? $"; {carry.Unresolved.Count} collision hull(s) found no description" : "")
                + $"; collision: {collision}",
                isError: carry.Unresolved.Count > 0);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or InvalidOperationException or Formats.SdsFormatException)
        {
            return "the import failed: " + ex.Message;
        }
    }

    /// <summary>
    /// The collision a piece of scenery brings: the source's hulls that stand inside its footprint, re-placed
    /// by the same move that took the object to its new spot — or one cooked here, as its convex hull, its box
    /// or its triangles, when it had none of its own or <paramref name="choice"/> asks for one. Returned as the
    /// edits that add them, for the import to apply and undo as one.
    /// </summary>
    private IReadOnlyList<Domain.IEditAction> SceneryCollision(FileInfo destination,
        Formats.Frames.ExtractedSds source, Formats.Frames.ObjectTypes.FrameObjectBase original, Matrix4x4 sourceWorld,
        Assets.Frames.FrameTransplant.TransplantedObject carried, Assets.Collisions.CollisionChoice choice, out string summary)
    {
        if (choice == Assets.Collisions.CollisionChoice.None)
        {
            summary = "none, as asked";
            return [];
        }
        SceneNode? layer = AllNodes().FirstOrDefault(n => n.Source is Assets.Adapters.CollisionDocumentAdapter c
            && string.Equals(c.SourceArchive.FullName, destination.FullName, StringComparison.OrdinalIgnoreCase));
        if (layer?.Source is not Assets.Adapters.CollisionDocumentAdapter document)
        {
            summary = "none — this archive's collision layer is not loaded";
            return [];
        }

        Matrix4x4 targetWorld = carried.Root.WorldTransform;
        var hulls = new List<Assets.Collisions.CollisionCarry.Hull>();
        IReadOnlyList<string> theirFiles = source.Manifest.GetFiles("Collisions");
        if (choice == Assets.Collisions.CollisionChoice.Auto && theirFiles.Count > 0
            && Assets.Frames.FrameTransplant.BoundsOf(original) is { } bounds)
        {
            // The footprint in the source's world: the root-space box through the object's own matrix.
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (int corner = 0; corner < 8; corner++)
            {
                var p = new Vector3((corner & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                    (corner & 2) == 0 ? bounds.Min.Y : bounds.Max.Y, (corner & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
                Vector3 w = Vector3.Transform(p, sourceWorld);
                min = Vector3.Min(min, w);
                max = Vector3.Max(max, w);
            }
            hulls.AddRange(Assets.Collisions.CollisionCarry.FromSource(Formats.Collisions.CollisionFile.Load(theirFiles[0]),
                document.Collision, min, max, sourceWorld, targetWorld));
        }

        string? refusal = null;
        if (hulls.Count == 0)
        {
            byte group = document.Collision.Instances.Count == 0
                ? (byte)0
                : document.Collision.Instances.GroupBy(i => i.Group).OrderByDescending(g => g.Count()).First().Key;
            Assets.Collisions.HullShape shape = choice switch
            {
                Assets.Collisions.CollisionChoice.Box => Assets.Collisions.HullShape.Box,
                Assets.Collisions.CollisionChoice.Mesh => Assets.Collisions.HullShape.Mesh,
                _ => Assets.Collisions.HullShape.Convex,
            };
            if (Assets.Collisions.CollisionCarry.FromGeometry(document.Collision,
                    Assets.Frames.FrameTransplant.TrianglesOf(carried.Root), targetWorld, group, shape, out refusal) is { } cooked)
            {
                hulls.Add(cooked);
            }
        }

        var edits = new List<Domain.IEditAction>();
        for (int i = 0; i < hulls.Count; i++)
        {
            string name = hulls.Count == 1 ? $"{carried.Root.Name} collision" : $"{carried.Root.Name} collision {i + 1}";
            edits.AddRange(_host.CollisionEditing.BuildCreateHull(document, layer, hulls[i].Added, hulls[i].Placement, name) ?? []);
        }
        // Written down, so the hulls move and are deleted with the object (see D3DImageHost.LinkedCollisionNodes).
        Assets.Sds.ImportLinks.Set(Assets.MafiaEnvironment.ExtractedDir(destination), carried.Root.Name.String,
            hulls.Select(h => h.Placement.Hash));
        summary = hulls.Count == 0
            ? "none — " + (refusal ?? "nothing to make one of")
            : hulls[0].FromSource
                ? $"{hulls.Count} hull(s) of its own from the source archive"
                : choice switch
                {
                    Assets.Collisions.CollisionChoice.Box => "its box, cooked here",
                    Assets.Collisions.CollisionChoice.Mesh => "one cooked from all its triangles",
                    Assets.Collisions.CollisionChoice.Convex => "its convex hull, cooked here",
                    _ => "its convex hull, cooked here (it had none of its own)",
                };
        return edits;
    }

    /// <summary>A name nothing in <paramref name="destination"/>'s scene or pack answers to yet:
    /// <paramref name="stem"/>, or <c>stem_2</c>, <c>stem_3</c>, …</summary>
    public string FreeName(FileInfo destination, string stem)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SceneNode node in AllNodes())
        {
            if (node.Source is SceneDocumentAdapter scene
                && string.Equals(scene.SourceArchive.FullName, destination.FullName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (object value in scene.Frame.FrameObjects.Values)
                {
                    if (value is FrameObjectBase frame && frame.Name.String is { Length: > 0 } n) taken.Add(n);
                }
                foreach (ActorEntry actor in scene.Placements.All) taken.Add(actor.EntityName);
            }
        }
        string candidate = stem;
        for (int i = 2; taken.Contains(candidate); i++) candidate = $"{stem}_{i}";
        return candidate;
    }

    private IEnumerable<SceneNode> AllNodes()
    {
        var stack = new Stack<SceneNode>(_host.Roots.Reverse());
        while (stack.Count > 0)
        {
            SceneNode node = stack.Pop();
            yield return node;
            for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
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
