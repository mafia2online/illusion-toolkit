using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Bridge;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Frames;

/// <summary>
/// Copies an object out of ANOTHER archive's scene into this one — a door from a shop, a bench from an
/// interior — with everything of the scene that it is made of: the frames under it, their geometry and
/// material blocks, and the vertex and index buffers those name, under fresh names in this archive's pools.
///
/// <para>
/// It is <see cref="ActorPrototypeCloner"/> with the source and the destination in two different resources,
/// and it keeps that one's rule: a copy has the shape its original has. Links that point inside the subtree
/// are redirected at the corresponding copies; what the ROOT hangs off cannot come along, so the caller says
/// which of the two shipped shapes the root takes here (<see cref="Standing"/>).
/// </para>
/// <para>
/// What a scene does NOT hold stays behind and is the caller's to carry: the textures the materials name,
/// the item descriptions the collision frames name by hash, and the prefab entry an actor's definition names.
/// <see cref="TransplantedObject.MaterialHashes"/> and <see cref="TransplantedObject.CollisionHashes"/> say
/// which — see <see cref="Sds.ArchiveCarry"/>.
/// </para>
/// </summary>
public static class FrameTransplant
{
    /// <summary>How the copied root stands in the scene it arrives in.</summary>
    public enum Standing
    {
        /// <summary>
        /// A prototype an actor places: no parents at all, standing where it stood in its own archive (the
        /// origin, as a rule) — the actor supplies the world matrix. The shape every actor's object ships in.
        /// </summary>
        Prototype,

        /// <summary>
        /// Scenery: anchored to the district's main scene through the second parent slot and on the frame
        /// name table, with the given world matrix as its own. The shape every drawable district mesh ships in.
        /// </summary>
        Scenery,
    }

    /// <summary>A transplanted object with everything undo needs to take it out and put it back.</summary>
    public sealed class TransplantedObject
    {
        internal FrameResource Resource = null!;
        internal SceneDocumentAdapter Adapter = null!;
        internal readonly List<FrameObjectBase> Frames = new();              // root first, then in source order
        internal readonly List<FrameGeometry> Geometries = new();
        internal readonly List<FrameMaterial> Materials = new();
        internal readonly List<VertexBuffer> VertexBuffers = new();
        internal readonly List<IndexBuffer> IndexBuffers = new();
        internal readonly Dictionary<FrameObjectBase, FrameObjectBase> Copies = new();   // source → copy
        internal FrameHeaderScene? Anchor;

        /// <summary>The frame of the receiving scene the copy's root was hung under, when it was asked to be
        /// someone's child; null for a root that stands in the scene by itself.</summary>
        public FrameObjectBase? Under { get; internal set; }

        /// <summary>The copy's root — what an actor places, or the scenery object itself.</summary>
        public FrameObjectBase Root { get; internal set; } = null!;

        /// <summary>The copied meshes, each with a render-ready copy for the caller's GPU upload.</summary>
        public IReadOnlyList<(FrameObjectSingleMesh Frame, MeshData Mesh)> Renderables { get; internal set; } = [];

        /// <summary>Source frame → its copy, for every node of the subtree.</summary>
        public IReadOnlyDictionary<FrameObjectBase, FrameObjectBase> Pairs => Copies;

        /// <summary>Where <see cref="Root"/> sits in the frame resource's object list — the index a scene
        /// reference stores. Read it fresh: an undone delete can reorder the list.</summary>
        public uint FrameIndex => ActorPrototypeCloner.IndexOf(Resource, Root);

        /// <summary>Whether the copy is currently part of the scene (false once <see cref="Detach"/> ran).</summary>
        public bool IsAttached => Resource.FrameObjects.ContainsKey(Root.RefID);

        /// <summary>Whether any copied frame is on the frame name table — the caller must mark the table
        /// dirty, or the copy is an object the game's spawn list never mentions.</summary>
        public bool IsOnNameTable => Frames.Any(f => f.IsOnFrameTable);

