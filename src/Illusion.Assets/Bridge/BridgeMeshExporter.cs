using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Bridge.Geometry;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Bridge;

/// <summary>
/// Turns one scene object into a kind="mesh" exchange payload: full-channel LOD0 decode, bit-exact
/// weld (keyed on the RAW quantized position bytes, handedness bit masked), per-loop attributes with
/// the Blender UV V-flip, and material slots resolved to absolute .dds paths in the document's
/// extracted folder.
/// </summary>
public static class BridgeMeshExporter
{
    /// <summary>Exports a node's mesh; null with a human-readable <paramref name="skipReason"/> when
    /// the object cannot ride the bridge (instanced content is the caller's check — it needs the GPU
    /// mesh, which this layer never sees).</summary>
    /// <param name="lod">Which level of detail to send. Blender is shown exactly what the viewport is
    /// showing, and the push comes back into this same level — see <c>BridgeMeshApplier.TryApply</c>.</param>
    public static MeshObjectPayload? TryExport(IFrameNode node, ISceneDocument document, out string? skipReason,
        int lod = 0)
    {
        skipReason = null;

        if (node is not FrameNodeAdapter adapter)
        {
            skipReason = "not a frame-backed object";
            return null;
        }
        // A skinned model (FrameObjectModel) rides too, with its rig and weights — that is the whole
        // point on a car, whose parts exist only as bones. Anything else derived from SingleMesh does
        // not: its extra blocks would not survive the roundtrip.
        if (adapter.Frame is not FrameObjectSingleMesh frame
            || (frame.GetType() != typeof(FrameObjectSingleMesh) && frame is not FrameObjectModel))
        {
            skipReason = $"unsupported frame type {adapter.Frame.GetType().Name}";
            return null;
        }

        DecodedMesh? decoded;
        try
        {
            decoded = SdsMeshLoader.DecodeLod(frame, lod);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            skipReason = "undecodable mesh: " + ex.Message;
            return null;
        }
        if (decoded == null)
        {
            skipReason = "mesh has no usable geometry buffers";
            return null;
        }
        if (decoded.Declaration.HasFlag(VertexFlags.Skin) && frame is not FrameObjectModel)
        {
            skipReason = "skinned vertex data on a frame that carries no rig";
            return null;
        }

        WeldedMesh welded = WeldFor(decoded);

        List<MeshMaterialInfo> materials = ResolveMaterials(frame, document, decoded.Indices.Length, decoded.Lod);

        // Material slot per KEPT triangle (degenerates were filtered by the weld — see WeldedMesh).
        ushort[] perSourceTriangle = BuildFaceMaterials(materials, decoded.Indices.Length);
        var faceMaterials = new ushort[welded.KeptTriangles.Length];
        for (int i = 0; i < faceMaterials.Length; i++)
            faceMaterials[i] = perSourceTriangle[welded.KeptTriangles[i]];

        // The skin, welded the same way the geometry was: split vertices that merged came from one
        // source vertex and carry one set of influences, so the first source vertex to claim a welded
        // slot decides it.
        byte[] boneIndices = [];
        float[] boneWeights = [];
        string? skeletonId = null;
        string? skinWarning = null;
        if (frame is FrameObjectModel model && decoded.BoneIndices is { } ids && decoded.BoneWeights is { } weights)
        {
            byte[]? resolved = ResolveSkin(model, decoded);
            if (resolved == null)
            {
                // The archive's own skin cannot be read. Sending the raw ids instead — which is what this
                // used to do — hands Blender vertex groups named after the WRONG bones, and every edit made
                // through them is silently wrong. No skin is the honest answer.
                skinWarning = "this mesh's skin cannot be resolved, so it was sent without bone weights — "
                    + "its vertex groups would have been named after the wrong bones. "
                    + SdsMeshLoader.DescribeBoneRemap(model);
            }
            else
            {
                boneIndices = new byte[welded.Positions.Length * 4];
                boneWeights = new float[welded.Positions.Length * 4];
                var claimed = new bool[welded.Positions.Length];
                for (int split = 0; split < welded.SplitToWelded.Length; split++)
                {
                    int target = welded.SplitToWelded[split];
                    if (target < 0 || target >= claimed.Length || claimed[target]) continue;
                    if (((split * 4) + 3) >= resolved.Length) continue;
                    claimed[target] = true;
                    for (int k = 0; k < 4; k++)
                    {
                        boneIndices[(target * 4) + k] = resolved[(split * 4) + k];
                        boneWeights[(target * 4) + k] = weights[(split * 4) + k];
                    }
                }
                skeletonId = SkeletonId(model, document);
            }
        }

        return new MeshObjectPayload
        {
            // The LEVEL is part of the identity. Both levels of a car are one frame, so sent together under
            // the frame's id alone they were one object to the session: the push of the near level came back
            // into the far one (whose pools do not hold its bones) and the other arrived as a brand-new mesh.
            Id = decoded.Lod > 0
                ? MakeId(frame, document) + "|lod" + decoded.Lod.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : MakeId(frame, document),
            BoneIndices = boneIndices,
            BoneWeights = boneWeights,
            SkeletonId = skeletonId,
            SkinWarning = skinWarning,
            Name = frame.Name?.ToString() ?? "mesh",
            // The NODE's world, not the frame's: an actor-placed object is a prototype parked at the origin,
            // and its spawn matrix lives in the .act. Sending the frame's own world would drop it at (0,0,0)
            // in Blender while the viewport shows it in the street — and, worse, the push-back compares the
            // returned matrix against this same placement-aware world, so every untouched prototype would
            // read as moved and have the inverse placement baked into its local transform. The two are the
            // same matrix for every frame no actor places.
            World = adapter.WorldTransform,
            Local = frame.LocalTransform,
            Positions = welded.Positions,
            LoopVertexIndices = welded.LoopVertexIndices,
            LoopNormals = welded.LoopNormals,
            LoopUvs = welded.LoopUvs,
            LoopOrigIndex = welded.LoopOrigIndex,
            FaceMaterials = faceMaterials,
            Materials = materials,
            DroppedDegenerateFaces = welded.DroppedDegenerateTriangles,
            DroppedDuplicateFaces = welded.DroppedDuplicateTriangles,
            VertexDeclaration = (uint)decoded.Declaration,
            DecompressionOffset = decoded.DecompressionOffset,
            DecompressionFactor = decoded.DecompressionFactor,
        };
    }

