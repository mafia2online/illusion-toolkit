using Illusion.Assets.Adapters;
using Illusion.Domain;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;

namespace Illusion.Assets.Frames;

/// <summary>
/// Copies the prototype an actor places — the whole subtree, not just the node the pack names.
///
/// An actor's prototype is a holder frame parked at the origin with its meshes (and often a collision hull)
/// hanging under it; the actor supplies the world matrix. A frame is spawned by exactly one actor, so a
/// copied actor cannot share the original's object: it needs its own subtree and its own scene reference.
///
/// The meshes go through <see cref="FrameDuplicator"/> unchanged — same buffer, geometry and material deep
/// copies, same rollback when a pool is full — and are then re-parented under the cloned holder instead of
/// the original's parents. Everything the game needs to spawn the copy (name-table membership, the anchored
/// bit, LOD draw distances) rides along in the copy constructors, because the source is an object the game
/// already spawns.
/// </summary>
public static class ActorPrototypeCloner
{
    /// <summary>A cloned prototype with everything undo needs to take it out and put it back.</summary>
    public sealed class ClonedPrototype
    {
        internal FrameResource Resource = null!;
        internal readonly List<FrameObjectBase> Holders = new();          // the frames we minted ourselves
        internal readonly List<FrameDuplicator.DuplicatedObject> Meshes = new();

        /// <summary>The clone's root — what the new actor places.</summary>
        public FrameObjectBase Root { get; internal set; } = null!;

        /// <summary>The cloned meshes, each with a render-ready copy for the caller's GPU upload.</summary>
        public IReadOnlyList<(FrameObjectSingleMesh Frame, MeshData Mesh)> Renderables { get; internal set; } = [];

        /// <summary>Where <see cref="Root"/> sits in the frame resource's object list — the index a scene
        /// reference stores. Read it fresh rather than caching: an undone delete can reorder the list.</summary>
        public uint FrameIndex => IndexOf(Resource, Root);

        /// <summary>Whether the clone is currently part of the scene (false once <see cref="Detach"/> ran).</summary>
        public bool IsAttached => Resource.FrameObjects.ContainsKey(Root.RefID);

        /// <summary>Whether any cloned frame inherited frame-name-table membership. The table is the game's
        /// spawn list and is rebuilt from the resource, so the caller must mark it dirty or the copy is an
        /// object the table never mentions.</summary>
        public bool IsOnNameTable { get; internal set; }

        /// <summary>Takes the clone back out of the frame resource (undo).</summary>
        public void Detach()
        {
            foreach (FrameDuplicator.DuplicatedObject mesh in Meshes) mesh.Detach();
            foreach (FrameObjectBase holder in Holders)
            {
                holder.SetParent(ParentInfo.ParentType.ParentIndex1, null);
                holder.SetParent(ParentInfo.ParentType.ParentIndex2, null);
                foreach (FrameHeaderScene scene in Resource.FrameScenes.Values) scene.Children.Remove(holder);
                Resource.FrameObjects.Remove(holder.RefID);
            }
        }

        /// <summary>Puts it back (redo), holders first so the meshes have their parents again.</summary>
        public void Reattach()
        {
            foreach (FrameObjectBase holder in Holders)
            {
                if (!Resource.FrameObjects.ContainsKey(holder.RefID)) Resource.FrameObjects.Add(holder.RefID, holder);
            }
            foreach (FrameDuplicator.DuplicatedObject mesh in Meshes) mesh.Reattach();
            LinkLikeSources(Resource, Clones);
            Root.SetWorldTransform();
        }

        /// <summary>Source frame → its clone, for every node of the subtree.</summary>
        internal readonly Dictionary<FrameObjectBase, FrameObjectBase> Clones = new();

        /// <summary>The same pairs, for a caller checking that the copy kept its original's shape.</summary>
        public IReadOnlyDictionary<FrameObjectBase, FrameObjectBase> Pairs => Clones;
    }

