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
        FrameObjectBase root, string name, Standing standing, Matrix4x4 world, out string? skipReason)
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

        var result = new TransplantedObject { Resource = resource, Adapter = adapter, Anchor = anchor };
        string unique = Guid.NewGuid().ToString("N")[..8];
        if (!CopyBuffers(source, resource, subtree, name, unique, result,
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
                CopyBlocks(resource, originalMesh, mesh, geometries, materials, vertexNames, indexNames, result);
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
                result.Parents[copy] = (null, anchor);
                continue;
            }
            // Inside the subtree a link follows its target's copy. A second-slot link that pointed OUTSIDE it
            // named what the whole chain hangs off — the scene, or the top of the chain — and that is now
            // whatever the root hangs off, or the root itself when it hangs off nothing.
            FrameEntry? parent1 = Mapped(original.Parent, result.Copies);
            FrameEntry? parent2 = original.Refs.ContainsKey(FrameEntryRefTypes.Parent2)
                ? Mapped(original.Root, result.Copies) ?? (FrameEntry?)anchor ?? result.Root
                : null;
            result.Parents[copy] = (parent1, parent2);
        }

        if (standing == Standing.Scenery)
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
    public static Vector3? BaseOf(FrameObjectBase root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!Matrix4x4.Invert(root.WorldTransform, out Matrix4x4 toRoot)) return null;
        var subtree = new List<FrameObjectBase>();
        Collect(root, subtree, new HashSet<FrameObjectBase>());

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (FrameObjectSingleMesh mesh in subtree.OfType<FrameObjectSingleMesh>())
        {
            if (!mesh.Refs.ContainsKey(FrameEntryRefTypes.Geometry)) continue;
            if (SdsMeshLoader.DecodeLod0(mesh) is not { } decoded) continue;
            Matrix4x4 toRootSpace = mesh.WorldTransform * toRoot;
            foreach (Vector3 position in decoded.Positions)
            {
                Vector3 p = Vector3.Transform(position, toRootSpace);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        return min.X > max.X ? null : new Vector3((min.X + max.X) / 2f, (min.Y + max.Y) / 2f, min.Z);
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
        string name, string unique, TransplantedObject into,
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
                }
            }
        }
        return true;
    }

    // Gives a copied mesh blocks of its own in the destination — deep copies of the source's, each LOD
    // repointed at the copied buffers.
    private static void CopyBlocks(FrameResource resource, FrameObjectSingleMesh original, FrameObjectSingleMesh copy,
        Dictionary<FrameGeometry, FrameGeometry> geometries, Dictionary<FrameMaterial, FrameMaterial> materials,
        Dictionary<ulong, HashName> vertexNames, Dictionary<ulong, HashName> indexNames, TransplantedObject into)
    {
        if (original.Refs.ContainsKey(FrameEntryRefTypes.Geometry))
        {
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