        /// <summary>Every material the copied meshes wear, by hash — what their textures are found through.</summary>
        public IReadOnlyCollection<ulong> MaterialHashes
        {
            get
            {
                var hashes = new HashSet<ulong>();
                foreach (FrameMaterial block in Materials)
                {
                    foreach (MaterialStruct[] lod in block.Materials)
                    {
                        foreach (MaterialStruct slot in lod) hashes.Add(slot.MaterialHash);
                    }
                }
                return hashes;
            }
        }

        /// <summary>The textures the copied meshes name themselves, not through a material: the occlusion
        /// map (<c>OMTextureHash</c>). A copy keeps the name, so the file has to be in its new archive too.</summary>
        public IReadOnlyCollection<string> DirectTextures =>
            Frames.OfType<FrameObjectSingleMesh>().Select(m => m.OMTextureHash)
                .Where(om => om is { Hash: not 0, String.Length: > 0 }).Select(om => om.String)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>The item descriptions the copied collision frames name, by hash.</summary>
        public IReadOnlyCollection<ulong> CollisionHashes =>
            Frames.OfType<FrameObjectCollision>().Select(c => c.Hash).Where(h => h != 0).ToHashSet();

        /// <summary>Takes the copy back out of the frame resource (undo).</summary>
        public void Detach()
        {
            foreach (FrameObjectBase frame in Frames)
            {
                frame.SetParent(ParentInfo.ParentType.ParentIndex1, null);
                frame.SetParent(ParentInfo.ParentType.ParentIndex2, null);
                foreach (FrameHeaderScene scene in Resource.FrameScenes.Values) scene.Children.Remove(frame);
                Resource.FrameObjects.Remove(frame.RefID);
            }
            foreach (FrameGeometry geometry in Geometries) Resource.FrameGeometries.Remove(geometry.RefID);
            foreach (FrameMaterial material in Materials) Resource.FrameMaterials.Remove(material.RefID);
            foreach (VertexBuffer vb in VertexBuffers) Resource.VertexBuffers.Remove(vb.Hash);
            foreach (IndexBuffer ib in IndexBuffers) Resource.IndexBuffers.Remove(ib.Hash);
        }

        /// <summary>Puts it back (redo). Re-registers blocks a save-time sanitize may have pruned while it
        /// was detached.</summary>
        public void Reattach()
        {
            foreach (FrameObjectBase frame in Frames)
            {
                if (!Resource.FrameObjects.ContainsKey(frame.RefID)) Resource.FrameObjects.Add(frame.RefID, frame);
            }
            foreach (FrameGeometry geometry in Geometries)
            {
                if (!Resource.FrameGeometries.ContainsKey(geometry.RefID))
                    Resource.FrameGeometries.Add(geometry.RefID, geometry);
            }
            foreach (FrameMaterial material in Materials)
            {
                if (!Resource.FrameMaterials.ContainsKey(material.RefID))
                    Resource.FrameMaterials.Add(material.RefID, material);
            }
            // And the blocks its meshes point at without owning them. A second import of the same object
            // draws from the first one's geometry block (CopyBlocks) and lists none of its own; with the
            // first copy deleted and this one undone, a save prunes the block as unused — and a redo that
            // put back only what is listed above left a mesh naming a block the resource no longer had,
            // which the next save writes as a mesh index pointing nowhere.
            foreach (FrameObjectSingleMesh mesh in Frames.OfType<FrameObjectSingleMesh>())
            {
                if (mesh.Refs.ContainsKey(FrameEntryRefTypes.Geometry) && mesh.Geometry is { } drawn
                    && !Resource.FrameGeometries.ContainsKey(drawn.RefID))
                {
                    Resource.FrameGeometries.Add(drawn.RefID, drawn);
                }
                if (mesh.Refs.ContainsKey(FrameEntryRefTypes.Material) && mesh.Material is { } worn
                    && !Resource.FrameMaterials.ContainsKey(worn.RefID))
                {
                    Resource.FrameMaterials.Add(worn.RefID, worn);
                }
            }
            foreach (VertexBuffer vb in VertexBuffers)
            {
                Resource.VertexBuffers.TryAddToPool(vb);
                Adapter.MarkVertexBufferDirty(vb.Hash);
            }
            foreach (IndexBuffer ib in IndexBuffers)
            {
                Resource.IndexBuffers.TryAddToPool(ib);
                Adapter.MarkIndexBufferDirty(ib.Hash);
            }
            Link(this);
            Root.SetWorldTransform();
        }

