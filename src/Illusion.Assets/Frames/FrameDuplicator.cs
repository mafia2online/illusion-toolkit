using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Frames;

/// <summary>
/// In-app duplication of a static mesh object: a byte-faithful deep copy — cloned frame, cloned
/// geometry/material blocks, verbatim buffer copies under fresh FNV64 names — parented exactly like the
/// source and left at the source's transform. Deep on purpose: sharing blocks or buffers would make a later
/// geometry edit of one copy silently reshape the other. v1 scope is the exact
/// <see cref="FrameObjectSingleMesh"/> type (the same restriction the Blender applier has); Model and the
/// other frame types are refused with a reason.
/// </summary>
public static class FrameDuplicator
{
    /// <summary>A duplicate with everything undo needs to take it out and put it back.</summary>
    public sealed class DuplicatedObject
    {
        internal FrameResource Resource = null!;
        internal SceneDocumentAdapter Adapter = null!;
        internal FrameObjectSingleMesh Clone = null!;
        internal FrameGeometry Geometry = null!;
        internal FrameMaterial Material = null!;
        internal List<VertexBuffer> VertexBuffers = new();
        internal List<IndexBuffer> IndexBuffers = new();
        internal FrameEntry? Parent1;
        internal FrameEntry? Parent2;

        /// <summary>The editor-facing node adapter (canonical for the document).</summary>
        public IFrameNode Node { get; internal set; } = null!;

        /// <summary>Render-ready copy of the duplicated mesh (LOD0) for the caller's GPU upload.</summary>
        public MeshData Mesh { get; internal set; } = null!;

        /// <summary>Whether the copy inherited frame-name-table membership — the caller must mark the
        /// name table dirty so the new entry is written on save.</summary>
        public bool IsOnNameTable => Clone.IsOnFrameTable;

        /// <summary>Removes the duplicate from the frame data (undo).</summary>
        public void Detach()
        {
            Clone.SetParent(ParentInfo.ParentType.ParentIndex1, null);
            Clone.SetParent(ParentInfo.ParentType.ParentIndex2, null);
            foreach (FrameHeaderScene scene in Resource.FrameScenes.Values) scene.Children.Remove(Clone);
            Resource.FrameObjects.Remove(Clone.RefID);
            Resource.FrameGeometries.Remove(Geometry.RefID);
            Resource.FrameMaterials.Remove(Material.RefID);
            foreach (VertexBuffer vb in VertexBuffers) Resource.VertexBuffers.Remove(vb.Hash);
            foreach (IndexBuffer ib in IndexBuffers) Resource.IndexBuffers.Remove(ib.Hash);
        }

        /// <summary>Puts the duplicate back (redo). Re-registers blocks a save-time sanitize may have
        /// pruned while it was detached.</summary>
        public void Reattach()
        {
            if (!Resource.FrameObjects.ContainsKey(Clone.RefID)) Resource.FrameObjects.Add(Clone.RefID, Clone);
            if (!Resource.FrameGeometries.ContainsKey(Geometry.RefID))
                Resource.FrameGeometries.Add(Geometry.RefID, Geometry);
            if (!Resource.FrameMaterials.ContainsKey(Material.RefID))
                Resource.FrameMaterials.Add(Material.RefID, Material);
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
            LinkParents(Clone, Parent1, Parent2);
            Clone.SetWorldTransform();
        }
    }

    /// <summary>Whether <paramref name="node"/> is something this duplicator can copy.</summary>
    public static bool CanDuplicate(IFrameNode node) =>
        node is FrameNodeAdapter adapter && adapter.Frame.GetType() == typeof(FrameObjectSingleMesh);

