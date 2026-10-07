using Illusion.Assets.Actors;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;

namespace Illusion.Assets.Adapters;

/// <summary>
/// Adapts one loaded vendor <see cref="FrameResource"/> (plus its source archive) into the Domain's
/// <see cref="ISceneDocument"/> port, and wraps its frame objects as <see cref="IFrameNode"/>s. Wrapping is
/// canonical — one adapter per frame object for the document's lifetime — because the editor's selection,
/// group-drag and delete logic key sets by reference identity (see <see cref="IFrameNode"/> remarks).
/// </summary>
public sealed class SceneDocumentAdapter : ISceneDocument
{
    private readonly FrameResource _frame;
    private readonly Dictionary<FrameObjectBase, FrameNodeAdapter> _nodes = new();
    private readonly HashSet<ulong> _dirtyVertexBuffers = new();
    private readonly HashSet<ulong> _dirtyIndexBuffers = new();
    private bool _nameTableDirty;
    private readonly Bridge.SdsGeometrySaver.PoolState _pools = new();

    public SceneDocumentAdapter(FrameResource frame, FileInfo sourceArchive, ActorPlacements? placements = null)
    {
        _frame = frame;
        SourceArchive = sourceArchive;
        Placements = placements ?? ActorPlacements.Empty;
    }

    public FileInfo SourceArchive { get; }

    /// <summary>Where the scene's actor pack puts its prototype objects. Objects an actor places carry an
    /// identity matrix of their own, so every world transform this document hands out folds the actor's
    /// matrix in — see <see cref="ActorPlacements"/>.</summary>
    public ActorPlacements Placements { get; }

    /// <summary>The wrapped vendor resource — for the asset layer's own machinery (the bridge's
    /// object factory); the UI never touches it.</summary>
    internal FrameResource Frame => _frame;

    /// <summary>
    /// Every name this document's graph currently answers to, keyed by FNV64 — frame objects and the bones of
    /// every skinned model, which is exactly the set a PREFAB can point at.
    ///
    /// <para>
    /// This is the LIVE graph, not the file: a frame minted this session is here and is not on disk until
    /// Save. Anything resolving prefab hashes for the panel has to fold these in, or a part made a moment ago
    /// shows as a bare hash and cannot be picked in another slot.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<ulong, string> FrameNames()
    {
        var names = new Dictionary<ulong, string>();
        foreach (object o in _frame.FrameObjects?.Values ?? Enumerable.Empty<object>())
        {
            if (o is FrameObjectBase f && f.Name.String is { Length: > 0 } n) names[f.Name.Hash] = n;
        }
        foreach (FrameObjectModel model in
                 (_frame.FrameObjects?.Values ?? Enumerable.Empty<object>()).OfType<FrameObjectModel>())
        {
            Formats.Hashing.HashName[] bones;
            try { bones = model.GetSkeletonObject().BoneNames ?? []; }
            catch (Exception) { continue; }
            foreach (Formats.Hashing.HashName bone in bones)
            {
                if (bone.String is { Length: > 0 } bn) names[bone.Hash] = bn;
            }
        }
        return names;
    }

    /// <summary>
    /// Where each of the car's bones stands, keyed by the hash the prefab names it with.
    ///
    /// <para>
    /// What the property panel needs to show a collision volume's position in the CAR's axes. A volume is
    /// written in the space of a bone — its own part's, or that of the part it hangs off — and most car bones
    /// are turned relative to the car, so the stored numbers do not mean what a person reading X/Y/Z assumes:
    /// on a door-mounted window, typing into Z moves the box along the car.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<ulong, System.Numerics.Matrix4x4> BoneWorlds()
    {
        var worlds = new Dictionary<ulong, System.Numerics.Matrix4x4>();
        foreach (FrameObjectModel model in
                 (_frame.FrameObjects?.Values ?? Enumerable.Empty<object>()).OfType<FrameObjectModel>())
        {
            Formats.Hashing.HashName[] bones;
            try { bones = model.GetSkeletonObject().BoneNames ?? []; }
            catch (Exception) { continue; }
            for (int i = 0; i < bones.Length; i++)
            {
                worlds.TryAdd(bones[i].Hash, model.GetJointWorldTransform(i));
            }
        }
        return worlds;
    }