        // What each copy hangs off: recorded once, when the copy is made, because the source resource is
        // not kept — a redo has nothing but this to re-link from.
        internal readonly Dictionary<FrameObjectBase, (FrameEntry? Parent1, FrameEntry? Parent2)> Parents = new();
    }

    /// <summary>Whether the subtree under <paramref name="root"/> is one this can copy, with the reason when
    /// it is not. The same set of frame types the in-archive cloner reproduces.</summary>
    public static bool CanTransplant(FrameObjectBase root, out string? reason) =>
        ActorPrototypeCloner.CanClone(root, out reason);

    /// <summary>
    /// Copies the subtree rooted at <paramref name="root"/> — an object of <paramref name="source"/> — into
    /// <paramref name="document"/>'s scene. Null with a reason when some part of it cannot be copied; a
    /// partial copy is never left behind.
    /// </summary>
    /// <param name="name">The copy's root name. It has to be free in the destination: an actor's link is a
    /// hash of it, and the frame name table is keyed by it.</param>
    /// <param name="world">The root's world matrix, for <see cref="Standing.Scenery"/>. Ignored for a
    /// prototype, which keeps the transform it has.</param>
    public static TransplantedObject? TryTransplant(ISceneDocument document, FrameResource source,
        FrameObjectBase root, string name, Standing standing, Matrix4x4 world, out string? skipReason) =>
        TryTransplant(document, source, root, name, standing, world, shared: null, out skipReason);

    /// <summary>
    /// The same, drawing from geometry an earlier import of the same object already brought when
    /// <paramref name="shared"/> remembers it and the scene still has it — the buffers and, when every level of
    /// detail is there, the geometry block itself, which is how the shipped scenes put many copies of one thing
    /// on one mesh. What is copied afresh is remembered in <paramref name="shared"/> for the next time; the caller
    /// saves it.
    /// </summary>
    public static TransplantedObject? TryTransplant(ISceneDocument document, FrameResource source,
        FrameObjectBase root, string name, Standing standing, Matrix4x4 world, ImportGeometry? shared,
        out string? skipReason) =>
        TryTransplant(document, source, root, name, standing, world, shared, under: null, out skipReason);

    /// <summary>
    /// The same, with the copy's root hung under <paramref name="under"/> — a frame of the receiving scene —
    /// instead of standing in the scene by itself: the shape an interior's furniture ships in, every piece a child
    /// of the frame that carries the interior to its place. <paramref name="world"/> is still where the copy
    /// stands in the world; its own matrix is worked out against the parent's. Scenery only.
    /// </summary>
    public static TransplantedObject? TryTransplant(ISceneDocument document, FrameResource source,
        FrameObjectBase root, string name, Standing standing, Matrix4x4 world, ImportGeometry? shared,
        FrameObjectBase? under, out string? skipReason)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(root);
        skipReason = null;
        if (document is not SceneDocumentAdapter adapter)
        {
            skipReason = "the destination is not a loaded scene";
            return null;
        }
        FrameResource resource = adapter.Frame;
        if (ReferenceEquals(resource, source))
        {
            skipReason = "the object is already in this scene — duplicate it instead";
            return null;
        }
        if (!CanTransplant(root, out skipReason)) return null;
        Matrix4x4 underInverse = Matrix4x4.Identity;
        if (under != null)
        {
            if (standing != Standing.Scenery)
            {
                skipReason = "only scenery can be hung under a frame — an actor places its own object";
                return null;
            }
            if (!resource.FrameObjects.TryGetValue(under.RefID, out object? held) || !ReferenceEquals(held, under))
            {
                skipReason = "the parent is not a frame of the receiving scene";
                return null;
            }
            if (!Matrix4x4.Invert(under.WorldTransform, out underInverse))
            {
                skipReason = $"'{under.Name}' has a matrix that cannot be undone — nothing can be placed under it";
                return null;
            }
        }
        if (string.IsNullOrWhiteSpace(name) || resource.FrameObjects.Values.OfType<FrameObjectBase>()
                .Any(o => string.Equals(o.Name.String, name, StringComparison.OrdinalIgnoreCase)))
        {
            skipReason = $"the name '{name}' is empty or already taken in this scene";
            return null;
        }