    /// <summary>Duplicates the node's frame object. Null with a reason when it cannot be copied
    /// (unsupported type, missing buffers, full pools).</summary>
    public static DuplicatedObject? TryDuplicate(ISceneDocument document, IFrameNode node, out string? skipReason)
    {
        skipReason = null;
        if (document is not SceneDocumentAdapter adapter
            || node is not FrameNodeAdapter sourceNode
            || sourceNode.Frame is not FrameObjectSingleMesh source
            || source.GetType() != typeof(FrameObjectSingleMesh))
        {
            skipReason = "duplicate supports static meshes for now";
            return null;
        }

        FrameResource resource = adapter.Frame;
        if (source.Geometry.LOD is not { Length: > 0 }
            || source.GetVertexBuffer(0) == null || source.GetIndexBuffer(0) == null)
        {
            skipReason = "mesh has no usable LOD0 buffers";
            return null;
        }

        string unique = Guid.NewGuid().ToString("N")[..8];

        // Verbatim buffer copies under fresh names, registered first — pools must have room before
        // anything else mutates. A buffer shared by several LODs is cloned once and remapped everywhere.
        var vertexClones = new Dictionary<ulong, VertexBuffer>();
        var indexClones = new Dictionary<ulong, IndexBuffer>();
        var added = new DuplicatedObject { Resource = resource, Adapter = adapter };
        bool CloneBuffers()
        {
            for (int i = 0; i < source.Geometry.LOD.Length; i++)
            {
                FrameLOD lod = source.Geometry.LOD[i];
                VertexBuffer? vb = resource.VertexBuffers.GetBuffer(lod.VertexBufferRef.Hash);
                if (vb != null && !vertexClones.ContainsKey(vb.Hash))
                {
                    var copy = new VertexBuffer(Fnv64.Hash(BufferName(vb.Hash, "vb")))
                    {
                        Data = (byte[])vb.Data.Clone(),
                    };
                    if (!resource.VertexBuffers.TryAddToPool(copy)) return false;
                    vertexClones[vb.Hash] = copy;
                    added.VertexBuffers.Add(copy);
                }
                IndexBuffer? ib = resource.IndexBuffers.GetBuffer(lod.IndexBufferRef.Hash);
                if (ib != null && !indexClones.ContainsKey(ib.Hash))
                {
                    var copy = new IndexBuffer(Fnv64.Hash(BufferName(ib.Hash, "ib")));
                    copy.SetFormat(ib.IndexFormat);
                    copy.SetData((uint[])ib.GetData().Clone());
                    if (!resource.IndexBuffers.TryAddToPool(copy)) return false;
                    indexClones[ib.Hash] = copy;
                    added.IndexBuffers.Add(copy);
                }
            }
            return true;
        }
        string BufferName(ulong sourceHash, string kind) => $"{source.Name.String}_{kind}{sourceHash:x8}_{unique}";
        if (!CloneBuffers())
        {
            foreach (VertexBuffer vb in added.VertexBuffers) resource.VertexBuffers.Remove(vb.Hash);
            foreach (IndexBuffer ib in added.IndexBuffers) resource.IndexBuffers.Remove(ib.Hash);
            skipReason = "the archive has no buffer pool to copy the geometry into";
            return null;
        }

        // Geometry block: a deep copy (LODs with their opcode/split capsules), registered with a
        // fresh RefID; then repoint each LOD at the cloned buffers.
        FrameGeometry geometry = resource.ConstructFrameAssetOfType<FrameGeometry>();
        geometry.CopyFrom(source.Geometry);
        foreach (FrameLOD lod in geometry.LOD)
        {
            if (vertexClones.ContainsKey(lod.VertexBufferRef.Hash))
                lod.VertexBufferRef = new HashName(BufferName(lod.VertexBufferRef.Hash, "vb"));
            if (indexClones.ContainsKey(lod.IndexBufferRef.Hash))
                lod.IndexBufferRef = new HashName(BufferName(lod.IndexBufferRef.Hash, "ib"));
        }

        // Material block: the copy constructor deep-copies the ranges; LodMatCount is shared by it, so
        // give the copy its own array (a later LOD0 rebuild writes LodMatCount[0] in place).
        var material = new FrameMaterial(source.Material) { LodMatCount = source.Material.LodMatCount.ToArray() };
        resource.FrameMaterials.Add(material.RefID, material);

        // The frame itself: the copy constructor covers every serialized field (flags, bounds, transform,
        // name-table membership); rewire its identity, blocks and registration.
        var clone = new FrameObjectSingleMesh(source)
        {
            Name = new HashName(UniqueName(resource, source.Name.String)),
            Geometry = geometry,
            Material = material,
        };
        clone.ReplaceRef(FrameEntryRefTypes.Geometry, geometry.RefID);
        clone.ReplaceRef(FrameEntryRefTypes.Material, material.RefID);
        resource.FrameObjects.Add(clone.RefID, clone);

        // Same parents as the source (both slots), resolved through the refs so every shipped shape —
        // nested object, scene-anchored root, true top level — reproduces exactly.
        FrameEntry? parent1 = ResolveRef(resource, source, FrameEntryRefTypes.Parent1);
        FrameEntry? parent2 = ResolveRef(resource, source, FrameEntryRefTypes.Parent2);
        LinkParents(clone, parent1, parent2);
        clone.SetWorldTransform();

        FrameNodeAdapter nodeAdapter = adapter.Node(clone);
        MeshData? mesh = SdsMeshLoader.TryConvert(clone);
        if (mesh == null)
        {
            added.Clone = clone;
            added.Geometry = geometry;
            added.Material = material;
            added.Parent1 = parent1;
            added.Parent2 = parent2;
            added.Detach();
            skipReason = "mesh could not be decoded for display";
            return null;
        }

        foreach (VertexBuffer vb in added.VertexBuffers) adapter.MarkVertexBufferDirty(vb.Hash);
        foreach (IndexBuffer ib in added.IndexBuffers) adapter.MarkIndexBufferDirty(ib.Hash);

        added.Clone = clone;
        added.Geometry = geometry;
        added.Material = material;
        added.Parent1 = parent1;
        added.Parent2 = parent2;
        added.Node = nodeAdapter;
        added.Mesh = mesh;
        return added;
    }