    /// <summary>Whether this prototype is one the cloner can copy, with the reason when it is not. Asked
    /// before anything is created, so a refusal costs nothing and never leaves a half-copy behind.</summary>
    public static bool CanClone(FrameObjectBase root) => CanClone(root, out _);

    /// <inheritdoc cref="CanClone(FrameObjectBase)"/>
    public static bool CanClone(FrameObjectBase root, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(root);
        reason = null;
        return AllCopyable(root, new HashSet<FrameObjectBase>(), ref reason);
    }

    /// <summary>
    /// How many collision hulls the object carries — nothing is refused for it, but a copy of such an object
    /// has not been shown to work yet and the caller says so.
    ///
    /// A destructible gate with three hulls is the one copy that made the game refuse a district on load, and
    /// the files it produced were verified correct field by field, so the reason is not known. It is NOT
    /// "physics cannot be copied": the engine plainly instantiates destructible props many times over — a
    /// city_crash row places hundreds of copies of one prototype, and duplicating those works. Nor is it "an
    /// actor cannot be given a fresh object": copying a pinup produces a new object and works in the game,
    /// action included. What is still untried is a copy of an object whose subtree carries hulls but is
    /// SIMPLER than that gate — a door with one, the port platform with two.
    /// </summary>
    public static int HullsOf(FrameObjectBase root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var hulls = new List<FrameObjectCollision>();
        CollectCollisions(root, hulls, new HashSet<FrameObjectBase>());
        return hulls.Count;
    }

    // Every node of the subtree has to be a type the cloner reproduces — a skinned model brings a skeleton and
    // blend blocks that a mesh copy does not, so it is named rather than silently dropped.
    private static bool AllCopyable(FrameObjectBase frame, HashSet<FrameObjectBase> seen, ref string? reason)
    {
        if (!seen.Add(frame)) return true;
        // The same set CopyOf reproduces — kept in step with it deliberately, since a type this list allows and
        // that one does not would be discovered halfway through a clone.
        bool copyable = frame.GetType() == typeof(FrameObjectSingleMesh)
            || frame is FrameObjectFrame or FrameObjectCollision or FrameObjectDummy or FrameObjectArea
                or FrameObjectPoint;
        if (!copyable)
        {
            reason = $"'{frame.Name}' is a {frame.GetType().Name}, which cannot be copied yet";
            return false;
        }
        foreach (FrameObjectBase child in frame.Children)
        {
            if (!AllCopyable(child, seen, ref reason)) return false;
        }
        return true;
    }

    private static void CollectCollisions(FrameObjectBase frame, List<FrameObjectCollision> into,
        HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return;
        if (frame is FrameObjectCollision hull) into.Add(hull);
        foreach (FrameObjectBase child in frame.Children) CollectCollisions(child, into, seen);
    }

    /// <summary>
    /// Clones the subtree rooted at <paramref name="root"/>. Null with a reason when some part of it cannot be
    /// copied — a partial clone is never left behind, since an actor placing half an object is worse than an
    /// actor that was not copied.
    /// </summary>
    public static ClonedPrototype? TryClone(ISceneDocument document, FrameObjectBase root, out string? skipReason)
    {
        ArgumentNullException.ThrowIfNull(root);
        skipReason = null;
        if (document is not SceneDocumentAdapter adapter)
        {
            skipReason = "the object does not belong to a loaded scene";
            return null;
        }

        if (!CanClone(root, out skipReason)) return null;

        var clone = new ClonedPrototype { Resource = adapter.Frame };
        var renderables = new List<(FrameObjectSingleMesh, MeshData)>();

        FrameObjectBase? cloneRoot = CloneSubtree(document, adapter, root, isRoot: true, clone, renderables,
            new HashSet<FrameObjectBase>(), ref skipReason);
        if (cloneRoot == null)
        {
            clone.Detach();
            skipReason ??= "the object could not be copied";
            return null;
        }

        clone.Root = cloneRoot;
        clone.Renderables = renderables;
        clone.IsOnNameTable = clone.Clones.Values.Any(f => f.IsOnFrameTable);
        LinkLikeSources(adapter.Frame, clone.Clones);
        cloneRoot.SetWorldTransform();
        return clone;
    }