        // A prototype keeps the shape it has: nearly all hang off nothing, and the few that are anchored to
        // their archive's scene are anchored to this one's.
        bool anchored = standing == Standing.Scenery
            || (root.Refs.TryGetValue(FrameEntryRefTypes.Parent2, out int rootAnchor) && source.FrameScenes.ContainsKey(rootAnchor));
        FrameHeaderScene? anchor = anchored ? BridgeObjectFactory.PickMainScene(resource) : null;
        if (anchored && anchor == null)
        {
            skipReason = "this archive has no scene folder to anchor the object to";
            return null;
        }

        var subtree = new List<FrameObjectBase>();
        Collect(root, subtree, new HashSet<FrameObjectBase>());

        var result = new TransplantedObject { Resource = resource, Adapter = adapter, Anchor = anchor, Under = under };
        // What a child of `under` records in its second slot: the top of the parent's chain hangs off it — an
        // object, or the scene folder that holds that top; nothing when the chain ends at a rootless frame.
        FrameEntry? underAnchor = null;
        if (under != null)
        {
            FrameObjectBase top = under;
            var climbed = new HashSet<FrameObjectBase> { top };
            while (top.Parent is { } next && climbed.Add(next)) top = next;
            underAnchor = top.Root as FrameEntry ?? resource.FrameScenes.Values.FirstOrDefault(folder => folder.Children.Contains(top));
        }
        string unique = Guid.NewGuid().ToString("N")[..8];
        var reused = new HashSet<ulong>(); // source buffers this copy draws from without copying them
        if (!CopyBuffers(source, resource, subtree, name, unique, result, shared, reused,
                out Dictionary<ulong, HashName> vertexNames, out Dictionary<ulong, HashName> indexNames, out skipReason))
        {
            foreach (VertexBuffer vb in result.VertexBuffers) resource.VertexBuffers.Remove(vb.Hash);
            foreach (IndexBuffer ib in result.IndexBuffers) resource.IndexBuffers.Remove(ib.Hash);
            return null;
        }

        // Blocks are shared in the shipped scenes — several frames on one geometry, on one material list —
        // and a copy keeps that: each source block is copied once and every frame that used it uses the copy.
        var geometries = new Dictionary<FrameGeometry, FrameGeometry>();
        var materials = new Dictionary<FrameMaterial, FrameMaterial>();

        foreach (FrameObjectBase original in subtree)
        {
            FrameObjectBase? copy = CopyOf(original);
            if (copy == null) // unreachable after CanTransplant; a half-copy is still never left behind
            {
                result.Detach();
                skipReason = $"'{original.Name}' is a {original.GetType().Name}, which cannot be copied yet";
                return null;
            }
            copy.MoveTo(resource);
            // The ids a copy inherits name parents and blocks of the OTHER resource. Dropped here; the
            // parents are written back by Link once every copy exists.
            copy.SetParent(ParentInfo.ParentType.ParentIndex1, null);
            copy.SetParent(ParentInfo.ParentType.ParentIndex2, null);
            // Only the root is renamed — an animation binds an object's inner frames by name (see
            // ActorPrototypeCloner), and names under a root need not be unique.
            // Hash and string both, since a frame can be named by hash alone (a weapon's parts are).
            copy.Name = ReferenceEquals(original, root) ? new HashName(name) : new HashName(original.Name);

            if (copy is FrameObjectSingleMesh mesh && original is FrameObjectSingleMesh originalMesh)
            {
                CopyBlocks(resource, originalMesh, mesh, geometries, materials, vertexNames, indexNames, reused, result);
            }

            resource.FrameObjects.Add(copy.RefID, copy);
            result.Frames.Add(copy);
            result.Copies[original] = copy;
        }