    /// <summary>A mesh frame given a geometry block and buffers of its own, with what undo needs to take them
    /// away again.</summary>
    public sealed class OwnedGeometry
    {
        internal FrameResource Resource = null!;
        internal SceneDocumentAdapter Adapter = null!;
        internal FrameObjectSingleMesh Mesh = null!;
        internal FrameGeometry Before = null!;
        internal FrameGeometry After = null!;
        internal List<VertexBuffer> VertexBuffers = new();
        internal List<IndexBuffer> IndexBuffers = new();
        internal Dictionary<ulong, IndexBuffer> IndexCopies = new();

        /// <summary>The copy that stands, in the mesh's own block, for an index buffer of the shared one.</summary>
        public IndexBuffer? CopyOf(IndexBuffer shared) => shared == null ? null : IndexCopies.GetValueOrDefault(shared.Hash);

        /// <summary>Puts the mesh back on the block it shared (undo).</summary>
        public void Revert()
        {
            Mesh.Geometry = Before;
            Mesh.ReplaceRef(FrameEntryRefTypes.Geometry, Before.RefID);
            Resource.FrameGeometries.Remove(After.RefID);
            foreach (VertexBuffer vb in VertexBuffers) Resource.VertexBuffers.Remove(vb.Hash);
            foreach (IndexBuffer ib in IndexBuffers) Resource.IndexBuffers.Remove(ib.Hash);
        }

        /// <summary>Puts the mesh on its own block again (redo). Re-registers what a save-time sanitize may
        /// have pruned while it was away.</summary>
        public void Apply()
        {
            if (!Resource.FrameGeometries.ContainsKey(After.RefID)) Resource.FrameGeometries.Add(After.RefID, After);
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
            Mesh.Geometry = After;
            Mesh.ReplaceRef(FrameEntryRefTypes.Geometry, After.RefID);
        }
    }