    /// <summary>
    /// The rig of a skinned model as its own exchange object, or null when the node carries none. Emitted
    /// beside the mesh that points at it through <see cref="MeshObjectPayload.SkeletonId"/>.
    /// </summary>
    public static SkeletonObjectPayload? TryExportSkeleton(IFrameNode node, ISceneDocument document)
    {
        if (node is not FrameNodeAdapter adapter || adapter.Frame is not FrameObjectModel model) return null;

        HashName[] names;
        byte[] parents;
        try
        {
            names = model.GetSkeletonObject().BoneNames ?? [];
            parents = model.GetSkeletonHierarchyObject().ParentIndices ?? [];
        }
        catch (Exception)
        {
            return null;
        }

        Matrix4x4[] rest = model.RestTransform ?? [];
        int count = Math.Min(names.Length, rest.Length);
        if (count == 0) return null;

        var boneParents = new int[count];
        for (int i = 0; i < count; i++)
        {
            int parent = i < parents.Length ? parents[i] : -1;
            // A root is its own parent in this format; Blender needs the absence spelled out.
            boneParents[i] = parent == i || parent >= count ? -1 : parent;
        }

        return new SkeletonObjectPayload
        {
            Id = SkeletonId(model, document),
            Name = model.Name?.ToString() ?? "rig",
            World = adapter.WorldTransform,
            BoneNames = [.. Enumerable.Range(0, count).Select(i => names[i].ToString() ?? $"bone{i}")],
            BoneParents = boneParents,
            BoneRest = [.. rest.Take(count)],
        };
    }