    private readonly Dictionary<Formats.Actors.ActorEntry, ActorNodeAdapter> _actorNodes = new();

    /// <summary>Wraps one of the scene's actors as a property source, canonically — the tree and the property
    /// panel must agree on identity the same way they do for frame objects.</summary>
    public ActorNodeAdapter ActorNode(Formats.Actors.ActorEntry actor)
    {
        if (!_actorNodes.TryGetValue(actor, out ActorNodeAdapter? node))
        {
            _actorNodes[actor] = node = new ActorNodeAdapter(actor, Placements);
        }
        return node;
    }

    public int ObjectCount => _frame.FrameObjects?.Count ?? 0;
    public int GeometryCount => _frame.FrameGeometries?.Count ?? 0;
    public int MaterialCount => _frame.FrameMaterials?.Count ?? 0;
    public int SkeletonCount => _frame.FrameSkeletons?.Count ?? 0;
    public int SceneCount => _frame.FrameScenes?.Count ?? 0;

    /// <summary>Flags a buffer whose in-memory bytes diverged from the extracted folder — its pool
    /// file is rewritten by the next <see cref="SaveWorkingCopy"/>.</summary>
    public void MarkVertexBufferDirty(ulong hash) => _dirtyVertexBuffers.Add(hash);

    /// <inheritdoc cref="MarkVertexBufferDirty"/>
    public void MarkIndexBufferDirty(ulong hash) => _dirtyIndexBuffers.Add(hash);

    /// <inheritdoc cref="ISceneDocument.MarkNameTableDirty"/>
    public void MarkNameTableDirty() => _nameTableDirty = true;

    private readonly HashSet<FrameObjectCollision> _movedCollisionStubs = new();

    /// <summary>
    /// Records that a collision stub has been moved, so the next save carries the new placement through to the
    /// half of the archive the game reads.
    /// <para>
    /// On a car the same placement is written down twice — as this frame's matrix and as a collision volume in
    /// the PREFAB — and only the prefab is read. Writing just the frame is exactly the change that looked like
    /// it worked and did nothing. Only stubs that actually moved are carried over: 37 of the 1097 shipped
    /// pairs already disagree, and rewriting those from a frame nobody touched would change cars nobody asked
    /// about.
    /// </para>
    /// </summary>
    public void MarkCollisionStubMoved(FrameObjectCollision stub) => _movedCollisionStubs.Add(stub);

    private readonly HashSet<FrameObjectBase> _movedMarkerFrames = new();

    /// <summary>
    /// Records that a helper frame has been moved, so the next save carries it through to the row the game
    /// actually reads.
    /// <para>
    /// The same trap the collision stubs set, in two more places: a climb box is stated by its ROW and the
    /// game never reads the Dummy, and a seat's row carries the position its Dummy stands at on 212 of the
    /// 213 shipped seats. Only the frames that MOVED are carried over — 18 of the 280 shipped climb-box rows
    /// already disagree with their Dummy, and rewriting those from a frame nobody touched would change cars
    /// nobody asked about.
    /// </para>
    /// </summary>
    public void MarkMarkerFrameMoved(FrameObjectBase frame) => _movedMarkerFrames.Add(frame);

    /// <summary>Why the last save could not carry a moved marker through to the row the game reads, or null
    /// when it could. The drag is still held and the next save will try again; this is what a host tells the
    /// modder so that "I moved it and nothing happened" is a sentence rather than a mystery.</summary>
    public string? MarkerRowsRefused { get; private set; }

    /// <summary>
    /// This archive's car collision, as the PREFAB describes it — every deformable part's volumes, resolved
    /// against the frame graph. Empty for an archive that is not a car. Read fresh each time: the prefab lives
    /// on disk and several editors write it.
    /// </summary>
    public IReadOnlyList<Collisions.PlacedPhysicsVolume> PhysicsVolumes() =>
        Collisions.CarPhysicsVolumes.Load(MafiaEnvironment.ExtractedDir(SourceArchive), _frame);