    /// <summary>
    /// Gives a mesh frame that draws a geometry block other frames draw too a block of its OWN: a copy of the
    /// block over copies of its vertex and index buffers - what a duplicate of the object gets, without the
    /// duplicate. The frame looks exactly as before; a change to its geometry now changes it alone. Applied
    /// when returned. Null with a reason when it cannot be done (missing buffers, full pools).
    /// </summary>
    public static OwnedGeometry? TryOwnGeometry(ISceneDocument document, IFrameNode node, out string? reason)
    {
        reason = null;
        if (document is not SceneDocumentAdapter adapter || node is not FrameNodeAdapter { Frame: FrameObjectSingleMesh mesh })
        {
            reason = "only a mesh has geometry to copy";
            return null;
        }
        FrameResource resource = adapter.Frame;
        if (mesh.Geometry?.LOD is not { Length: > 0 })
        {
            reason = "the mesh has no geometry";
            return null;
        }

        string unique = Guid.NewGuid().ToString("N")[..8];
        string BufferName(ulong sourceHash, string kind) => $"{mesh.Name.String}_{kind}{sourceHash:x8}_{unique}";
        var owned = new OwnedGeometry { Resource = resource, Adapter = adapter, Mesh = mesh, Before = mesh.Geometry };
        var vertexCopies = new HashSet<ulong>();
        bool room = true;
        foreach (FrameLOD lod in mesh.Geometry.LOD)
        {
            VertexBuffer? vb = resource.VertexBuffers.GetBuffer(lod.VertexBufferRef.Hash);
            IndexBuffer? ib = resource.IndexBuffers.GetBuffer(lod.IndexBufferRef.Hash);
            if (vb == null || ib == null)
            {
                reason = "a level of the mesh has no buffers in this archive";
                room = false;
                break;
            }
            if (vertexCopies.Add(vb.Hash))
            {
                var copy = new VertexBuffer(Fnv64.Hash(BufferName(vb.Hash, "vb"))) { Data = (byte[])vb.Data.Clone() };
                if (!resource.VertexBuffers.TryAddToPool(copy)) { room = false; break; }
                owned.VertexBuffers.Add(copy);
            }
            if (!owned.IndexCopies.ContainsKey(ib.Hash))
            {
                var copy = new IndexBuffer(Fnv64.Hash(BufferName(ib.Hash, "ib")));
                copy.SetFormat(ib.IndexFormat);
                copy.SetData((uint[])ib.GetData().Clone());
                if (!resource.IndexBuffers.TryAddToPool(copy)) { room = false; break; }
                owned.IndexCopies[ib.Hash] = copy;
                owned.IndexBuffers.Add(copy);
            }
        }
        if (!room)
        {
            foreach (VertexBuffer vb in owned.VertexBuffers) resource.VertexBuffers.Remove(vb.Hash);
            foreach (IndexBuffer ib in owned.IndexBuffers) resource.IndexBuffers.Remove(ib.Hash);
            reason ??= "the archive has no buffer pool to copy the geometry into";
            return null;
        }

        FrameGeometry geometry = resource.ConstructFrameAssetOfType<FrameGeometry>();
        geometry.CopyFrom(mesh.Geometry);
        foreach (FrameLOD lod in geometry.LOD)
        {
            ulong vertices = lod.VertexBufferRef.Hash, indices = lod.IndexBufferRef.Hash;
            lod.VertexBufferRef = new HashName(BufferName(vertices, "vb"));
            lod.IndexBufferRef = new HashName(BufferName(indices, "ib"));
        }
        owned.After = geometry;
        owned.Apply();
        return owned;
    }

    // "<name>_copy", then "<name>_copy2", … — the frame name table is keyed by name, so a copy must not
    // collide with any existing object.
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

    private static FrameEntry? ResolveRef(FrameResource resource, FrameObjectBase source, FrameEntryRefTypes slot)
    {
        if (!source.Refs.TryGetValue(slot, out int id)) return null;
        if (resource.FrameScenes.TryGetValue(id, out FrameHeaderScene? scene)) return scene;
        if (resource.FrameObjects.TryGetValue(id, out object? obj)) return obj as FrameEntry;
        return null;
    }

    // Writes both parent slots the way the loader/reparenter do: SetParent maintains the frame-side
    // runtime links; scene folders hold their members in a separate list the setter does not touch.
    private static void LinkParents(FrameObjectSingleMesh clone, FrameEntry? parent1, FrameEntry? parent2)
    {
        clone.SetParent(ParentInfo.ParentType.ParentIndex1, parent1);
        clone.SetParent(ParentInfo.ParentType.ParentIndex2, parent2);
        if (parent2 is FrameHeaderScene scene && clone.Parent == null && !scene.Children.Contains(clone))
            scene.Children.Add(clone);
    }
}