    /// <summary>
    /// The skinned model's bone ids resolved to its own bone list — on the wire they index a per-LOD remap
    /// pool instead (see <c>--probe-skinning</c>). NULL when the blend info does not line up with the mesh.
    /// <para>
    /// It used to fall back to the raw ids, and that fallback was a trap: pool-local ids name different bones
    /// entirely, so Blender built its vertex groups under the wrong names and every edit made through them
    /// was wrong with nothing to show for it. The caller sends no skin at all instead, and says why.
    /// </para>
    /// </summary>
    private static byte[]? ResolveSkin(FrameObjectModel model, DecodedMesh decoded) =>
        SdsMeshLoader.ResolveBoneRemap(
            model, SdsMeshLoader.BuildParts(model, decoded.Indices.Length, decoded.Lod), decoded);

    private static string SkeletonId(FrameObjectModel model, ISceneDocument document)
    {
        string rel = MafiaEnvironment.IsInitialized
            ? Path.GetRelativePath(MafiaEnvironment.GameRoot, document.SourceArchive.FullName)
            : document.SourceArchive.Name;
        return $"{rel.Replace('\\', '/')}|{model.Name}|rig";
    }

    /// <summary>Stable-within-session object id: archive-relative path + frame name + runtime RefID.
    /// RefID is NOT stable across toolkit runs — the session controller resolves ids only through its
    /// own export map, and the session GUID guards against stale pushes.</summary>
    public static string MakeId(FrameObjectBase frame, ISceneDocument document)
    {
        string rel = MafiaEnvironment.IsInitialized
            ? Path.GetRelativePath(MafiaEnvironment.GameRoot, document.SourceArchive.FullName)
            : document.SourceArchive.Name;
        return $"{rel.Replace('\\', '/')}|{frame.Name}|{frame.RefID}";
    }

    // Weld key = the raw quantized position triple (x | y<<16 | z<<32) with the binormal-handedness
    // top bit of Z masked off — two split vertices weld iff the game data itself agrees on position.
    /// <summary>
    /// The weld this mesh is exported with. Shared so a diagnostic can reproduce it exactly rather than
    /// approximate it — a weld that differs by one vertex tells you nothing about the one that shipped.
    /// </summary>
    public static WeldedMesh WeldFor(DecodedMesh decoded) =>
        WeldMapBuilder.Build(
            BuildWeldKeys(decoded), decoded.Positions, decoded.Normals, FlipV(decoded.UVs), decoded.Indices,
            BuildSkinKeys(decoded));

    /// <summary>
    /// One key per split vertex identifying its SKIN — the four bone ids and their weights — or null when the
    /// mesh has none. Two vertices sharing a position but not a skin must not be welded: on a car the door's
    /// edge and the body's edge sit at the same point and answer to different bones, and merging them binds
    /// body geometry to the door.
    /// <para>
    /// Weights are quantized to a byte, which is what the vertex buffer stores them as anyway, so two skins
    /// that differ by less than that were never distinguishable in the first place.
    /// </para>
    /// </summary>
    internal static ulong[]? BuildSkinKeys(DecodedMesh decoded)
    {
        if (decoded.BoneIndices is not { } ids || decoded.BoneWeights is not { } weights) return null;

        var keys = new ulong[decoded.NumVerts];
        for (int i = 0; i < keys.Length && ((i * 4) + 3) < ids.Length; i++)
        {
            ulong key = 0;
            for (int k = 0; k < 4; k++)
            {
                key |= (ulong)ids[(i * 4) + k] << (k * 8);
                byte w = (byte)Math.Clamp((int)MathF.Round(weights[(i * 4) + k] * 255f), 0, 255);
                key |= (ulong)w << (32 + (k * 8));
            }
            keys[i] = key;
        }
        return keys;
    }

    // Internal: the applier re-derives the exported face set from the same keys to detect topology
    // changes (deleted/reshaped faces) in a pushed mesh.
    internal static ulong[] BuildWeldKeys(DecodedMesh decoded)
    {
        Dictionary<VertexFlags, VertexOffset> offsets = VertexLayout.ComputeOffsets(decoded.Declaration, out _);
        int posOffset = offsets[VertexFlags.Position].Offset;
        byte[] data = decoded.RawVertexData;

        var keys = new ulong[decoded.NumVerts];
        for (int i = 0; i < keys.Length; i++)
        {
            int at = i * decoded.Stride + posOffset;
            ulong x = (ulong)(data[at + 0] | (data[at + 1] << 8));
            ulong y = (ulong)(data[at + 2] | (data[at + 3] << 8));
            ulong z = (ulong)(data[at + 4] | (data[at + 5] << 8)) & 0x7FFF;
            keys[i] = x | (y << 16) | (z << 32);
        }
        return keys;
    }

