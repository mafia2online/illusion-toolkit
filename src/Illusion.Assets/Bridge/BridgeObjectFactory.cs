using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Bridge;

/// <summary>
/// Creates a brand-new <see cref="FrameObjectSingleMesh"/> from a mesh the user built in Blender or
/// imported from a file. The frame starts as a minimal 3-vertex placeholder (fresh geometry/material
/// blocks, fresh FNV64-named buffers appended to pools with spare capacity) and the REAL geometry then
/// flows in through the ordinary rebuild path — one code path fills both edited and newborn meshes.
/// <para>
/// The created object mirrors the ONLY shape a drawable stock mesh ships in (census over eastside:
/// 2516/2516 drawable meshes): anchored to the district's main scene via ParentIndex2, ON the frame
/// name table — the table is the game's spawn list, an object missing from it is never instantiated —
/// with an "always draw" LOD range. A district with no scene folders falls back to a parentless,
/// off-table object (visible in the editor only).
/// </para>
/// </summary>
public static class BridgeObjectFactory
{
    /// <summary>The stock "no switch-out" LOD0 distance (LOD distances are squared metres; this is the
    /// value 991 of eastside's 2528 meshes carry).</summary>
    private const float AlwaysDrawDistance = 999999995904f;

    /// <summary>A created object with everything undo needs to detach and re-attach it.</summary>
    public sealed class CreatedObject
    {
        internal FrameResource Resource = null!;
        internal FrameObjectSingleMesh Frame = null!;
        internal VertexBuffer VertexBuffer = null!;
        internal IndexBuffer IndexBuffer = null!;
        internal FrameHeaderScene? Anchor;

        /// <summary>The editor-facing node adapter (canonical for the document).</summary>
        public IFrameNode Node { get; internal set; } = null!;

        /// <summary>Whether the frame object is currently registered in its resource (diagnostics).</summary>
        public bool IsAttached => Resource.FrameObjects.ContainsKey(Frame.RefID);

        /// <summary>Whether the object was put on the frame name table (the caller must mark the
        /// document's name table dirty so a save rewrites it).</summary>
        public bool OnNameTable => Frame.IsOnFrameTable;

        /// <summary>Whether <paramref name="source"/> is the tree adapter of the scene folder this
        /// object was anchored to — the caller parents the new tree node under that scene's node.</summary>
        public bool IsAnchorNode(ISceneSource? source) =>
            Anchor != null && source is Adapters.FrameSceneAdapter fsa && ReferenceEquals(fsa.Scene, Anchor);

        /// <summary>The filled-in geometry (rebuild result), already applied to the frame data.</summary>
        public BridgeMeshApplier.ApplyResult Geometry { get; internal set; } = null!;

        /// <summary>Removes the object from the frame data (undo of the creation).</summary>
        public void Detach()
        {
            Frame.SetParent(ParentInfo.ParentType.ParentIndex2, null);
            Anchor?.Children.Remove(Frame);
            Resource.DeleteFrame(Frame);
            Resource.VertexBuffers.Remove(VertexBuffer.Hash);
            Resource.IndexBuffers.Remove(IndexBuffer.Hash);
        }

        /// <summary>Puts the object back (redo). Re-registers blocks a save-time sanitize may have
        /// pruned while the object was detached, and re-links the scene anchor.</summary>
        public void Reattach()
        {
            if (!Resource.FrameObjects.ContainsKey(Frame.RefID)) Resource.FrameObjects.Add(Frame.RefID, Frame);
            if (!Resource.FrameGeometries.ContainsKey(Frame.Geometry.RefID))
                Resource.FrameGeometries.Add(Frame.Geometry.RefID, Frame.Geometry);
            if (!Resource.FrameMaterials.ContainsKey(Frame.Material.RefID))
                Resource.FrameMaterials.Add(Frame.Material.RefID, Frame.Material);
            Resource.VertexBuffers.TryAddToPool(VertexBuffer);
            Resource.IndexBuffers.TryAddToPool(IndexBuffer);
            if (Anchor != null)
            {
                Frame.SetParent(ParentInfo.ParentType.ParentIndex2, Anchor);
                if (!Anchor.Children.Contains(Frame)) Anchor.Children.Add(Frame);
            }
        }
    }