        result.Root = result.Copies[root];
        foreach (FrameObjectBase original in subtree)
        {
            FrameObjectBase copy = result.Copies[original];
            if (ReferenceEquals(original, root))
            {
                // Under a frame: the parent in the first slot and, in the second, what the parent's own chain
                // hangs off — the way a shipped interior's pieces name both the frame that carries them and
                // the scene that frame stands in.
                result.Parents[copy] = under != null ? (under, underAnchor) : (null, anchor);
                continue;
            }
            // Inside the subtree a link follows its target's copy. A second-slot link that pointed OUTSIDE it
            // named what the whole chain hangs off — the scene, or the top of the chain — and that is now
            // whatever the root hangs off, or the root itself when it hangs off nothing.
            FrameEntry? parent1 = Mapped(original.Parent, result.Copies);
            FrameEntry? parent2 = original.Refs.ContainsKey(FrameEntryRefTypes.Parent2)
                ? Mapped(original.Root, result.Copies) ?? (under != null ? underAnchor : anchor) ?? result.Root
                : null;
            result.Parents[copy] = (parent1, parent2);
        }

        if (under != null)
        {
            // A child is found through its parent: not on the spawn list, and a matrix of its own that is told
            // against the parent's. The anchored-mesh bit as the shipped files have it on a child: set when the
            // second slot names an object (a door's leaf under its frame), clear when it names the scene (an
            // interior's furniture under its holder) or nothing.
            result.Root.IsOnFrameTable = false;
            if (result.Root is FrameObjectSingleMesh childMesh)
            {
                childMesh.SingleMeshFlags = underAnchor is FrameObjectBase
                    ? childMesh.SingleMeshFlags | SingleMeshFlags.ParentIndex2_Flag
                    : childMesh.SingleMeshFlags & ~SingleMeshFlags.ParentIndex2_Flag;
            }
            result.Root.LocalTransform = world * underInverse;
        }
        else if (standing == Standing.Scenery)
        {
            // The stock drawable shape, as the bridge's object factory builds it: anchored through the
            // second slot, on the spawn list, normal-season flags, and the anchored-mesh bit a mesh in that
            // slot carries — clearing or setting the slot without the bit writes an anchor the game follows
            // to nowhere.
            result.Root.IsOnFrameTable = true;
            result.Root.FrameNameTableFlags = 0;
            if (result.Root is FrameObjectSingleMesh rootMesh) rootMesh.SingleMeshFlags |= SingleMeshFlags.ParentIndex2_Flag;
            result.Root.LocalTransform = world;
        }
        else if (anchor == null && result.Root is FrameObjectSingleMesh bareMesh)
        {
            bareMesh.SingleMeshFlags &= ~SingleMeshFlags.ParentIndex2_Flag;
        }

        Link(result);
        result.Root.SetWorldTransform();

        var renderables = new List<(FrameObjectSingleMesh, MeshData)>();
        foreach (FrameObjectSingleMesh mesh in result.Frames.OfType<FrameObjectSingleMesh>())
        {
            if (!mesh.Refs.ContainsKey(FrameEntryRefTypes.Geometry)) continue;
            MeshData? data = SdsMeshLoader.TryConvert(mesh);
            if (data == null)
            {
                result.Detach();
                skipReason = $"'{mesh.Name}' could not be decoded for display";
                return null;
            }
            renderables.Add((mesh, data));
        }
        result.Renderables = renderables;