    // D3D's UV origin is top-left, Blender's bottom-left — the container carries Blender's convention.
    private static Vector2[] FlipV(Vector2[] uvs)
    {
        var flipped = new Vector2[uvs.Length];
        for (int i = 0; i < uvs.Length; i++) flipped[i] = new Vector2(uvs[i].X, 1f - uvs[i].Y);
        return flipped;
    }

    private static List<MeshMaterialInfo> ResolveMaterials(
        FrameObjectSingleMesh frame, ISceneDocument document, int indexCount, int lod)
    {
        var result = new List<MeshMaterialInfo>();
        List<string> textureDirs = TextureSearchDirs(document);

        // The slot list of the level being sent: a coarser level drops materials the fine one draws, and
        // handing Blender LOD0's list would label its faces with slots this geometry does not have.
        FrameMaterial fm = frame.Material;
        if (fm?.Materials is { Count: > 0 } && lod < fm.Materials.Count
            && fm.Materials[lod] is { Length: > 0 } mats)
        {
            MafiaMaterials.EnsureLoaded();
            foreach (MaterialStruct mat in mats)
            {
                MafiaMaterials.MaterialTextures tex = MafiaMaterials.GetMaterialTextures(mat.MaterialHash);
                result.Add(new MeshMaterialInfo
                {
                    Hash = "0x" + mat.MaterialHash.ToString("X16"),
                    // The frame stream stores only the hash — the display name lives in the MTL libraries.
                    Name = string.IsNullOrEmpty(mat.MaterialName)
                        ? MafiaMaterials.GetMaterialName(mat.MaterialHash)
                        : mat.MaterialName,
                    Diffuse = ResolveTexture(textureDirs, tex.Diffuse),
                    Normal = ResolveTexture(textureDirs, tex.Normal),
                    // Mafia II ships its S001 normal maps DXT5nm-swizzled (X in alpha) — flag them so
                    // the addon unswizzles for preview.
                    NormalIsDxt5nm = tex.Normal != null,
                    Specular = ResolveTexture(textureDirs, tex.Specular),
                    StartIndex = mat.StartIndex,
                    NumFaces = mat.NumFaces,
                });
            }
            return result;
        }

        // No material table — one implicit slot covering the whole mesh.
        result.Add(new MeshMaterialInfo { Hash = "0x0", StartIndex = 0, NumFaces = indexCount / 3 });
        return result;
    }

    // The same folder set the viewport's TextureLibrary probes: the document's own extracted
    // folder plus the shared season ground archives — many district materials reference textures
    // that physically live there.
    private static List<string> TextureSearchDirs(ISceneDocument document)
    {
        var dirs = new List<string> { SdsMeshLoader.EnsureExtracted(document.SourceArchive) };
        if (MafiaEnvironment.IsInitialized)
        {
            foreach (string ground in new[] { "ground_leto", "ground_zima" })
            {
                string sds = Path.Combine(MafiaEnvironment.PcFolder, "sds", "ground", ground + ".sds");
                if (File.Exists(sds)) dirs.Add(SdsMeshLoader.EnsureExtracted(new FileInfo(sds)));
            }
        }
        return dirs;
    }

    private static string? ResolveTexture(List<string> textureDirs, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (string dir in textureDirs)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // Material slot per triangle, from the LOD0 index ranges the slots cover.
    private static ushort[] BuildFaceMaterials(List<MeshMaterialInfo> materials, int indexCount)
    {
        var faces = new ushort[indexCount / 3];
        for (int slot = 0; slot < materials.Count; slot++)
        {
            MeshMaterialInfo mat = materials[slot];
            int firstFace = mat.StartIndex / 3;
            for (int f = 0; f < mat.NumFaces && firstFace + f < faces.Length; f++)
                faces[firstFace + f] = (ushort)slot;
        }
        return faces;
    }
}
