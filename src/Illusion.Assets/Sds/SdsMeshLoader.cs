using System.Numerics;
using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Domain;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using Illusion.Formats.Hashing;
using Illusion.Formats.Translokator;

namespace Illusion.Assets.Sds;

/// <summary>
/// Reads a single .sds and produces a list of <see cref="MeshData"/>: SdsArchive (unpack) →
/// SceneData (FrameResource + buffer pools) → VertexTranslator (vertex unpacking).
/// </summary>
public static class SdsMeshLoader
{
    // Extraction is check-then-act filesystem work (unpack into a shared legacy folder, then
    // delete+move) and now runs on background loaders that can outlive their viewport — after a
    // close-and-reopen two instances may request the same .sds concurrently. One process-wide lock
    // serializes the cold path; the warm path stays lock-free.
    private static readonly object ExtractSync = new();

    /// <summary>
    /// Ensures the SDS is unpacked into the mirror <c>&lt;root&gt;\resources\…\&lt;name&gt;.sds\</c>, and
    /// returns that folder. A folder left in the legacy <c>&lt;dir&gt;\extracted\</c> location (older
    /// MafiaToolkit runs) is adopted by moving it into /resources instead of re-extracting.
    /// </summary>
    public static string EnsureExtracted(FileInfo sdsFile)
    {
        string target = MafiaEnvironment.ExtractedDir(sdsFile);
        if (File.Exists(Path.Combine(target, "SDSContent.xml")))
        {
            return target;
        }

        lock (ExtractSync)
        {
            if (File.Exists(Path.Combine(target, "SDSContent.xml")))
            {
                return target; // extracted by whoever held the lock before us
            }

            string legacy = Path.Combine(sdsFile.DirectoryName!, "extracted", sdsFile.Name);
            if (File.Exists(Path.Combine(legacy, "SDSContent.xml")))
            {
                RelocateToResources(legacy, target);
                return target;
            }

            SdsArchive.Open(sdsFile.FullName).Extract(target);
            return target;
        }
    }