    /// <summary>
    /// Moves every collision stub onto the placement its prefab volume gives it — for after an edit made to
    /// the FILE rather than to the scene, so the handle stops disagreeing with what it holds. In memory only;
    /// returns how many had to move.
    /// </summary>
    /// <remarks>
    /// A stub the user has DRAGGED is left alone. Its new placement lives only in the frame until a save
    /// writes it through, so snapping it to the prefab here would silently throw the drag away — and this
    /// runs after edits made in the property panel, which happen mid-placement.
    /// </remarks>
    public int AdoptPrefabPlacements() =>
        Collisions.CarPhysicsVolumes.AlignStubsToPrefab(
            MafiaEnvironment.ExtractedDir(SourceArchive), _frame, _movedCollisionStubs);

    /// <summary>
    /// Writes what the editor has changed into the archive's working copy.
    ///
    /// <para>
    /// A CAR goes through the aggregate, whole: its frame resource, its name table and the prefab rows its
    /// moved frames are written down in twice are one save, because a car's collision, its climb boxes and
    /// its seats are each stated in two places and only one of them is what the game reads. Every other kind
    /// of archive takes the plain frame writer, which is what it has always been.
    /// </para>
    /// </summary>
    public string SaveWorkingCopy()
    {
        string extracted = MafiaEnvironment.ExtractedDir(SourceArchive);
        MarkerRowsRefused = null;
        string? written = _isCar == false ? null : SaveCar(extracted);
        if (written == null)
        {
            // Not a car: the frame resource, and its name table after it — the rebuild reads the object order
            // and scene indices that serializing the resource finalises, so it has to run second. Verified a
            // semantic fixpoint across every district (see --probe-nametable).
            written = SdsWriter.SaveFrameResource(_frame, SourceArchive);
            if (_nameTableDirty)
            {
                SdsWriter.SaveFrameNameTable(_frame, SourceArchive);
                _nameTableDirty = false;
            }
            // Textures an import carried in that the scene no longer names — an undone import, or one a scene
            // closed without saving took with it — leave with this save.
            Sds.ArchiveCarry.SweepUnused(extracted, _frame);
        }

        // The pools describe geometry the FRAME RESOURCE indexes, so they go only when it went. A refused car
        // save wrote neither, and flushing the buffers anyway would leave the working copy holding vertices
        // no frame in it accounts for.
        // Every pool is looked at, not only the dirty ones: a deleted object or a rebuilt mesh leaves buffers
        // nothing draws from any more, and those stay out of the files (in memory they stay, for undo).
        if (MarkerRowsRefused == null)
        {
            Bridge.SdsGeometrySaver.SavePools(_frame, _dirtyVertexBuffers, _dirtyIndexBuffers, _pools);
            _dirtyVertexBuffers.Clear(); // the working copy now matches memory
            _dirtyIndexBuffers.Clear();
        }
        return written;
    }

    /// <summary>
    /// Whether this archive carries a car, once anything has asked. A district's PREFAB holds over a thousand
    /// entries and opening it to find no car is work paid on every save of a scene that never had one, so the
    /// answer is remembered — a document is one archive's graph for its whole life, and an archive does not
    /// become a car.
    /// </summary>
    private bool? _isCar;

    /// <summary>
    /// The car half of a save, through the one seam. Null when this archive carries no car, which is how the
    /// caller knows to fall back to the plain frame writer.
    ///
    /// <para>
    /// The moved-frame sets are cleared only when the rows were actually written. A save can be REFUSED — the
    /// prefab has to survive being written and read back, and it refuses outright if another editor has
    /// written the file since — and forgetting the drag anyway is how a marker moves in the editor and
    /// nowhere else. They are held for the next save instead, and the modder is told through
    /// <see cref="MarkerRowsRefused"/> — which is also the only place a refusal is reported, since the
    /// string this answers with names the archive's frame resource either way.
    /// </para>
    /// </summary>
    private string? SaveCar(string extracted)
    {
        Cars.Car? car = Cars.Car.SaveScene(
            extracted, _frame, _movedMarkerFrames, _movedCollisionStubs, _nameTableDirty, out string? lost);
        _isCar = car != null;
        if (car == null) return null;

        MarkerRowsRefused = lost;
        if (lost == null)
        {
            _movedMarkerFrames.Clear();
            _movedCollisionStubs.Clear();
            _nameTableDirty = false;
        }
        return FrameResourceFile(extracted) ?? car.PrefabPath;
    }