    /// <summary>
    /// Gives every clone the parents its source has, with links that pointed INSIDE the subtree redirected at
    /// the corresponding clone and links that pointed outside left exactly as they were.
    ///
    /// Reproducing the shape rather than inventing one matters more than it looks. A mesh carries a flag
    /// saying it is anchored through its second parent slot; clearing the slot while the flag stays set writes
    /// an anchor index of -1 into the file, and the game follows it on load. The shipped prototypes use
    /// several legal shapes, and the only safe rule is that a copy has the same one as its original.
    /// </summary>
    private static void LinkLikeSources(FrameResource resource,
        Dictionary<FrameObjectBase, FrameObjectBase> clones)
    {
        foreach (KeyValuePair<FrameObjectBase, FrameObjectBase> pair in clones)
        {
            FrameEntry? parent1 = Mapped(ResolveRef(resource, pair.Key, FrameEntryRefTypes.Parent1), clones);
            FrameEntry? parent2 = Mapped(ResolveRef(resource, pair.Key, FrameEntryRefTypes.Parent2), clones);

            // The ANCHOR slot goes first and the hierarchy slot second, and the order is the whole point.
            // Filling a slot detaches the link that slot owns, and the anchor only claims the node as a child
            // when nothing else parents it — so with the hierarchy slot written first, writing the anchor
            // afterwards REMOVES the node from that parent's child list and declines to put it back, for every
            // node whose two slots name the same parent (an animated platform's anim node does). The list is
            // what everything walks to find the geometry, so the copy became an object with nothing under it.
            // Writing the hierarchy slot last makes the pass idempotent, which it has to be: the copy is
            // re-linked once when it is made and again when it is re-applied.
            pair.Value.SetParent(ParentInfo.ParentType.ParentIndex2, parent2);
            pair.Value.SetParent(ParentInfo.ParentType.ParentIndex1, parent1);

            // A scene folder holds its members in a list of its own that SetParent does not touch — the same
            // rule the loader and the mesh duplicator follow.
            if (parent2 is FrameHeaderScene scene && pair.Value.Parent == null && !scene.Children.Contains(pair.Value))
            {
                scene.Children.Add(pair.Value);
            }
        }
    }

    private static FrameEntry? Mapped(FrameEntry? entry, Dictionary<FrameObjectBase, FrameObjectBase> clones) =>
        entry is FrameObjectBase frame && clones.TryGetValue(frame, out FrameObjectBase? clone) ? clone : entry;

    private static FrameEntry? ResolveRef(FrameResource resource, FrameObjectBase source, FrameEntryRefTypes slot)
    {
        if (!source.Refs.TryGetValue(slot, out int id)) return null;
        if (resource.FrameScenes.TryGetValue(id, out FrameHeaderScene? scene)) return scene;
        return resource.FrameObjects.TryGetValue(id, out object? obj) ? obj as FrameEntry : null;
    }