        foreach (VertexBuffer vb in result.VertexBuffers) adapter.MarkVertexBufferDirty(vb.Hash);
        foreach (IndexBuffer ib in result.IndexBuffers) adapter.MarkIndexBufferDirty(ib.Hash);
        return result;
    }

    /// <summary>
    /// Where the object stands, in its root's own space: the middle of the bottom of everything it draws.
    /// <para>
    /// A piece of scenery's origin says nothing about where its geometry is — a district mesh is pivoted
    /// wherever its author left it, often in its middle or metres away — so "put it here" has to mean "stand
    /// it here". An actor's prototype needs none of this: its origin is the point the actor places, and the
    /// shipped ones stand on it. Null when nothing in the subtree decodes.
    /// </para>
    /// </summary>
    public static Vector3? BaseOf(FrameObjectBase root) =>
        BoundsOf(root) is { } bounds
            ? new Vector3((bounds.Min.X + bounds.Max.X) / 2f, (bounds.Min.Y + bounds.Max.Y) / 2f, bounds.Min.Z)
            : null;

    /// <summary>The box everything the subtree draws fits in, in its root's own space. Null when nothing decodes.</summary>
    public static (Vector3 Min, Vector3 Max)? BoundsOf(FrameObjectBase root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!Matrix4x4.Invert(root.WorldTransform, out Matrix4x4 toRoot)) return null;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach ((Vector3[] positions, _) in TrianglesOf(root))
        {
            foreach (Vector3 position in positions)
            {
                Vector3 p = Vector3.Transform(position, toRoot);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        return min.X > max.X ? null : (min, max);
    }

    /// <summary>Every triangle the subtree draws at its finest level, in WORLD space — what a hull is cooked from.</summary>
    public static IReadOnlyList<(Vector3[] Positions, uint[] Indices)> TrianglesOf(FrameObjectBase root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var subtree = new List<FrameObjectBase>();
        Collect(root, subtree, new HashSet<FrameObjectBase>());
        var meshes = new List<(Vector3[], uint[])>();
        foreach (FrameObjectSingleMesh mesh in subtree.OfType<FrameObjectSingleMesh>())
        {
            if (!mesh.Refs.ContainsKey(FrameEntryRefTypes.Geometry)) continue;
            if (SdsMeshLoader.DecodeLod0(mesh) is not { } decoded) continue;
            Matrix4x4 world = mesh.WorldTransform;
            meshes.Add((decoded.Positions.Select(p => Vector3.Transform(p, world)).ToArray(), decoded.Indices));
        }
        return meshes;
    }

    private static void Collect(FrameObjectBase frame, List<FrameObjectBase> into, HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return; // a malformed hierarchy can loop
        into.Add(frame);
        foreach (FrameObjectBase child in frame.Children) Collect(child, into, seen);
    }

    // Verbatim copies of every buffer the subtree's meshes draw from, under fresh names, registered before
    // anything else mutates — the pools must have room first. A buffer several meshes share is copied once.
    private static bool CopyBuffers(FrameResource source, FrameResource resource, List<FrameObjectBase> subtree,
        string name, string unique, TransplantedObject into, ImportGeometry? shared, HashSet<ulong> reused,
        out Dictionary<ulong, HashName> vertexNames, out Dictionary<ulong, HashName> indexNames, out string? reason)
    {
        vertexNames = new Dictionary<ulong, HashName>();
        indexNames = new Dictionary<ulong, HashName>();
        reason = null;
        foreach (FrameObjectSingleMesh mesh in subtree.OfType<FrameObjectSingleMesh>())
        {
            if (!mesh.Refs.ContainsKey(FrameEntryRefTypes.Geometry)) continue;
            foreach (FrameLOD lod in mesh.Geometry.LOD ?? [])
            {
                ulong vertexHash = lod.VertexBufferRef.Hash, indexHash = lod.IndexBufferRef.Hash;
                VertexBuffer? vb = source.VertexBuffers.GetBuffer(vertexHash);
                IndexBuffer? ib = source.IndexBuffers.GetBuffer(indexHash);
                if (vb == null || ib == null)
                {
                    reason = $"'{mesh.Name}' draws from a buffer its own archive does not carry";
                    return false;
                }
                if (!vertexNames.ContainsKey(vertexHash)
                    && shared?.Copied('v', vertexHash) is { } earlierVertex
                    && resource.VertexBuffers.GetBuffer(new HashName(earlierVertex).Hash) is { } existingVertex
                    && existingVertex.Data.AsSpan().SequenceEqual(vb.Data))
                {
                    vertexNames[vertexHash] = new HashName(earlierVertex);
                    reused.Add(vertexHash);
                }
                if (!vertexNames.ContainsKey(vertexHash))
                {
                    var bufferName = new HashName($"{name}_vb{vertexHash:x16}_{unique}");
                    var copy = new VertexBuffer(bufferName.Hash) { Data = (byte[])vb.Data.Clone() };
                    if (!resource.VertexBuffers.TryAddToPool(copy))
                    {
                        reason = "this archive has no vertex buffer pool to copy the geometry into";
                        return false;
                    }
                    vertexNames[vertexHash] = bufferName;
                    into.VertexBuffers.Add(copy);
                    shared?.Remember('v', vertexHash, bufferName.String);
                }
                if (!indexNames.ContainsKey(indexHash)
                    && shared?.Copied('i', indexHash) is { } earlierIndex
                    && resource.IndexBuffers.GetBuffer(new HashName(earlierIndex).Hash) is { } existingIndex
                    && existingIndex.GetData().AsSpan().SequenceEqual(ib.GetData()))
                {
                    indexNames[indexHash] = new HashName(earlierIndex);
                    reused.Add(indexHash);
                }
                if (!indexNames.ContainsKey(indexHash))
                {
                    var bufferName = new HashName($"{name}_ib{indexHash:x16}_{unique}");
                    var copy = new IndexBuffer(bufferName.Hash);
                    copy.SetFormat(ib.IndexFormat);
                    copy.SetData((uint[])ib.GetData().Clone());
                    if (!resource.IndexBuffers.TryAddToPool(copy))
                    {
                        reason = "this archive has no index buffer pool to copy the geometry into";
                        return false;
                    }
                    indexNames[indexHash] = bufferName;
                    into.IndexBuffers.Add(copy);
                    shared?.Remember('i', indexHash, bufferName.String);
                }
            }
        }
        return true;
    }

    // Gives a copied mesh blocks of its own in the destination — deep copies of the source's, each LOD
    // repointed at the copied buffers.
    private static void CopyBlocks(FrameResource resource, FrameObjectSingleMesh original, FrameObjectSingleMesh copy,
        Dictionary<FrameGeometry, FrameGeometry> geometries, Dictionary<FrameMaterial, FrameMaterial> materials,
        Dictionary<ulong, HashName> vertexNames, Dictionary<ulong, HashName> indexNames, HashSet<ulong> reused,
        TransplantedObject into)
    {
        if (original.Refs.ContainsKey(FrameEntryRefTypes.Geometry))
        {
            // Every level already here: the block that draws them is here too — share it, as the shipped scenes
            // share one chair's block between all their chairs. It is not this copy's to take out on undo.
            if (!geometries.ContainsKey(original.Geometry)
                && original.Geometry.LOD.All(l => reused.Contains(l.VertexBufferRef.Hash) && reused.Contains(l.IndexBufferRef.Hash))
                && resource.FrameGeometries.Values.FirstOrDefault(g => Draws(g, original.Geometry, vertexNames, indexNames)) is { } existing)
            {
                geometries[original.Geometry] = existing;
            }
            if (!geometries.TryGetValue(original.Geometry, out FrameGeometry? geometry))
            {
                geometry = resource.ConstructFrameAssetOfType<FrameGeometry>();
                geometry.CopyFrom(original.Geometry);
                foreach (FrameLOD lod in geometry.LOD)
                {
                    lod.VertexBufferRef = vertexNames[lod.VertexBufferRef.Hash];
                    lod.IndexBufferRef = indexNames[lod.IndexBufferRef.Hash];
                }
                geometries[original.Geometry] = geometry;
                into.Geometries.Add(geometry);
            }
            copy.Geometry = geometry;
            copy.ReplaceRef(FrameEntryRefTypes.Geometry, geometry.RefID);
        }

        if (original.Refs.ContainsKey(FrameEntryRefTypes.Material))
        {
            if (!materials.TryGetValue(original.Material, out FrameMaterial? material))
            {
                // The copy constructor deep-copies the ranges and shares LodMatCount — give the copy its own.
                material = new FrameMaterial(original.Material) { LodMatCount = original.Material.LodMatCount.ToArray() };
                material.MoveTo(resource);
                resource.FrameMaterials.Add(material.RefID, material);
                materials[original.Material] = material;
                into.Materials.Add(material);
            }
            copy.Material = material;
            copy.ReplaceRef(FrameEntryRefTypes.Material, material.RefID);
        }
    }

    // Whether a block of this scene draws exactly what the source block does, from the buffers it was copied to.
    // "Exactly" is the whole block: a copy made earlier is the user's to edit, and one whose draw distance was
    // set to 0 since — or whose level was rebuilt — is no longer the source's block. Sharing it gave the next
    // import that edit too: an object that does not draw. The buffers are still shared; the block is copied.
    private static bool Draws(FrameGeometry candidate, FrameGeometry original, Dictionary<ulong, HashName> vertexNames,
        Dictionary<ulong, HashName> indexNames)
    {
        if (candidate.LOD is not { } lods || lods.Length != original.LOD.Length) return false;
        if (candidate.NumLods != original.NumLods || candidate.Unk01 != original.Unk01) return false;
        if (candidate.DecompressionOffset != original.DecompressionOffset || candidate.DecompressionFactor != original.DecompressionFactor)
        {
            return false;
        }
        for (int i = 0; i < lods.Length; i++)
        {
            if (lods[i].VertexBufferRef.Hash != vertexNames[original.LOD[i].VertexBufferRef.Hash].Hash
                || lods[i].IndexBufferRef.Hash != indexNames[original.LOD[i].IndexBufferRef.Hash].Hash
                || !lods[i].DrawsLike(original.LOD[i]))
            {
                return false;
            }
        }
        return true;
    }

    // Kept in step with ActorPrototypeCloner.CanClone, which is what decided the subtree could be copied.
    private static FrameObjectBase? CopyOf(FrameObjectBase source) => source switch
    {
        FrameObjectSingleMesh mesh when mesh.GetType() == typeof(FrameObjectSingleMesh) => new FrameObjectSingleMesh(mesh),
        FrameObjectFrame frame => new FrameObjectFrame(frame),
        FrameObjectCollision collision => new FrameObjectCollision(collision),
        FrameObjectDummy dummy => new FrameObjectDummy(dummy),
        FrameObjectArea area => new FrameObjectArea(area),
        FrameObjectPoint point => new FrameObjectPoint(point),
        _ => null,
    };

    private static FrameEntry? Mapped(FrameObjectBase? original, Dictionary<FrameObjectBase, FrameObjectBase> copies) =>
        original != null && copies.TryGetValue(original, out FrameObjectBase? copy) ? copy : null;

    // Writes every copy's two parent slots the way the loader resolves them: both cleared, then the hierarchy
    // slot, then the anchor. The order is what keeps the child lists right. An anchor claims the node as a
    // child only when nothing else parents it, so written FIRST on a fresh node it claims one it should not —
    // the node then sits under its anchor and under its parent both; and written over a link already there it
    // takes the node out of the list its parent shares with it (an animated node's two slots often name one
    // frame) and does not put it back. Clearing first makes neither possible, and makes the pass idempotent,
    // which it has to be: it runs when the copy is made and again when a redo puts it back.
    private static void Link(TransplantedObject copy)
    {
        foreach (FrameObjectBase frame in copy.Frames)
        {
            (FrameEntry? parent1, FrameEntry? parent2) = copy.Parents[frame];
            frame.SetParent(ParentInfo.ParentType.ParentIndex1, null);
            frame.SetParent(ParentInfo.ParentType.ParentIndex2, null);
            frame.SetParent(ParentInfo.ParentType.ParentIndex1, parent1);
            frame.SetParent(ParentInfo.ParentType.ParentIndex2, parent2);
            // A scene folder holds its members in a list of its own that SetParent does not touch.
            if (parent2 is FrameHeaderScene scene && frame.Parent == null && !scene.Children.Contains(frame))
            {
                scene.Children.Add(frame);
            }
        }
    }
}