    /// <summary>Where this archive's frame resource lives, for the caller that wants to say what was
    /// written. Null when the manifest cannot be read or lists none.</summary>
    private static string? FrameResourceFile(string extracted)
    {
        try
        {
            IReadOnlyList<string> files = Formats.Archive.SdsManifest.Load(extracted)
                .GetFiles("FrameResource");
            return files.Count > 0 ? files[0] : null;
        }
        catch (Exception ex) when (ex is IOException or Formats.SdsFormatException)
        {
            return null;
        }
    }

    /// <inheritdoc cref="ISceneDocument.Reparent"/>
    public bool Reparent(IFrameNode child, ISceneSource? newParent)
    {
        if (child is not FrameNodeAdapter childAdapter) return false;
        FrameObjectBase childFrame = childAdapter.Frame;
        TraceReparent(childFrame, newParent);

        FrameEntry? parentEntry = newParent switch
        {
            FrameNodeAdapter fna => fna.Frame,
            FrameSceneAdapter fsa => fsa.Scene,
            _ => null, // ISceneDocument / null → a scene root
        };

        // Reject self / a descendant (would make a cycle). SetParent itself has no such guard.
        if (parentEntry is FrameObjectBase pf &&
            (ReferenceEquals(pf, childFrame) || childFrame.IsFrameOwnChildren(pf.RefID)))
            return false;

        // Detach from any old scene folder's runtime children — SetParent only detaches the link its own slot
        // owns, and a scene-parented object is held by the folder, not by a frame.
        if (_frame.FrameScenes != null)
            foreach (FrameHeaderScene s in _frame.FrameScenes.Values) s.Children.Remove(childFrame);

        // The two slots mean different things, so each target shape writes a different pair. These are the only
        // three shapes the game ships: a scene-anchored root (-1, scene), a nested object (object, chain root),
        // and a true top-level frame (-1, -1). Writing a scene index into ParentIndex1 — which is what this used
        // to do for every target — produces a combination that occurs nowhere in the stock game and that the
        // engine refuses to stream.
        switch (parentEntry)
        {
            case FrameHeaderScene scene:
                childFrame.SetParent(ParentInfo.ParentType.ParentIndex2, scene);
                childFrame.SetParent(ParentInfo.ParentType.ParentIndex1, null);
                scene.Children.Add(childFrame);
                break;

            case FrameObjectBase parentFrame:
                childFrame.SetParent(ParentInfo.ParentType.ParentIndex1, parentFrame);
                childFrame.SetParent(ParentInfo.ParentType.ParentIndex2, AnchorOf(parentFrame));
                break;

            default: // (root)
                childFrame.ClearBothParents();
                break;
        }

        childFrame.SetWorldTransform();
        return true;
    }

    /// <summary>
    /// The anchor a child of <paramref name="parent"/> must record in ParentIndex2: the top of the parent's
    /// ParentIndex1 chain, or — when that top is itself scene-anchored — the scene folder it lives in. Null when
    /// the chain ends at a rootless frame, which is the (object, -1) shape.
    /// </summary>
    private FrameEntry? AnchorOf(FrameObjectBase parent)
    {
        FrameObjectBase top = parent;
        var seen = new HashSet<FrameObjectBase> { top };
        while (top.Parent is { } next && seen.Add(next)) top = next; // seen guards a malformed cycle
        return top.Root as FrameEntry ?? FindScene(top);
    }

    /// <summary>The scene folder that holds <paramref name="frame"/>, by runtime membership.</summary>
    private FrameHeaderScene? FindScene(FrameObjectBase frame) =>
        _frame.FrameScenes?.Values.FirstOrDefault(s => s.Children.Contains(frame));