    /// <summary>Creates the object and fills it with the pushed mesh. Null with a reason when the
    /// payload cannot become a game object (no game material assigned, pools full, bad geometry).</summary>
    public static CreatedObject? TryCreate(ISceneDocument document, MeshObjectPayload payload, out string? skipReason)
    {
        skipReason = null;
        if (document is not SceneDocumentAdapter adapter)
        {
            skipReason = "unsupported document";
            return null;
        }
        if (payload.LoopOrigIndex.Length < 3)
        {
            skipReason = "mesh has no faces";
            return null;
        }
        MafiaMaterials.EnsureLoaded();
        if (payload.Materials.Count == 0)
        {
            skipReason = "assign a game material (one that came from the toolkit) to the new object first";
            return null;
        }

        FrameResource resource = adapter.Frame;
        string baseName = string.IsNullOrWhiteSpace(payload.Name) ? "blender_object" : payload.Name;
        string unique = Guid.NewGuid().ToString("N")[..8];

        // Fresh, collision-free buffer identities; pools must have room before anything mutates.
        var vertexBuffer = new VertexBuffer(Fnv64.Hash($"{baseName}_vb_{unique}"));
        var indexBuffer = new IndexBuffer(Fnv64.Hash($"{baseName}_ib_{unique}"));
        const VertexFlags declaration =
            VertexFlags.Position | VertexFlags.Normals | VertexFlags.Tangent | VertexFlags.TexCoords0;
        VertexLayout.ComputeOffsets(declaration, out int stride);
        vertexBuffer.Data = new byte[3 * stride]; // 1-triangle placeholder the rebuild replaces
        indexBuffer.SetData(new uint[] { 0, 1, 2 });
        if (!resource.VertexBuffers.TryAddToPool(vertexBuffer))
        {
            skipReason = "the archive has no vertex buffer pool to add to";
            return null;
        }
        if (!resource.IndexBuffers.TryAddToPool(indexBuffer))
        {
            resource.VertexBuffers.Remove(vertexBuffer.Hash);
            skipReason = "the archive has no index buffer pool to add to";
            return null;
        }

        FrameObjectSingleMesh frame = resource.ConstructFrameAssetOfType<FrameObjectSingleMesh>();
        frame.Name = new HashName(baseName);

        FrameGeometry geometry = frame.Geometry; // lazy-constructs + registers + wires the ref
        // The placeholder LOD0 (one triangle, no material bursts) comes from the native capsule
        // builder; the first real push rebuilds it with the pushed topology.
        // LOD distances are squared metres and double as the draw range — 0 would mean "switch out
        // immediately", i.e. never visible in game.
        FrameLOD lod = FrameLOD.CreateRebuilt(AlwaysDrawDistance,
            new HashName($"{baseName}_ib_{unique}"), new HashName($"{baseName}_vb_{unique}"),
            declaration, numVerts: 3, indexStride: 2, numFaces: 1, slots: []);
        geometry.NumLods = 1;
        geometry.LOD = new[] { lod };
        geometry.DecompressionOffset = Vector3.Zero;
        // A vanishingly small placeholder factor: any real vertex falls outside the lattice, so the
        // rebuild always derives a proper quantization from the pushed AABB.
        geometry.DecompressionFactor = 1e-9f;

        FrameMaterial material = frame.Material;
        material.NumLods = 1;
        material.LodMatCount = new[] { 1 };
        material.Materials.Clear();
        material.Materials.Add(new[] { new MaterialStruct { StartIndex = 0, NumFaces = 1 } });

        // Scene folders carry no transform, so local IS world either way.
        frame.LocalTransform = payload.World;

        // The stock drawable shape: anchored to the district's main scene (ParentIndex2), on the frame
        // name table (the game's spawn list), normal-season flags. Without a scene to anchor to the
        // object stays parentless and off-table — editor-visible only.
        FrameHeaderScene? anchor = PickMainScene(resource);
        if (anchor != null)
        {
            frame.SetParent(ParentInfo.ParentType.ParentIndex2, anchor);
            anchor.Children.Add(frame); // SetParent maintains frame links; a scene's child list is manual
            frame.IsOnFrameTable = true;
            frame.FrameNameTableFlags = 0;
            frame.SingleMeshFlags |= SingleMeshFlags.ParentIndex2_Flag; // the anchored-mesh bit every stock P2 mesh carries
        }

        FrameNodeAdapter node = adapter.Node(frame);
        BridgeMeshApplier.ApplyResult? geometryResult = BridgeMeshApplier.TryApply(node, payload, out string? reason);
        if (geometryResult == null || geometryResult.Unchanged)
        {
            frame.SetParent(ParentInfo.ParentType.ParentIndex2, null);
            anchor?.Children.Remove(frame);
            resource.DeleteFrame(frame);
            resource.VertexBuffers.Remove(vertexBuffer.Hash);
            resource.IndexBuffers.Remove(indexBuffer.Hash);
            skipReason = reason ?? "pushed mesh produced no geometry";
            return null;
        }
        geometryResult.ApplyNew();

        return new CreatedObject
        {
            Resource = resource,
            Frame = frame,
            VertexBuffer = vertexBuffer,
            IndexBuffer = indexBuffer,
            Anchor = anchor,
            Node = node,
            Geometry = geometryResult,
        };
    }

    // The scene the game actually populates: the folder holding the most normal-season, on-table
    // objects (eastside: 'scene10' with 2110 of them; the winter/proxy folders hold zero). Null when
    // the district has no scene folders at all.
    internal static FrameHeaderScene? PickMainScene(FrameResource resource)
    {
        FrameHeaderScene? best = null;
        int bestScore = -1;
        foreach (FrameHeaderScene scene in resource.FrameScenes.Values)
        {
            int score = 0;
            foreach (FrameObjectBase child in scene.Children)
                if (child.IsOnFrameTable && (int)child.FrameNameTableFlags == 0) score++;
            if (score > bestScore)
            {
                bestScore = score;
                best = scene;
            }
        }
        return best;
    }
}