    private static void RelocateToResources(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(to)) Directory.Delete(to, true);
        Directory.Move(from, to); // same volume → instant
    }

    // Opens an already-extracted scene folder (little-endian PC data; console archives would pass
    // true). Public for the diagnostics probes; app code goes through the Load* entry points.
    public static ExtractedSds OpenScene(string extractedDir)
    {
        return ExtractedSds.Load(extractedDir);
    }

    // Shared loader prologue: extract (if needed), warm up materials, build the scene, and return its
    // FrameResource — or null when the scene carries no frame objects. `placements` is where the archive's
    // actor pack puts the prototype objects it spawns (empty when there is none).
    private static FrameResource? OpenFrameResource(FileInfo sdsFile, out string extracted,
        out ActorPlacements placements)
    {
        extracted = EnsureExtracted(sdsFile);
        MafiaMaterials.EnsureLoaded();
        ExtractedSds scene = OpenScene(extracted);
        FrameResource? fr = scene.FrameResource;
        if (fr?.FrameObjects == null)
        {
            placements = ActorPlacements.Empty;
            return null;
        }
        placements = ActorPlacements.Load(scene.Manifest, fr);
        return fr;
    }

    public static List<MeshData> LoadSds(FileInfo sdsFile, int lod = 0)
    {
        var result = new List<MeshData>();
        FrameResource? fr = OpenFrameResource(sdsFile, out _, out ActorPlacements placements);
        if (fr == null) return result;

        foreach (var pair in fr.FrameObjects)
        {
            if (pair.Value is FrameObjectSingleMesh mesh && mesh.Geometry != null)
            {
                MeshData? md = TryConvert(mesh, placement: placements.For(mesh), lod: lod);
                if (md != null) result.Add(md);
            }
        }
        return result;
    }

    /// <summary>
    /// Reads a .sds and returns the INTERNAL FrameResource hierarchy (frame tree) + a flat list of
    /// meshes + the loaded document (the save/build unit, null when the archive carries no frame
    /// objects). Children come from <c>FrameObjectBase.Children</c> — the union of the frames linked
    /// through both parent slots — and each frame is claimed the first time it is placed, so a frame
    /// reachable through more than one link appears exactly once. Roots are the scene folders plus the
    /// frames whose two parent slots are both empty.
    /// </summary>
    /// <param name="lod">Which level of detail to build the render meshes from; clamped per mesh to what it
    /// ships. The whole editing chain follows this level — a push writes back into it.</param>
    public static (List<SdsFrameNode> Roots, List<MeshData> Meshes, ISceneDocument? Document) LoadHierarchy(
        FileInfo sdsFile, IReadOnlyCollection<string>? districtNames = null, int lod = 0)
    {
        // Names of OTHER districts (to detect neighbor proxy meshes), except the one being loaded.
        string self = Path.GetFileNameWithoutExtension(sdsFile.Name);
        var others = new List<string>();
        if (districtNames != null)
            foreach (string d in districtNames)
                if (!d.Equals(self, StringComparison.OrdinalIgnoreCase)) others.Add(d);

        var meshes = new List<MeshData>();
        FrameResource? fr = OpenFrameResource(sdsFile, out string folder, out ActorPlacements placements);
        if (fr == null) return (new List<SdsFrameNode>(), meshes, null);

        // A car writes each collision placement down twice — as a stub frame here and as a volume in its
        // prefab — and the prefab is the copy the game reads. Where they disagree the stub is the one that is
        // wrong, so it is snapped onto the truth before anyone can see it or drag it. No-op for anything that
        // is not a car: the lookup costs one manifest read when there is no PREFAB to find.
        Collisions.CarPhysicsVolumes.AlignStubsToPrefab(folder, fr);

        var document = new SceneDocumentAdapter(fr, sdsFile, placements);
        var roots = BuildRoots(fr, document, others, meshes, null, lod);
        return (roots, meshes, document);
    }

    /// <summary>
    /// city_crash: the same frame objects as a regular SDS (folder → hierarchy), but prototype meshes
    /// referenced by the Translokator table become INSTANCED — their copies are placed according to
    /// the table (see <see cref="MeshData.Instances"/>). Meshes without references are drawn as usual.
    /// </summary>
    public static (List<SdsFrameNode> Roots, List<MeshData> Meshes, ISceneDocument? Document,
        CrashPlacements? Placements) LoadCrashHierarchy(FileInfo crashSds, int lod = 0)
    {
        var meshes = new List<MeshData>();
        FrameResource? fr = OpenFrameResource(crashSds, out string extracted, out ActorPlacements actors);
        if (fr == null) return (new List<SdsFrameNode>(), meshes, null, null);

        var document = new SceneDocumentAdapter(fr, crashSds, actors);
        CrashPlacements? placements = LoadPlacements(crashSds, extracted, fr);
        var roots = BuildRoots(fr, document, Array.Empty<string>(), meshes, placements?.BuildClouds(), lod);
        return (roots, meshes, document, placements);
    }

    // Reads the Translokator table (and the other season's, so an edit can be mirrored into it) and resolves its
    // rows against the frame resource. Null when the archive carries no table — then the crash SDS just draws its
    // prototypes where they stand.
    private static CrashPlacements? LoadPlacements(FileInfo crashSds, string extracted, FrameResource fr)
    {
        string? tra = SdsTranslokatorSaver.ResolvePath(extracted);
        if (tra == null) return null;

        TranslokatorLoader table;
        try { table = new TranslokatorLoader(new FileInfo(tra)); }
        catch { return null; } // a table we cannot read is a table we must not write back

        (TranslokatorLoader? twin, FileInfo? twinArchive) = LoadTwinTable(crashSds);
        var document = new TranslokatorDocumentAdapter(table, crashSds, twin, twinArchive);
        return CrashPlacements.Build(fr, document);
    }

    /// <summary>
    /// The other season's table for a crash archive: <c>city_crash</c> ↔ <c>city_crash_z</c>. Both ship the same
    /// placements, so loading the twin here — on the background loader, where extracting it costs no interaction —
    /// is what lets an edit be applied to both seasons at once. Best-effort: an archive with no twin (Sicily), or
    /// one that cannot be extracted, simply edits its own season.
    /// </summary>
    private static (TranslokatorLoader? Table, FileInfo? Archive) LoadTwinTable(FileInfo crashSds)
    {
        string stem = Path.GetFileNameWithoutExtension(crashSds.Name);
        string twinStem = stem.EndsWith("_z", StringComparison.OrdinalIgnoreCase) ? stem[..^2] : stem + "_z";
        var twinArchive = new FileInfo(Path.Combine(crashSds.DirectoryName!, twinStem + ".sds"));
        if (!twinArchive.Exists) return (null, null);

        try
        {
            string twinExtracted = EnsureExtracted(twinArchive);
            string? twinTra = SdsTranslokatorSaver.ResolvePath(twinExtracted);
            return twinTra == null ? (null, null) : (new TranslokatorLoader(new FileInfo(twinTra)), twinArchive);
        }
        catch
        {
            return (null, null);
        }
    }

    // Builds tree roots from FrameResource. instanceMap (if provided) marks prototype meshes as instanced.
    private static List<SdsFrameNode> BuildRoots(FrameResource fr, SceneDocumentAdapter document,
        IReadOnlyCollection<string> others,
        List<MeshData> meshes, IReadOnlyDictionary<FrameObjectSingleMesh, CrashPlacements.Cloud>? instanceMap,
        int lod)
    {
        var roots = new List<SdsFrameNode>();

        var objs = new List<FrameObjectBase>();
        foreach (var pair in fr.FrameObjects)
            if (pair.Value is FrameObjectBase o) objs.Add(o);

        // Take the child lists the loader already built. FrameObjectBase.Children is the union of the frames
        // linked through ParentIndex1 (the hierarchy parent) and ParentIndex2 (the anchor), which is the same set
        // the reference toolkit walks. Deriving it from o.Parent alone loses every anchor-linked frame — those
        // then match the "no parent" test below and float to the top of the tree.
        var childrenOf = new Dictionary<FrameObjectBase, List<FrameObjectBase>>();
        foreach (FrameObjectBase o in objs)
        {
            if (o.Children.Count > 0) childrenOf[o] = new List<FrameObjectBase>(o.Children);
        }

        var claimed = new HashSet<FrameObjectBase>();

        // Real scene folders (FrameHeaderScene) — top level of the hierarchy; their children are the scene's root objects.
        if (fr.FrameScenes != null)
        {
            foreach (FrameHeaderScene s in fr.FrameScenes.Values)
            {
                string sceneName = s.Name?.ToString() ?? "scene";
                var sn = new SdsFrameNode { Name = sceneName, Kind = "Scene", Source = new FrameSceneAdapter(s) };
                foreach (FrameObjectBase obj in s.Children)
                    if (claimed.Add(obj)) sn.Children.Add(BuildNode(obj, document, childrenOf, meshes, instanceMap, claimed, lod));
                if (sn.Children.Count > 0)
                {
                    sn.Category = CategorizeScene(sn, others);
                    roots.Add(sn);
                }
            }
        }

        // True top-level frames: both parent slots empty. An object that merely lacks a hierarchy parent is still
        // anchored somewhere (~35 % of the shipped game is exactly that shape), and hoisting it here is what made
        // an edited mesh appear to lose its parent.
        foreach (FrameObjectBase o in objs)
            if (o.ParentIndex1.Index < 0 && o.ParentIndex2.Index < 0 && claimed.Add(o))
                roots.Add(BuildNode(o, document, childrenOf, meshes, instanceMap, claimed, lod));

        // Anything still unplaced is anchored to something the walk above never reached — a malformed hierarchy.
        // Show it rather than dropping it silently, but keep it distinguishable from a genuine root.
        foreach (FrameObjectBase o in objs)
            if (claimed.Add(o))
            {
                SdsFrameNode orphan = BuildNode(o, document, childrenOf, meshes, instanceMap, claimed, lod);
                orphan.Name += "  (unanchored)";
                roots.Add(orphan);
            }

        return roots;
    }

    private static SdsFrameNode BuildNode(FrameObjectBase obj, SceneDocumentAdapter document,
        Dictionary<FrameObjectBase, List<FrameObjectBase>> childrenOf, List<MeshData> meshes,
        IReadOnlyDictionary<FrameObjectSingleMesh, CrashPlacements.Cloud>? instanceMap,
        HashSet<FrameObjectBase> claimed, int lod)
    {
        var node = new SdsFrameNode { Name = TreeName(obj), Kind = KindOf(obj), Source = document.Node(obj) };

        if (obj is FrameObjectSingleMesh sm && sm.Geometry != null)
        {
            CrashPlacements.Cloud cloud = default;
            instanceMap?.TryGetValue(sm, out cloud);
            Matrix4x4? placement = document.Placements.For(sm);
            int levels = sm.Geometry.LOD?.Length ?? 0;

            if (levels > 1)
            {
                // Every level, so the tree can offer them as rows. A level that will not decode ends the
                // list rather than leaving a gap in it.
                for (int level = 0; level < levels; level++)
                {
                    MeshData? each = TryConvert(sm, cloud.Matrices, cloud.DrawDistances, placement, level);
                    if (each == null) break;
                    node.LodMeshes.Add(each);
                }
            }

            if (node.LodMeshes.Count > 1)
            {
                // Only the finest goes into the flat mesh list: that list answers "how big is this scene and
                // where is it", and counting one object once per level would weigh a car's far silhouette as
                // a second car.
                meshes.Add(node.LodMeshes[0]);
            }
            else
            {
                // One usable level (nearly every district mesh): geometry stays on the frame's own row.
                node.LodMeshes.Clear();
                MeshData? md = TryConvert(sm, cloud.Matrices, cloud.DrawDistances, placement, lod);
                if (md != null) { node.Mesh = md; meshes.Add(md); }
            }
        }

        // A skinned model brings its rig with it. For a car the bones ARE the parts — doors, covers, axles,
        // a deform_ bone per panel — and the archive's collision hulls and interaction points hang off them,
        // so this is the map of what the object is made of, not a rendering detail.
        if (obj is FrameObjectModel model) node.Skeleton = TryReadSkeleton(model, document);

        // Claim descendants as we go: the caller uses the same set to decide what is still unplaced, and a
        // malformed hierarchy can otherwise reach the same frame twice (claimed also breaks any cycle).
        if (childrenOf.TryGetValue(obj, out List<FrameObjectBase>? kids))
            foreach (FrameObjectBase k in kids)
                if (claimed.Add(k))
                    node.Children.Add(BuildNode(k, document, childrenOf, meshes, instanceMap, claimed, lod));

        return node;
    }

    /// <summary>
    /// The rig of a skinned model: bone names, who hangs off whom, and where each bone sits.
    /// <para>
    /// The rest transforms are taken as MODEL space and used as they are. The format offers three readings of
    /// the same data and names none of them plainly; measured on the corpus, a car's <c>axleFL</c> and
    /// <c>axleFR</c> come out mirrored about X and inside the body's own bounding box, while the skeleton's
    /// <c>WorldTransforms</c> do not even invert (NaN). Accumulating the rest transforms down the hierarchy
    /// would place every bone twice. See <c>--probe-cars</c>, which prints all three readings side by side.
    /// </para>
    /// Best-effort: a model whose skeleton block is missing or mis-indexed simply has no rig to show.
    /// </summary>
    private static SkeletonData? TryReadSkeleton(FrameObjectModel model, SceneDocumentAdapter document)
    {
        try
        {
            FrameSkeleton skeleton = model.GetSkeletonObject();
            HashName[] names = skeleton.BoneNames ?? [];
            Matrix4x4[] rest = model.RestTransform ?? [];
            if (names.Length == 0 || rest.Length == 0) return null;

            byte[] parents = model.GetSkeletonHierarchyObject().ParentIndices ?? [];
            int count = Math.Min(names.Length, rest.Length);
            var bones = new List<BoneData>(count);
            for (int i = 0; i < count; i++)
            {
                // A bone whose parent is itself (the root's own convention) has no line to draw to.
                int parent = i < parents.Length ? parents[i] : -1;
                if (parent == i || parent >= count) parent = -1;
                bones.Add(new BoneData(names[i].ToString() ?? "?", parent, rest[i], document.Bone(model, i)));
            }

            // What hangs off those bones. The attached frame's world is already joint-aware — the frame
            // hierarchy places it through the joint (see FrameObjectBase.AttachedTo), so nothing is recomputed
            // here and the tree, the overlay and a gizmo drag can never disagree about where it is.
            var attachments = new List<BoneAttachment>();
            foreach (FrameObjectModel.AttachmentReference a in model.AttachmentReferences ?? [])
            {
                if (a.Attachment is not { } frame || a.JointIndex >= count) continue;
                string type = frame.GetType().Name;
                if (type.StartsWith("FrameObject", StringComparison.Ordinal)) type = type[11..];
                attachments.Add(new BoneAttachment(
                    a.JointIndex, frame.Name?.ToString() ?? "?", type, frame.WorldTransform, document.Node(frame)));
            }

            return new SkeletonData
            {
                OwnerName = model.Name?.ToString() ?? "?",
                Bones = bones,
                World = model.WorldTransform,
                Attachments = attachments,
                InverseBind = InverseBindOf(model, count),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Scene category for streaming filters, from the majority season class of its mesh leaves (FrameNameTable
    // flags, cascaded to unflagged children — see ClassifyNode):
    //  Proxy — most meshes are proxies (cityNN / neighbor-district / LOD backdrops);
    //  Snow  — most meshes are winter geometry (flag_1|flag_2);
    //  otherwise Normal.
    private static string CategorizeScene(SdsFrameNode scene, IReadOnlyCollection<string> others)
    {
        if ((scene.Name ?? "").Contains("proxy", StringComparison.OrdinalIgnoreCase)) return "Proxy";

        int total = 0, proxy = 0, snow = 0;
        CountScene(scene, others, ref total, ref proxy, ref snow, SeasonClass.Normal);
        if (total == 0) return "Normal";
        if (proxy * 2 >= total) return "Proxy";
        if (snow * 2 >= total) return "Snow";
        return "Normal";
    }

    private static void CountScene(SdsFrameNode n, IReadOnlyCollection<string> others,
        ref int total, ref int proxy, ref int snow, SeasonClass inherited)
    {
        SeasonClass cls = ClassifyNode(n, others, inherited);
        if (n.Mesh != null)
        {
            total++;
            if (cls == SeasonClass.Proxy) proxy++;
            else if (cls == SeasonClass.Snow) snow++;
        }
        foreach (SdsFrameNode c in n.Children) CountScene(c, others, ref total, ref proxy, ref snow, cls);
    }

    private enum SeasonClass { Normal, Proxy, Snow }

    // FrameNameTable flag semantics (verified across every Mafia II district via --probe-flags/--probe-flagtree):
    //   normal      = no flags        (value 0)
    //   snow/winter = flag_1 | flag_2 (value 3)      — z-prefixed winter geometry (its own scene4XX folder)
    //   proxy       = any other non-zero combination — cityNN (flag_2) plus neighbor-district / LOD proxies,
    //                 which carry assorted flag_1|256|512|1024|2048|4096 bits.
    // Flags live on the NAMED (frame-name-table) objects; a proxy/snow group's mesh children are NOT on the
    // table and carry no flag, so they inherit their nearest flagged ancestor's class (cascade). Objects that
    // are on the table are authoritative by their own flag. Objects with no flagged ancestor (interiors, stray
    // nodes) fall through to the legacy name heuristic.
    private const int SnowFlags = 3;   // flag_1 | flag_2

    private static SeasonClass ClassifyNode(SdsFrameNode n, IReadOnlyCollection<string> others, SeasonClass inherited)
    {
        if (n.Source is IFrameNode o && o.IsOnNameTable)
        {
            int f = o.NameTableFlags;
            return f == 0 ? SeasonClass.Normal : f == SnowFlags ? SeasonClass.Snow : SeasonClass.Proxy;
        }
        // Not on the frame name table: inherit the ancestor's class; only when there is no flagged ancestor
        // (still Normal) does the name heuristic get to upgrade a stray proxy/snow node (legacy fallback).
        if (inherited != SeasonClass.Normal) return inherited;
        string nm = n.Name ?? "";
        if (IsProxyMesh(nm, others)) return SeasonClass.Proxy;
        if (IsSnowMesh(nm)) return SeasonClass.Snow;
        return SeasonClass.Normal;
    }

    // Proxy mesh: proxy_… / cityNN / <neighbor district name>+digit (chinatown900, uppertown18…).
    private static bool IsProxyMesh(string nm, IReadOnlyCollection<string> others)
    {
        if (nm.Contains("proxy", StringComparison.OrdinalIgnoreCase)) return true;
        if (StartsWithNameThenDigit(nm, "city")) return true;
        foreach (string d in others)
            if (StartsWithNameThenDigit(nm, d)) return true;
        return false;
    }

    // Snow: name starts with z/Z, then a digit (z10_64_…).
    private static bool IsSnowMesh(string nm) =>
        nm.Length >= 2 && (nm[0] == 'z' || nm[0] == 'Z') && char.IsDigit(nm[1]);

    private static bool StartsWithNameThenDigit(string nm, string name) =>
        nm.Length > name.Length
        && nm.StartsWith(name, StringComparison.OrdinalIgnoreCase)
        && char.IsDigit(nm[name.Length]);

    // Object type = class name without the FrameObject prefix (SingleMesh→Mesh). No switch-by-type —
    // that has a trap of unreachable patterns due to inheritance (Area:Joint, Frame:Joint, etc.).
    /// <summary>
    /// What the scene tree calls a frame — its name, or what it IS when it has none.
    ///
    /// <para>
    /// A car's grouping holders carry no name at all, and asking the vendor <c>HashName</c> for a string
    /// gives one anyway: with an empty name it casts the 64-bit hash to <c>SkeletonBoneIDs</c>, a BONE-id
    /// enum, and stringifies that. For an unnamed holder the hash is 0 and the enum prints "0", so a car's
    /// hierarchy came out as a column of rows called "0" — a number that is neither a name nor an id of
    /// anything, and that reads as data rather than as the absence of it.
    /// </para>
    /// </summary>
    private static string TreeName(FrameObjectBase o) =>
        o.Name?.String is { Length: > 0 } named ? named : $"({KindOf(o).ToLowerInvariant()}, unnamed)";

    private static string KindOf(FrameObjectBase o)
    {
        string t = o.GetType().Name;
        if (t.StartsWith("FrameObject", StringComparison.Ordinal)) t = t.Substring(11);
        return t == "SingleMesh" ? "Mesh" : t;
    }

    /// <summary>
    /// The level this mesh answers for when the caller asks for <paramref name="lod"/>: the request
    /// clamped to what the mesh actually ships. Nearly every district mesh has a single level, so a
    /// viewport switched to LOD1 has to fall back to the coarsest one present rather than draw
    /// nothing — and every stage of a push has to agree about which level that was.
    /// </summary>
    public static int ClampLod(FrameObjectSingleMesh mesh, int lod)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        int count = mesh.Geometry?.LOD?.Length ?? 0;
        return count == 0 ? 0 : Math.Clamp(lod, 0, count - 1);
    }

    /// <summary>
    /// Full-fidelity decode of one level: float channels plus the raw packed bytes and quantization
    /// parameters. Shared by the viewport conversion below and the Blender bridge exporter; null for
    /// a mesh without usable buffers. Public for the bridge and the diagnostics probes.
    /// <para>
    /// The quantization parameters are the GEOMETRY's, not the level's — every LOD of one geometry
    /// block is packed against the same offset and factor, which is why re-quantizing for one of them
    /// has to re-pack the others too.
    /// </para>
    /// </summary>
    public static DecodedMesh? DecodeLod(FrameObjectSingleMesh mesh, int lod)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        FrameGeometry geom = mesh.Geometry;
        if (geom?.LOD == null || geom.LOD.Length == 0)
        {
            return null;
        }

        int level = ClampLod(mesh, lod);
        FrameLOD lodBlock = geom.LOD[level];
        var vertexBuffer = mesh.GetVertexBuffer(level);
        var indexBuffer = mesh.GetIndexBuffer(level);
        if (vertexBuffer?.Data == null || indexBuffer == null)
        {
            return null;
        }

        lodBlock.GetVertexOffsets(out int stride);
        int numVerts = lodBlock.NumVerts;
        if (stride <= 0 || numVerts <= 0 || (long)numVerts * stride > vertexBuffer.Data.Length)
        {
            return null;
        }

        byte[] raw = new byte[numVerts * stride];
        Array.Copy(vertexBuffer.Data, raw, raw.Length);

        var positions = new Vector3[numVerts];
        var normals = new Vector3[numVerts];
        var uvs = new Vector2[numVerts];
        // Tangent frame is only present when the vertex declaration advertises it; otherwise the shader
        // falls back to the vertex normal (flat-normal path), so we leave these null.
        bool hasTangent = lodBlock.VertexDeclaration.HasFlag(VertexFlags.Tangent);
        Vector3[]? tangents = hasTangent ? new Vector3[numVerts] : null;
        Vector3[]? binormals = hasTangent ? new Vector3[numVerts] : null;
        // Straight into the channel arrays: no wire, no Vertex per vertex. A district is millions
        // of vertices, and the full-fidelity path allocated one object per vertex plus a ~124-byte
        // wire record for a 16-20 byte packed vertex — enough LOH churn to stall the render thread.
        // (Binormals already carry the handedness sign applied by the decoder.)
        VertexTranslator.DecompressChannels(
            raw, numVerts, lodBlock.VertexDeclaration, geom.DecompressionOffset, geom.DecompressionFactor,
            positions, normals, uvs, tangents, binormals);

        // The skin, when there is one. This goes through the full-fidelity decode rather than the channel
        // path above, which has no skin outputs — a second pass over the buffer, but only for skinned models,
        // and those are single objects (a car, a character), never a district's millions of vertices.
        byte[]? boneIndices = null;
        float[]? boneWeights = null;
        if (lodBlock.VertexDeclaration.HasFlag(VertexFlags.Skin))
        {
            Vertex[] full = VertexTranslator.DecompressBuffer(
                raw, numVerts, lodBlock.VertexDeclaration, geom.DecompressionOffset, geom.DecompressionFactor);
            boneIndices = new byte[numVerts * 4];
            boneWeights = new float[numVerts * 4];
            for (int i = 0; i < numVerts; i++)
            {
                for (int k = 0; k < 4; k++)
                {
                    boneIndices[(i * 4) + k] = full[i].BoneIDs[k];
                    boneWeights[(i * 4) + k] = full[i].BoneWeights[k];
                }
            }
        }

        return new DecodedMesh
        {
            Frame = mesh,
            Lod = level,
            Declaration = lodBlock.VertexDeclaration,
            Stride = stride,
            NumVerts = numVerts,
            DecompressionOffset = geom.DecompressionOffset,
            DecompressionFactor = geom.DecompressionFactor,
            RawVertexData = raw,
            Positions = positions,
            Normals = normals,
            UVs = uvs,
            Tangents = tangents,
            Binormals = binormals,
            Indices = indexBuffer.GetData(),
            BoneIndices = boneIndices,
            BoneWeights = boneWeights,
        };
    }

    /// <summary>The maximum-detail level — <see cref="DecodeLod"/> at zero. Kept as its own entry point
    /// for the callers that are about the mesh itself rather than about what the viewport is showing.</summary>
    public static DecodedMesh? DecodeLod0(FrameObjectSingleMesh mesh) => DecodeLod(mesh, 0);

    /// <summary>
    /// Render-ready geometry for one frame at one level — what the viewport needs to redraw a mesh at a
    /// different level of detail without reloading its archive. The world transform is the FRAME's own; a
    /// caller holding a placement-aware matrix (an actor-placed prototype, a translokator copy) keeps it by
    /// carrying it over to the new GPU mesh.
    /// </summary>
    public static MeshData? BuildMeshData(FrameObjectSingleMesh mesh, int lod) => TryConvert(mesh, lod: lod);

    // Internal for the frame duplicator, which needs a render-ready MeshData for a freshly cloned object.
    internal static MeshData? TryConvert(FrameObjectSingleMesh mesh, Matrix4x4[]? instances = null,
        float[]? drawDistances = null, Matrix4x4? placement = null, int lod = 0)
    {
        try
        {
            DecodedMesh? decoded = DecodeLod(mesh, lod);
            if (decoded == null)
            {
                return null;
            }

            MeshPart[] parts = BuildParts(mesh, decoded.Indices.Length, decoded.Lod);

            // An actor-placed mesh carries an identity matrix of its own — the actor pack holds where it
            // stands (see ActorPlacements), so the placement goes in front of the frame's own world transform.
            //
            // An INSTANCED mesh is a different animal and gets none of this. A translokator copy's matrix is
            // already an absolute world placement (CrashPlacements.CloudFor: the row's own offset times the
            // .tra record — the format has no parent at all), so composing it with an actor placement adds two
            // unrelated world transforms together and scatters the whole row. It bites for real: city_crash.sds
            // ships its own actor packs which claim the very prototypes the .tra table instances. Picking and
            // the selection outline build their matrices straight from the table, so folding here would also
            // put the geometry somewhere the ray never looks.
            // The test matches GpuMesh's own "is this a cloud" rule, so the two never disagree about which
            // matrix moves the geometry.
            Matrix4x4 place = instances is { Length: > 0 } ? Matrix4x4.Identity : placement ?? Matrix4x4.Identity;

            // A skinned model's bone ids only mean something once the per-face-group remap is applied.
            var skin = SkinOf(mesh, decoded, parts);

            return new MeshData
            {
                Name = mesh.Name?.ToString() ?? "mesh",
                Lod = decoded.Lod,
                World = mesh.WorldTransform * place,
                Positions = decoded.Positions,
                Normals = decoded.Normals,
                UVs = decoded.UVs,
                Tangents = decoded.Tangents,
                Binormals = decoded.Binormals,
                Indices = decoded.Indices,
                Parts = parts,
                Instances = instances,
                InstanceDrawDistances = drawDistances,
                BoneIndices = skin.Indices,
                BoneWeights = skin.Weights,
                Skeleton = skin.Rig,
                // The document's OWN array, so a bone moved anywhere is seen here without being announced.
                LiveRest = skin.LiveRest,
            };
        }
        catch
        {
            // Skip a broken/non-standard mesh, don't crash the rest of the scene.
            return null;
        }
    }

    /// <summary>
    /// Which bone of the model's OWN list each vertex is weighted to, four per vertex — the reading the
    /// renderer uses, and the only one that answers "which bone does this part ride" out loud.
    /// <para>
    /// Reading the ids straight out of the vertex buffer names the wrong bone: they are pool-local, an index
    /// into the remap pool of whichever face group draws the vertex. Null for a model with no usable skin.
    /// </para>
    /// </summary>
    public static byte[]? GlobalBoneIds(FrameObjectModel model, int lod = 0)
    {
        ArgumentNullException.ThrowIfNull(model);
        DecodedMesh? decoded = DecodeLod(model, lod);
        return decoded == null
            ? null
            : ResolveBoneRemap(model, BuildParts(model, decoded.Indices.Length, decoded.Lod), decoded);
    }

    /// <summary>
    /// WHY a model's skin does or does not resolve, in words. <see cref="GlobalBoneIds"/> answers null for
    /// half a dozen different reasons and they are not interchangeable: a mesh that lost its skin channel, a
    /// remap table with fewer groups than the mesh has materials, and an id pointing past the table are three
    /// different bugs. An unresolvable skin is never cosmetic — the renderer, the bridge's export and the
    /// game all read through this, and the bridge quietly falls back to the RAW pool-local ids, which is how
    /// a vertex group ends up labelled "bumper" while it selects a window.
    /// </summary>
    public static string DescribeBoneRemap(FrameObjectModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DecodedMesh? decoded = DecodeLod0(model);
        if (decoded == null) return "the mesh has no usable LOD0 buffers";
        if (decoded.BoneIndices == null || decoded.BoneWeights == null)
        {
            return "the vertex buffer carries no skin channel at all";
        }

        FrameBlendInfo blend;
        try { blend = model.GetBlendInfoObject(); }
        catch (Exception) { return "the blend info cannot be read"; }
        if (blend.BoneIndexInfos is not { Length: > 0 } lods) return "the blend info carries no LODs";

        FrameBlendInfo.BoneIndexInfo info = lods[Math.Clamp(decoded.Lod, 0, lods.Length - 1)];
        byte[] pools = info.BonesPerRemapPool ?? [];
        byte[] remap = info.BoneRemapIDs ?? [];
        FrameBlendInfo.SkinnedMaterialInfo[] groups = info.SkinnedMaterialInfo ?? [];
        MeshPart[] parts = BuildParts(model, decoded.Indices.Length, decoded.Lod);

        var text = new System.Text.StringBuilder();
        text.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"{parts.Length} material parts, {groups.Length} skinned-material groups, {pools.Length} pools "
            + $"({string.Join("+", pools)} = {remap.Length} remap entries)");
        if (pools.Length == 0) return text + " — NO POOLS";
        if (remap.Length == 0) return text + " — EMPTY REMAP TABLE";
        if (groups.Length < parts.Length)
        {
            return text + " — FEWER GROUPS THAN PARTS: the mesh draws materials the remap table cannot answer for";
        }

        int at = 0;
        var poolStart = new int[pools.Length];
        for (int p = 0; p < pools.Length; p++) { poolStart[p] = at; at += pools[p]; }
        if (at > remap.Length) return text + " — POOLS OVERRUN THE REMAP TABLE";

        for (int part = 0; part < parts.Length; part++)
        {
            int pool = groups[part].AssignedPoolIndex;
            if (pool >= pools.Length)
            {
                return text + $" — part {part} names pool {pool}, which does not exist";
            }
            int end = Math.Min(parts[part].StartIndex + parts[part].IndexCount, decoded.Indices.Length);
            for (int i = parts[part].StartIndex; i < end; i++)
            {
                int vertex = (int)decoded.Indices[i];
                if (vertex < 0 || (vertex * 4) + 3 >= decoded.BoneIndices.Length) continue;
                for (int k = 0; k < 4; k++)
                {
                    int slot = poolStart[pool] + decoded.BoneIndices[(vertex * 4) + k];
                    if (slot >= remap.Length)
                    {
                        return text + $" — vertex {vertex} names id "
                            + $"{decoded.BoneIndices[(vertex * 4) + k]} in pool {pool} (size {pools[pool]}), "
                            + "which is past the end of the remap table";
                    }
                }
            }
        }
        // How many influences each face group tells the game to blend, and out of which pool. The game reads
        // this per material, not per vertex: a vertex carrying two influences inside a group that declares
        // one is a vertex whose second bone the game never applies — and a group that declares more than its
        // vertices carry blends bytes that mean nothing.
        // WHICH bones each pool can name. A pool is a palette a draw can reach into, and a material can only
        // weight its vertices to bones its own pool holds — so "this bone is not in that pool" is a complete
        // explanation for a part that will not take a weight, and there is no other way to see it.
        HashName[] rigBones = model.GetSkeletonObject().BoneNames ?? [];
        string BoneName(byte id) => id < rigBones.Length ? rigBones[id].ToString() ?? $"#{id}" : $"#{id}";
        for (int p = 0; p < pools.Length; p++)
        {
            if (pools[p] == 0) continue;
            text.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"\n        pool {p} ({pools[p]} bones): ");
            text.Append(string.Join(", ",
                Enumerable.Range(poolStart[p], pools[p]).Select(i => BoneName(remap[i]))));
        }

        text.Append("\n        per material: ");
        for (int part = 0; part < parts.Length; part++)
        {
            int carried = 0;
            int end = Math.Min(parts[part].StartIndex + parts[part].IndexCount, decoded.Indices.Length);
            for (int i = parts[part].StartIndex; i < end; i++)
            {
                int vertex = (int)decoded.Indices[i];
                if (vertex < 0 || (vertex * 4) + 3 >= decoded.BoneWeights.Length) continue;
                int here = 0;
                for (int k = 0; k < 4; k++)
                {
                    if (decoded.BoneWeights[(vertex * 4) + k] > 0f) here++;
                }
                carried = Math.Max(carried, here);
            }
            text.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"[{part}: pool {groups[part].AssignedPoolIndex}, declares "
                + $"{groups[part].NumWeightsPerVertex}, carries {carried}]");
        }
        return text.ToString();
    }

    /// <summary>
    /// Turns a skinned mesh's per-vertex bone ids into indices of the model's OWN bone list.
    /// <para>
    /// On the wire they are not that. Each LOD carries a set of remap pools — a flat table of global bone
    /// indices cut into slices by <c>BonesPerRemapPool</c> — and a vertex's id indexes the slice belonging to
    /// the face group it is drawn in. Face groups are the LOD0 material splits, in the same order as
    /// <paramref name="parts"/>. Measured on the corpus by <c>--probe-skinning</c>: every id resolves, and
    /// pools really are used (a car splits its body across two).
    /// </para>
    /// Null when there is nothing to resolve, or when the blend info does not line up with the mesh — a
    /// half-remapped skin would put triangles on the wrong bones, which is worse than not skinning at all.
    /// </summary>
    internal static byte[]? ResolveBoneRemap(FrameObjectModel model, MeshPart[] parts, DecodedMesh decoded)
    {
        if (decoded.BoneIndices is not { } ids || decoded.BoneWeights == null) return null;

        FrameBlendInfo blend;
        try { blend = model.GetBlendInfoObject(); }
        catch (Exception) { return null; }
        if (blend.BoneIndexInfos is not { Length: > 0 } lods) return null;

        // The pools are per LOD, like the material splits they are indexed by: reading LOD0's table for a
        // coarser level names bones out of the wrong palette, which puts triangles on the wrong bones.
        FrameBlendInfo.BoneIndexInfo info = lods[Math.Clamp(decoded.Lod, 0, lods.Length - 1)];
        byte[] pools = info.BonesPerRemapPool ?? [];
        byte[] remap = info.BoneRemapIDs ?? [];
        var groups = info.SkinnedMaterialInfo ?? [];
        if (pools.Length == 0 || remap.Length == 0 || groups.Length < parts.Length) return null;

        // Where each pool's slice starts in the flat remap table.
        var poolStart = new int[pools.Length];
        int at = 0;
        for (int p = 0; p < pools.Length; p++) { poolStart[p] = at; at += pools[p]; }
        if (at > remap.Length) return null;

        var resolved = new byte[ids.Length];
        Array.Copy(ids, resolved, ids.Length);
        var claimed = new bool[decoded.Positions.Length];

        for (int part = 0; part < parts.Length; part++)
        {
            int pool = groups[part].AssignedPoolIndex;
            if (pool >= pools.Length) return null;

            int end = Math.Min(parts[part].StartIndex + parts[part].IndexCount, decoded.Indices.Length);
            for (int i = parts[part].StartIndex; i < end; i++)
            {
                int vertex = (int)decoded.Indices[i];
                // A vertex shared between two groups is remapped once, by the first group that draws it —
                // remapping twice would resolve an already-global index a second time.
                if (vertex < 0 || vertex >= claimed.Length || claimed[vertex]) continue;
                claimed[vertex] = true;

                for (int k = 0; k < 4; k++)
                {
                    int slot = poolStart[pool] + ids[(vertex * 4) + k];
                    if (slot >= remap.Length) return null;
                    resolved[(vertex * 4) + k] = remap[slot];
                }
            }
        }

        return resolved;
    }

    /// <summary>
    /// The four fields that make a <see cref="MeshData"/> skinned, for a frame that is a skinned model —
    /// resolved bone ids, their weights, the rig, and the LIVE rest-transform array the renderer reads every
    /// frame. All null for anything else.
    /// <para>
    /// Shared because forgetting it is not hypothetical: a mesh rebuilt after a Blender push once came back
    /// without any of it, and a body that quietly stops being skinned looks exactly like a bone that moves
    /// while the geometry stays put — through the gizmo, through undo and through every later push, for the
    /// rest of the session.
    /// </para>
    /// </summary>
    /// <summary>The model's rig, without its attachment list — what a rebuilt mesh needs to stay skinned.</summary>
    internal static SkeletonData? RigOf(FrameObjectModel model) => TryReadSkeletonBones(model);

    internal static (byte[]? Indices, float[]? Weights, SkeletonData? Rig, IReadOnlyList<Matrix4x4>? LiveRest)
        SkinOf(FrameObjectSingleMesh mesh, DecodedMesh decoded, MeshPart[] parts)
    {
        if (mesh is not FrameObjectModel model) return (null, null, null, null);
        byte[]? indices = ResolveBoneRemap(model, parts, decoded);
        if (indices == null) return (null, null, null, null);
        return (indices, decoded.BoneWeights, TryReadSkeletonBones(model), model.RestTransform);
    }

    /// <summary>
    /// The pose the geometry was skinned in, from the skeleton's own table. Measured on the corpus
    /// (<c>--probe-skinning</c>): <c>WorldTransforms[i]</c> is the inverse of bone i's rest transform, on 82
    /// of 83 bones — and unlike the rest table, the editor never rewrites it, which is exactly why it is the
    /// one to read. Falls back to inverting the rest for a bone the table does not cover.
    /// </summary>
    private static Matrix4x4[] InverseBindOf(FrameObjectModel model, int count)
    {
        Matrix4x4[] table;
        try { table = model.GetSkeletonObject().WorldTransforms ?? []; }
        catch (Exception) { table = []; }

        Matrix4x4[] rest = model.RestTransform ?? [];
        var result = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            if (i < table.Length && Affine(table[i]) is { } bind && bind.GetDeterminant() != 0)
            {
                result[i] = bind;
            }
            else
            {
                result[i] = i < rest.Length && Matrix4x4.Invert(Affine(rest[i]), out Matrix4x4 inverse)
                    ? inverse
                    : Matrix4x4.Identity;
            }
        }
        return result;
    }

    // A frame matrix rides as 4x3: its fourth column is not (0,0,0,1) until it is put there, and until then
    // it neither inverts nor multiplies correctly.
    private static Matrix4x4 Affine(Matrix4x4 m)
    {
        m.M14 = 0;
        m.M24 = 0;
        m.M34 = 0;
        m.M44 = 1;
        return m;
    }

    /// <summary>The rig a skinned mesh is bound to, without the attachment list — geometry does not need it,
    /// and building it here would duplicate work the scene tree already does.</summary>
    private static SkeletonData? TryReadSkeletonBones(FrameObjectModel model)
    {
        try
        {
            HashName[] names = model.GetSkeletonObject().BoneNames ?? [];
            Matrix4x4[] rest = model.RestTransform ?? [];
            if (names.Length == 0 || rest.Length == 0) return null;

            byte[] parents = model.GetSkeletonHierarchyObject().ParentIndices ?? [];
            int count = Math.Min(names.Length, rest.Length);
            var bones = new List<BoneData>(count);
            for (int i = 0; i < count; i++)
            {
                int parent = i < parents.Length ? parents[i] : -1;
                if (parent == i || parent >= count) parent = -1;
                bones.Add(new BoneData(names[i].ToString() ?? "?", parent, rest[i]));
            }

            return new SkeletonData
            {
                OwnerName = model.Name?.ToString() ?? "?",
                Bones = bones,
                World = model.WorldTransform,
                Attachments = [],
                InverseBind = InverseBindOf(model, count),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Split the mesh indices into ranges by material and resolve the diffuse texture. Each level owns its
    // own slot list — a car body draws 7 materials up close and 3 far away — so the level has to be the
    // one the geometry came from, or the ranges address triangles that are not there.
    // Internal for the bridge applier, which rebuilds a MeshData after a geometry push.
    internal static MeshPart[] BuildParts(FrameObjectSingleMesh mesh, int indexCount, int lod = 0)
    {
        FrameMaterial fm = mesh.Material;
        int level = ClampLod(mesh, lod);
        if (fm?.Materials != null && level < fm.Materials.Count
            && fm.Materials[level] != null && fm.Materials[level].Length > 0)
        {
            MaterialStruct[] mats = fm.Materials[level];
            var parts = new MeshPart[mats.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                var tex = MafiaMaterials.GetMaterialTextures(mats[i].MaterialHash);
                parts[i] = new MeshPart(mats[i].StartIndex, mats[i].NumFaces * 3, tex.Diffuse, tex.Normal, tex.Specular,
                    mats[i].MaterialHash, tex.Tint, tex.Blended);
            }
            return parts;
        }

        // No material table — draw the whole mesh as one part without a texture.
        return new[] { new MeshPart(0, indexCount, null) };
    }
}