    // Diagnostic: records every reparent with a stack trace when ILLUSION_TRACE_PARENT=1. A reparent rewrites the
    // object's ParentIndex1, so an unintended one silently corrupts the FrameResource; this is how an unintended
    // caller gets identified rather than guessed at. Off (and free) unless the variable is set.
    private static void TraceReparent(FrameObjectBase child, ISceneSource? newParent)
    {
        if (Environment.GetEnvironmentVariable("ILLUSION_TRACE_PARENT") != "1") return;
        try
        {
            string parentName = newParent switch
            {
                FrameNodeAdapter fna => "object " + fna.Frame.Name,
                FrameSceneAdapter fsa => "scene " + fsa.Scene.Name,
                null => "(root)",
                _ => newParent.GetType().Name,
            };
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "illusion_parent_trace.txt"),
                $"REPARENT '{child.Name}' (ParentIndex1={child.ParentIndex1.Index}, ParentIndex2={child.ParentIndex2.Index})"
                + $" -> {parentName}\n{new System.Diagnostics.StackTrace(true)}\n\n");
        }
        catch (Exception) { /* tracing must never break an edit */ }
    }

    /// <summary>
    /// How many OTHER frame objects of this resource draw the same geometry block.
    ///
    /// A frame references its mesh, it does not own it, and the shipped districts reuse blocks heavily — 62% of
    /// italy's mesh frames sit on a block another frame also uses, and its three wanted posters share one
    /// between them. An edit to such a mesh is an edit to all of them: there is one buffer in the file, and the
    /// viewport only looks otherwise because it swaps the edited node's GPU mesh alone.
    /// </summary>
    public IReadOnlyList<FrameObjectSingleMesh> GeometrySharers(FrameObjectSingleMesh mesh)
    {
        if (mesh.Geometry is not { } geometry) return [];
        var sharers = new List<FrameObjectSingleMesh>();
        foreach (object? value in _frame.FrameObjects.Values)
        {
            if (value is FrameObjectSingleMesh other
                && !ReferenceEquals(other, mesh)
                && ReferenceEquals(other.Geometry, geometry))
            {
                sharers.Add(other);
            }
        }
        return sharers;
    }

    /// <summary>
    /// The other frame objects that draw from one of the given index buffers through a geometry block of their
    /// OWN - which <see cref="GeometrySharers"/> does not see. No shipped district has one (measured: 0 of 1310
    /// buffers in uppertown); an object imported twice with a level's draw distance changed in between does.
    /// </summary>
    public IReadOnlyList<FrameObjectSingleMesh> IndexBufferSharers(FrameObjectSingleMesh mesh, IReadOnlySet<ulong> indexBuffers)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(indexBuffers);
        var sharers = new List<FrameObjectSingleMesh>();
        foreach (object? value in _frame.FrameObjects.Values)
        {
            if (value is not FrameObjectSingleMesh other || ReferenceEquals(other, mesh)
                || other.Geometry is not { LOD: { } levels } || ReferenceEquals(other.Geometry, mesh.Geometry))
            {
                continue;
            }
            for (int lod = 0; lod < levels.Length; lod++)
            {
                if (other.GetIndexBuffer(lod) is { } buffer && indexBuffers.Contains(buffer.Hash))
                {
                    sharers.Add(other);
                    break;
                }
            }
        }
        return sharers;
    }

    /// <summary>The other frame objects that draw with the same material block: re-pointing one of its slots
    /// re-points it for all of them. Rare in the shipped districts (8 of 3099 mesh frames in uppertown).</summary>
    public IReadOnlyList<FrameObjectSingleMesh> MaterialSharers(FrameObjectSingleMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        // Asked through the reference table first: the Material property makes an empty block for a mesh that
        // has none, and a question must not leave every such mesh of the scene with one.
        if (!mesh.Refs.ContainsKey(FrameEntryRefTypes.Material) || mesh.Material is not { } material) return [];
        return [.. _frame.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
            .Where(other => !ReferenceEquals(other, mesh) && other.Refs.ContainsKey(FrameEntryRefTypes.Material)
                            && ReferenceEquals(other.Material, material))];
    }

    /// <summary>The canonical <see cref="IFrameNode"/> adapter for a frame object of this document.</summary>
    public FrameNodeAdapter Node(FrameObjectBase frame)
    {
        if (!_nodes.TryGetValue(frame, out FrameNodeAdapter? node))
        {
            _nodes[frame] = node = new FrameNodeAdapter(frame, this);
        }
        return node;
    }

    private readonly Dictionary<(FrameObjectModel Model, int Index), BoneNodeAdapter> _bones = new();

    /// <summary>The canonical adapter for one bone of a skinned model — canonical for the same reason frame
    /// objects are: selection and the undo history key by reference identity.</summary>
    public BoneNodeAdapter Bone(FrameObjectModel model, int index)
    {
        if (!_bones.TryGetValue((model, index), out BoneNodeAdapter? bone))
        {
            _bones[(model, index)] = bone = new BoneNodeAdapter(model, index, this);
        }
        return bone;
    }
}