    // One node and everything under it. A mesh goes through the frame duplicator (which registers its own
    // blocks); every other type is copy-constructed and registered here. Parenting is deliberately NOT done
    // here — it needs every clone to exist first, so LinkLikeSources runs once at the end.
    private static FrameObjectBase? CloneSubtree(ISceneDocument document, SceneDocumentAdapter adapter,
        FrameObjectBase source, bool isRoot, ClonedPrototype into,
        List<(FrameObjectSingleMesh, MeshData)> renderables, HashSet<FrameObjectBase> seen, ref string? skipReason)
    {
        if (!seen.Add(source)) return null; // a malformed hierarchy can loop

        FrameObjectBase? copy;
        if (source.GetType() == typeof(FrameObjectSingleMesh))
        {
            FrameDuplicator.DuplicatedObject? duplicated =
                FrameDuplicator.TryDuplicate(document, adapter.Node(source), out skipReason);
            if (duplicated == null) return null;
            into.Meshes.Add(duplicated);

            copy = ((FrameNodeAdapter)duplicated.Node).Frame;
            renderables.Add(((FrameObjectSingleMesh)copy, duplicated.Mesh));
        }
        else
        {
            copy = CopyOf(source, adapter.Frame);
            if (copy == null)
            {
                skipReason = $"'{source.Name}' is a {source.GetType().Name}, which cannot be copied yet";
                return null;
            }
            into.Holders.Add(copy);
        }

        // Only the ROOT is renamed. It has to be: the actor's link is a hash of that name, and a copy sharing
        // it would resolve to the original. Everything under it keeps the name it had, because animations are
        // bound to an object's inner frames BY NAME — port ships 19_port_plosina-download.an2 for its platform
        // — and a renamed child is a child the animation cannot find. The shipped data settles that names need
        // not be unique: distillery repeats 'Dummy01' 233 times and 'lahev' 87, and uppertown's eight wanted
        // posters are eight differently-named roots over eight children all called 'wanted01_poster'. Which is
        // exactly the shape a copy should have.
        copy.Name = isRoot
            ? new Formats.Hashing.HashName(UniqueName(adapter.Frame, source.Name.String))
            : new Formats.Hashing.HashName(source.Name); // hash and string: some are named by hash alone
        into.Clones[source] = copy;

        foreach (FrameObjectBase child in source.Children.ToList())
        {
            if (CloneSubtree(document, adapter, child, isRoot: false, into, renderables, seen, ref skipReason)
                == null)
            {
                return null;
            }
        }
        return copy;
    }

    // A copy-constructed frame of the same type, named uniquely and registered. Only the types an actor's
    // prototype is actually built from are listed — anything else is refused by name rather than silently
    // dropped, so a prototype that carries one never turns into a half-copy.
    private static FrameObjectBase? CopyOf(FrameObjectBase source, FrameResource resource)
    {
        FrameObjectBase? copy = source switch
        {
            FrameObjectFrame frame => new FrameObjectFrame(frame),
            FrameObjectCollision collision => new FrameObjectCollision(collision),
            FrameObjectDummy dummy => new FrameObjectDummy(dummy),
            FrameObjectArea area => new FrameObjectArea(area),
            FrameObjectPoint point => new FrameObjectPoint(point),
            _ => null,
        };
        if (copy == null) return null;

        // Naming is the caller's business — only the subtree root gets a fresh name (see CloneSubtree).
        resource.FrameObjects.Add(copy.RefID, copy);
        return copy;
    }

    // Same rule the mesh duplicator uses: the frame name table is keyed by name, so no copy may collide.
    private static string UniqueName(FrameResource resource, string sourceName)
    {
        for (int i = 1; ; i++)
        {
            string candidate = i == 1 ? $"{sourceName}_copy" : $"{sourceName}_copy{i}";
            bool taken = resource.FrameObjects.Values.OfType<FrameObjectBase>()
                .Any(o => string.Equals(o.Name.String, candidate, StringComparison.OrdinalIgnoreCase));
            if (!taken) return candidate;
        }
    }

    /// <summary>
    /// Where a frame object sits in the resource's object list. This is the index a scene reference and the
    /// frame name table both store — the plain ordinal, NOT <c>GetIndexOfObject</c>, which offsets by the
    /// block count and belongs to the parent-index space instead.
    /// </summary>
    public static uint IndexOf(FrameResource resource, FrameObjectBase frame)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(frame);
        uint index = 0;
        foreach (int key in resource.FrameObjects.Keys)
        {
            if (key == frame.RefID) return index;
            index++;
        }
        return uint.MaxValue;
    }
}
