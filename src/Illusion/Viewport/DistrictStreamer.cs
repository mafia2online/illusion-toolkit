using System.Diagnostics;
using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Assets.Collisions;
using Illusion.Assets.Frames;
using Illusion.Assets.Library;
using Illusion.Assets.Sds;
using Illusion.Assets.World;
using Illusion.Domain;
using Illusion.Formats.Actors;
using Illusion.Formats.Collisions;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Translokator;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Scene;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// The viewport's district loading and streaming pipeline: the .sds load queue, the background
/// extract→parse→GPU-prepare task, the time-budgeted attach of prepared meshes, camera streaming over
/// AREA zones (Whole map mode), the additive city_crash layer and the season ground swap. The heavy
/// work is UI-thread-free (the D3D11 device is free-threaded); only the attach runs on the UI thread,
/// a few milliseconds per frame.
/// </summary>
internal sealed class DistrictStreamer
{
    private readonly D3DImageHost _host;

    public DistrictStreamer(D3DImageHost host)
    {
        _host = host;
        Actors = new ActorLayer(host);
    }

    // Tracking key of the city_crash layer in _loadedDistricts: reuses the district machinery
    // (placeholder dedup, BeginBuild registration, UnloadDistrict teardown) for additive load/unload.
    private const string CrashLayerKey = "city_crash";
    private const float StreamMargin = 200f; // margin at zone borders so districts don't flicker at seams

    // Time-budgeted attach: prepared meshes join the render list a few ms per frame (each step is O(1)),
    // so even a huge district streams in without a frame hitch.
    private const double AttachBudgetMs = 3.0;

    /// <summary>city_crash layer toggle: spawn objects from the Translokator table (instances). Loads and
    /// unloads additively — toggling never resets the rest of the scene.</summary>
    public bool CrashEnabled;

    /// <summary>Collision overlay toggle: decode each resident district's Collisions (.col) and draw the hulls
    /// as a translucent, wireframe-edged, per-district layer. Additive — toggling never resets the scene.</summary>
    public bool CollisionEnabled;

    // Resident districts that carry a collision resource. The "Collisions" tree layer is grafted ALWAYS (so
    // placements are browsable/editable regardless of the overlay toggle); the translucent hull overlay is only
    // uploaded while the toggle is on (Rendered). The document is the .col save unit backing the layer.
    private readonly List<CollisionSource> _collisionSources = new();

    private sealed class CollisionSource
    {
        public required SceneNode Sds;                     // the SDS tree node (also the renderer key)
        public required CollisionDocumentAdapter Document; // the .col save unit backing the layer
        public required SceneNode Layer;                   // the "Collisions" tree node, always grafted under Sds
        public bool Rendered;                              // whether the hull overlay is currently uploaded
        public CollisionRenderData? Decoded;               // cached decoded geometry (for cheap instance-only re-upload on edit)
        public ulong CoverageAttemptKey;                   // mesh set a full re-decode was last attempted for (see LiveUpdateCollision)
    }

    // The crash archive's placement layer: one entry while city_crash is loaded. Unlike collision hulls the props
    // are drawn by the ordinary mesh pipeline (hardware-instanced prototypes), so there is no separate overlay —
    // an edit refreshes the affected prototype's copy matrices in place.
    private readonly List<CrashSource> _crashSources = new();

    private sealed class CrashSource
    {
        public required SceneNode Sds;                        // the SDS tree node this layer hangs under
        public required CrashPlacements Placements;           // table rows ↔ prototype meshes
        public required SceneNode Layer;                      // the "Crash objects" tree node
        // Prototype mesh → its tree leaf, so an edited row can find the GpuMesh whose copies must be re-uploaded.
        public required Dictionary<FrameObjectSingleMesh, SceneNode> Leaves;

        // Placement → its tree node, for the copies that have been materialised. The shipped city holds 57 652
        // of them; building a node (each with its own observable child collection) and an adapter for every one
        // up front is ~170 000 objects that stay live for the session, and every later gen2 collection walks
        // them. They are created on demand instead — when a copy is clicked, or when its row is expanded.
        public readonly Dictionary<Instance, SceneNode> Nodes = new();

        // Row → its tree node, so a placement can be materialised under the right parent without a lookup.
        public readonly Dictionary<Formats.Translokator.Object, SceneNode> RowNodes = new();
    }

    // Resident rigs, per SDS tree node — the source the bone overlay is rebuilt from after a bone is dragged.
    // Kept here rather than in the renderer because the renderer only knows finished line lists.
    private readonly Dictionary<SceneNode, List<SkeletonData>> _rigs = new();

    // Per-archive frame roots, kept so the helper layer can be rebuilt after an edit moves something, and the
    // archives whose helpers are waiting for that rebuild.
    private readonly Dictionary<SceneNode, IReadOnlyList<SdsFrameNode>> _helperRoots = new();
    private readonly HashSet<SceneNode> _helperDirty = new();

    // The same glyphs as click targets: tree node, where its glyph sits, and the world slack a click gets.
    private readonly Dictionary<SceneNode, List<(SceneNode Node, Vector3 At, float Radius)>> _helperPicks = new();

    // .sds load queue (single area / city_univers when streaming). One item at a time.
    private readonly Queue<(FileInfo File, string Label, string? District)> _loadQueue = new();
    private bool _hasFramedOnce; // frame the camera ONCE (first load), don't reset afterwards
    private bool _frameNextLoad; // …except on the library stage, where each resource put on it is framed

    // Camera streaming (Whole map mode): zones from city_univers → districts by camera position.
    private bool _streaming;
    private bool _winter;
    private readonly Dictionary<string, DistrictLoad> _loadedDistricts = new(StringComparer.OrdinalIgnoreCase);

    private sealed class DistrictLoad
    {
        public SceneNode? SdsNode;   // SDS node in the tree (under the folder)
        public SceneNode? Folder;    // parent folder (to remove when empty)
        public List<GpuMesh>? Meshes;
        public FileInfo? Archive;    // what to hand back to OpenArchives when this district goes
    }

    // Background-prepared load of one .sds: extraction, parsing, the detached SceneNode tree AND all
    // GPU resources (device-only creation is free-threaded) happen in one background task; the UI
    // thread only attaches the results. Null result = cancelled or failed (meshes already released).
    private sealed class PreparedLoad
    {
        public required SceneNode Sds;                                // fully built, not yet in Roots
        public required List<(SceneNode Leaf, GpuMesh Mesh)> Meshes;  // GPU resources already created
        public SceneNode? CollisionLayer;                            // the "Collisions" tree node (built for any district with a .col)
        public CollisionDocumentAdapter? CollisionDoc;               // the .col save unit backing the layer
        public CollisionRenderData? Collision;                       // decoded hull overlay, when the toggle was on at load
        public SceneNode? CrashLayer;                                // the "Crash objects" tree node (city_crash only)
        public CrashPlacements? Crash;                               // the .tra save unit + prototype correspondence
        public Dictionary<FrameObjectSingleMesh, SceneNode>? CrashLeaves; // prototype mesh → its tree leaf
        public IReadOnlyList<Vector3>? NavLines;                     // decoded .nov road graph (edge endpoint pairs), null if none
        public IReadOnlyList<Vector3>? NavMeshLines;                // decoded .nov AI-mesh box wireframe, null if none
        public List<SkeletonData>? Skeletons;                        // every skinned model's rig (the overlay is built from these)
        public IReadOnlyList<SdsFrameNode>? Roots;                   // the archive's frame roots (the helper layer is rebuilt from these)
        public HelperGlyphRenderData? Helpers;                       // dummies / points / volumes — the nodes nothing draws
        public IReadOnlyList<Vector3>? NavWorldLines;               // decoded .nav path-object boxes, null if none
        public ActorMarkerRenderData? ActorMarkers;                 // glyphs for the actors nothing draws, null if none
        public List<(SceneNode Node, Vector3 Position)>? ActorPickables; // those glyphs, tree nodes, for ray-picking
        public ActorPlacements? ActorPlacements;                          // which actor governs which frame
        public Dictionary<ActorEntry, SceneNode>? ActorNodes;             // actor → its tree node
        public Dictionary<FrameObjectBase, SceneNode>? MeshNodeByFrame;   // frame → its mesh leaf (outline lookup)
        public Dictionary<FrameObjectBase, SceneNode>? FrameNodeByFrame;  // frame → its tree row, holders included
    }

    /// <summary>
    /// The actors of every resident district: their glyphs, what a click is tested against, and the way from an
    /// actor to the geometry it places. Its own class because none of the streamer's mesh machinery reaches an
    /// actor — a glyph is not a GpuMesh, and an actor's geometry hangs in the FrameResource branch, not under it.
    /// </summary>
    public ActorLayer Actors { get; }

    private Task<PreparedLoad?>? _loadTask;
    private (string label, string? district, string folder, int gen, FileInfo file) _loadCtx;
    private int _loadGen; // scene generation: discard the result of a stale (post-reset) load
    private CancellationTokenSource? _loadCts; // cancels the in-flight background load

    private bool _building;

    /// <summary>Whether an archive is still on its way into the scene — queued, loading in the background, or
    /// having its meshes attached. What a caller that asked for an area waits on.</summary>
    public bool IsBusy => _building || _loadTask != null || _loadQueue.Count > 0;

    private Queue<(SceneNode Leaf, GpuMesh Mesh)> _buildQueue = null!; // prepared meshes awaiting attach
    private (string label, string? district, string folder, int gen, FileInfo file) _buildCtx;
    private List<GpuMesh> _buildMeshes = null!;

    // Per-frame scene advancement (before the base moves the camera): finish a background load into a
    // time-budgeted attach, kick the next queued .sds, then let streaming pick districts by position.
    public void Tick(float dt)
    {
        // Completed background load → attach its tree, then feed meshes to the renderer per frame.
        if (!_building && _loadTask != null && _loadTask.IsCompleted) BeginBuild();
        if (_building) AttachStep();

        // Glyphs hidden through the tree's eye: one coalesced rebuild per frame (see ActorLayer).
        Actors.RebuildDirty();
        // Helper glyphs of an archive whose frames just moved — likewise one rebuild per frame, since a
        // gizmo drag would otherwise rebuild the layer on every mouse move.
        RebuildDirtyHelpers();

        // The queue (single area / city_univers when streaming) has priority over streaming.
        if (!_building && _loadTask == null && _loadQueue.Count > 0)
        {
            (FileInfo file, string label, string? district) = _loadQueue.Dequeue();
            StartBackgroundLoad(file, label, district);
        }

        // Streaming populates the scene with districts by camera position (its load waits until the queue is empty).
        if (_streaming) StreamStep();

        // Repaint any collision district whose placements were just edited (live during a gizmo drag).
        LiveUpdateCollision();
        // …and the crash props, whose copies live in the instance buffers of their prototypes.
        LiveUpdateCrash();
    }

    // Ray-picks the nearest collision placement under a viewport ray (CPU): collision hulls are hardware-instanced
    // and live outside Renderer.Meshes, so the standard GpuMesh pick can't reach them. Only rendered sources are
    // tested (hidden overlay = not pickable). Returns the hit placement's tree node + its ray distance, or null.
    public SceneNode? PickCollision(Vector3 origin, Vector3 dir, out float bestT)
    {
        bestT = float.PositiveInfinity;
        SceneNode? best = null;
        foreach (CollisionSource src in _collisionSources)
        {
            if (!src.Rendered || src.Decoded == null) continue;

            var geom = new Dictionary<ulong, CollisionRenderMesh>(src.Decoded.Meshes.Length);
            foreach (CollisionRenderMesh m in src.Decoded.Meshes) geom[m.Hash] = m;

            List<CollisionInstance> instances = src.Document.Collision.Instances;
            var nodes = src.Layer.Children;
            int n = Math.Min(instances.Count, nodes.Count);
            for (int i = 0; i < n; i++)
            {
                CollisionInstance inst = instances[i];
                if (!geom.TryGetValue(inst.Hash, out CollisionRenderMesh? mesh)) continue;
                // The display scale has to be in here too, or a resized hull is picked at its old size.
                Matrix4x4 world = TransformMath.Compose(
                    TransformMath.CollisionEulerToQuaternion(inst.Rotation), src.Document.ScaleOf(inst), inst.Position);

                if (!InstanceAabbHit(origin, dir, mesh, world, out float tEnter) || tEnter > bestT) continue;

                Vector3[] pos = mesh.Positions;
                uint[] idx = mesh.Indices;
                for (int k = 0; k + 2 < idx.Length; k += 3)
                {
                    Vector3 a = Vector3.Transform(pos[idx[k]], world);
                    Vector3 b = Vector3.Transform(pos[idx[k + 1]], world);
                    Vector3 c = Vector3.Transform(pos[idx[k + 2]], world);
                    if (Picking.IntersectTriangle(origin, dir, a, b, c, out float t) && t < bestT)
                    {
                        bestT = t;
                        best = nodes[i];
                    }
                }
            }
        }
        return best;
    }

    // Broad phase: ray vs the placement's world AABB (the 8 local-AABB corners transformed by the instance world).
    private static bool InstanceAabbHit(Vector3 o, Vector3 d, CollisionRenderMesh mesh, Matrix4x4 world, out float tEnter)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int k = 0; k < 8; k++)
        {
            var corner = new Vector3(
                (k & 1) == 0 ? mesh.LocalMin.X : mesh.LocalMax.X,
                (k & 2) == 0 ? mesh.LocalMin.Y : mesh.LocalMax.Y,
                (k & 4) == 0 ? mesh.LocalMin.Z : mesh.LocalMax.Z);
            Vector3 wp = Vector3.Transform(corner, world);
            min = Vector3.Min(min, wp);
            max = Vector3.Max(max, wp);
        }
        return Picking.IntersectAabb(o, d, min, max, out tEnter);
    }

    // Pushes the current selection's collision placements to the renderer to highlight (bright overlay). Called on
    // selection change and each gizmo-drag frame, so the highlight tracks the drag. Only placements in a rendered
    // district contribute; a hidden overlay highlights nothing.
    public void UpdateCollisionSelection(IReadOnlyList<SceneNode> selected)
    {
        if (_host.Rnd == null) return;
        var highlights = new List<(object Key, ulong Hash, Matrix4x4 World)>();
        foreach (SceneNode n in selected)
        {
            if (n.Source is not CollisionInstanceAdapter ca) continue;
            foreach (CollisionSource src in _collisionSources)
            {
                if (src.Rendered && ReferenceEquals(src.Layer, n.Parent))
                {
                    highlights.Add((src.Sds, ca.Instance.Hash, ca.WorldTransform));
                    break;
                }
            }
        }
        _host.Rnd.SetCollisionSelection(highlights);
    }

    // Cheap per-frame collision repaint: for each source flagged RenderDirty (by a placement's transform setter),
    // rebuild ONLY the instance matrices from the current .col (reusing the cached decoded geometry) and rewrite
    // the instance buffers in place. Runs every frame, so a gizmo drag / numeric edit updates the hull live.
    private void LiveUpdateCollision()
    {
        if (_host.Rnd == null) return;
        foreach (CollisionSource src in _collisionSources)
        {
            if (!src.Document.RenderDirty) continue;
            src.Document.RenderDirty = false;
            if (!src.Rendered || src.Decoded == null) continue;

            // An edit can add a hull to the .col, not just move a placement. The cached decode would not
            // contain it, and RebuildInstances iterates the CACHE — so the new hull's placements would
            // vanish from the overlay, from picking and from the selection highlight, all of which read
            // this same cache. Decide that on COVERAGE alone: an edit that adds one hull and removes
            // another leaves the mesh count equal, so gating on a count change (as this once did) let the
            // added hull render from a stale cache indefinitely.
            //
            // The retry guard is the mesh SET, not the count: a placed hull whose blob cannot be decoded
            // never becomes covered, and RenderDirty is raised every frame of a gizmo drag — without a key
            // that changes only when the meshes do, that hull would force a full re-decode per frame.
            if (!CollisionSceneBuilder.CoversPlacedMeshes(src.Decoded, src.Document.Collision))
            {
                ulong key = MeshSetKey(src.Document.Collision);
                if (src.CoverageAttemptKey == key) continue;
                src.CoverageAttemptKey = key;

                try { src.Decoded = CollisionSceneBuilder.Build(src.Document.Collision, src.Document.ScaleOf); }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException) { }
                _host.Rnd.SetCollisionDistrict(
                    src.Sds, CollisionSceneBuilder.RebuildInstances(src.Decoded, src.Document.Collision, src.Document.ScaleOf));
                continue;
            }

            _host.Rnd.UpdateCollisionInstances(
                src.Sds, CollisionSceneBuilder.RebuildInstances(src.Decoded, src.Document.Collision, src.Document.ScaleOf));
        }
    }

    // Order-independent identity of a .col's mesh set, used only to avoid re-attempting a decode that
    // already failed to cover. Order-independent because a minted hull is inserted in hash order rather
    // than appended, which must not read as a different set on its own.
    private static ulong MeshSetKey(CollisionFile file)
    {
        ulong key = (ulong)file.Meshes.Count;
        foreach (CollisionMesh mesh in file.Meshes) key ^= mesh.Hash;
        return key;
    }

    /// <summary>
    /// "Whole map" (<paramref name="wholeMap"/>) → camera streaming mode: districts load/unload
    /// by camera position (AREA zones). Otherwise — load one selected area. Season swaps <c>_z</c>
    /// and ground textures ground_leto→ground_zima.
    /// </summary>
    public void LoadArea(MapArea? area, bool winter, bool wholeMap)
    {
        if (_host.Catalogs.Map == null) return; // catalogs still initializing — CatalogReady re-triggers the selection
        _winter = winter;

        if (wholeMap)
        {
            EnterStreaming();
            return;
        }

        _streaming = false;
        _loadedDistricts.Clear();
        AddSeasonGround(winter);

        var items = new List<(FileInfo File, string Label, string? District)>();
        if (area != null) items.Add((area.FileFor(winter), area.BaseName, null));
        AddCrashItem(items, winter);

        LoadSet(items);
    }

    /// <summary>
    /// Puts ONE archive on the viewport on its own — the resource library's stage. The district pipeline is
    /// reused whole (extract → parse → GPU prepare → budgeted attach, plus the collision and actor layers and
    /// the persistence bookkeeping); what is left out is everything that is about the CITY: no season ground,
    /// no crash layer, no <c>cityareas</c> row, no camera streaming. The archive needs no entry in any catalog,
    /// which is the point — the library reaches content the map selector never offered.
    /// <para>
    /// Unlike a district, every resource that lands frames the camera: the stage is looked at one thing at a
    /// time, and a car left off-screen because a district was framed an hour ago is not a stage.
    /// </para>
    /// </summary>
    public void LoadStage(FileInfo sds, string label)
    {
        _streaming = false;
        _winter = false;          // seasons are a city thing; the toggle stays for the map to read again
        _loadedDistricts.Clear(); // the stage holds one archive, replaced whole — nothing to unload piecemeal
        _frameNextLoad = true;
        LoadSet(new[] { (sds, label, (string?)null) });
    }

    // Adds city_crash to the load set (by season: _z — winter), if the layer is enabled. The layer is
    // tracked under CrashLayerKey so it can be unloaded additively (crash toggle) like a district.
    private void AddCrashItem(List<(FileInfo File, string Label, string? District)> items, bool winter)
    {
        if (!CrashEnabled) return;
        string name = winter ? "city_crash_z.sds" : "city_crash.sds";
        var f = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "city_crash", name));
        if (f.Exists) items.Add((f, "city_crash", CrashLayerKey));
    }

    // Crash toggle ON with a live scene: append the layer without touching anything else.
    public void EnqueueCrashLayer()
    {
        if (_host.Catalogs.Map == null) return; // catalogs still initializing — LoadArea adds the layer afterwards
        if (_loadedDistricts.ContainsKey(CrashLayerKey)) return;          // loaded or load in flight
        foreach ((_, _, string? d) in _loadQueue) if (d == CrashLayerKey) return; // already queued

        var items = new List<(FileInfo File, string Label, string? District)>();
        AddCrashItem(items, _winter);
        foreach (var it in items) _loadQueue.Enqueue(it);
        _host.RaiseSceneChanged();
    }

    // Crash toggle OFF: drop a still-queued item, then tear the layer down via the district machinery.
    // An in-flight background load is discarded by the district-gone checks in BeginBuild/AttachStep.
    public void RemoveCrashLayer()
    {
        if (_loadQueue.Count > 0)
        {
            var keep = new List<(FileInfo File, string Label, string? District)>();
            foreach (var it in _loadQueue) if (it.District != CrashLayerKey) keep.Add(it);
            if (keep.Count != _loadQueue.Count)
            {
                _loadQueue.Clear();
                foreach (var it in keep) _loadQueue.Enqueue(it);
            }
        }
        UnloadDistrict(CrashLayerKey);
        _host.RaiseSceneChanged();
    }

    // Collision overlay toggle: additively (un)upload the translucent hull overlay for every resident district —
    // no scene reset. The "Collisions" tree layer is unaffected (it is always present); only the visual overlay
    // follows the toggle, so placements stay browsable/editable whether or not the hulls are drawn.
    public void SetCollisionEnabled(bool on)
    {
        if (CollisionEnabled == on) return;
        CollisionEnabled = on;
        if (_host.Rnd == null) return; // pre-load: the next background load uploads/omits the overlay by this flag
        foreach (CollisionSource src in _collisionSources)
        {
            if (on) ShowCollisionOverlay(src);
            else HideCollisionOverlay(src);
        }
        _host.RaiseSceneChanged();
    }

    // Parses a district's Collisions (.col) into the selectable "Collisions" tree layer: a CollisionDocumentAdapter
    // save unit + one child node per placement. Cheap — it does NOT decode the cooked hulls (that heavy step is
    // deferred to the overlay). Returns null when the district has no collision resource or it fails to parse —
    // collision is best-effort, never fatal. The layer node is a plain POCO here; it only data-binds once its Sds
    // root attaches on the UI thread.
    private static (SceneNode Layer, CollisionDocumentAdapter Doc)? BuildCollisionTree(FileInfo sourceFile, string extracted)
    {
        // Resolve through the SDS manifest, exactly as SdsCollisionSaver does when writing back. A directory glob
        // would be a SECOND, independent rule: the moment a district ships more than one .col the two could pick
        // different files and a save would land in a resource nobody is looking at.
        string? col;
        try { col = Formats.Archive.SdsManifest.Load(extracted).GetFiles("Collisions").FirstOrDefault(); }
        catch { return null; }
        if (col == null || !File.Exists(col)) return null;

        CollisionFile file;
        try { file = CollisionFile.Load(col); }
        catch { return null; }

        var doc = new CollisionDocumentAdapter(file, sourceFile);
        var layer = new SceneNode("Collisions", "Collision", true) { Source = doc };
        for (int i = 0; i < file.Instances.Count; i++)
            layer.AddChild(new SceneNode($"instance {i}", "CollisionInstance", false) { Source = doc.Node(file.Instances[i]) });
        return (layer, doc);
    }

    // Builds the "Crash objects" tree layer: the Translokator save unit at the top, one container per table row
    // (the prop and how many copies of it stand in the world) and one selectable node per copy. Rows come from
    // CrashPlacements, so only props that actually resolve to prototype geometry are listed — the same set the
    // viewport draws. A plain POCO tree here; it data-binds once its SDS root attaches on the UI thread.
    private static SceneNode BuildCrashTree(CrashPlacements placements)
    {
        var layer = new SceneNode("Crash objects", "Crash", true) { Source = placements.Document };
        foreach (Formats.Translokator.Object row in placements.Rows)
        {
            // Rows only — the copies under them are materialised on demand (see CrashNodeFor / ExpandCrashRow).
            layer.AddChild(new SceneNode($"{row.Name.String} — {row.Instances.Count}", "CrashObject", true));
        }
        return layer;
    }

    /// <summary>
    /// The tree node of one placement, created on first use and cached. A node is what selection, the property
    /// panel and the undo stack key on, so anything that hands a placement to the user goes through here.
    /// </summary>
    public SceneNode? CrashNodeFor(Instance placement, Formats.Translokator.Object row)
    {
        foreach (CrashSource src in _crashSources)
        {
            if (!src.RowNodes.TryGetValue(row, out SceneNode? rowNode)) continue;
            if (src.Nodes.TryGetValue(placement, out SceneNode? node)) return node;

            node = new SceneNode($"copy #{placement.ID}", "CrashInstance", false)
            {
                // Label by the placement id, not by position in the list: ids are stable across adds and
                // deletes, so a row's nodes keep their names while the list around them changes.
                Source = src.Placements.Document.Node(placement, row),
            };
            src.Nodes[placement] = node;
            rowNode.AddChild(node);
            return node;
        }
        return null;
    }

    /// <summary>
    /// Materialises every placement of a crash row — what expanding the row in the tree needs. Copies already
    /// created (by a viewport click) keep their nodes; the rest are added in one batch, so filling a row of a
    /// thousand costs one aggregate recompute rather than a thousand.
    /// </summary>
    public void ExpandCrashRow(SceneNode rowNode)
    {
        ArgumentNullException.ThrowIfNull(rowNode);
        foreach (CrashSource src in _crashSources)
        {
            foreach ((Formats.Translokator.Object row, SceneNode candidate) in src.RowNodes)
            {
                if (!ReferenceEquals(candidate, rowNode)) continue;
                if (rowNode.Children.Count == row.Instances.Count) return; // already whole

                var fresh = new List<SceneNode>(row.Instances.Count - rowNode.Children.Count);
                foreach (Instance copy in row.Instances)
                {
                    if (src.Nodes.ContainsKey(copy)) continue;
                    var node = new SceneNode($"copy #{copy.ID}", "CrashInstance", false)
                    {
                        Source = src.Placements.Document.Node(copy, row),
                    };
                    src.Nodes[copy] = node;
                    fresh.Add(node);
                }
                rowNode.AddChildren(fresh);
                return;
            }
        }
    }

    /// <summary>Forgets a placement's node (it was deleted) so a later undo materialises a fresh one.</summary>
    public void ForgetCrashNode(Instance placement)
    {
        foreach (CrashSource src in _crashSources) src.Nodes.Remove(placement);
    }

    // Re-uploads the copy matrices of the prototypes whose placements were just edited (live during a gizmo drag).
    // Only the edited ROWS are refreshed: the crash archive carries ~800 prototype meshes holding 134 000 copies
    // between them, and rebuilding all of them per frame is what made a drag stutter. Only the instance buffer is
    // rebuilt — geometry and textures stay as they are.
    //
    // Deliberately no RaiseSceneChanged here: the scene stats and the tree do not change when a prop moves, and
    // raising it per drag frame put a full stats recount plus a collection-view refresh in the frame budget.
    private void LiveUpdateCrash()
    {
        if (_host.Rnd == null) return;
        foreach (CrashSource src in _crashSources)
        {
            if (!src.Placements.Document.RenderDirty) continue;
            src.Placements.Document.RenderDirty = false;

            var stale = new HashSet<FrameObjectSingleMesh>();
            foreach (Formats.Translokator.Object row in src.Placements.Document.ConsumeDirtyRows())
            {
                foreach (FrameObjectSingleMesh mesh in src.Placements.MeshesOf(row)) stale.Add(mesh);
            }

            foreach (FrameObjectSingleMesh mesh in stale)
            {
                if (!src.Leaves.TryGetValue(mesh, out SceneNode? leaf) || leaf.Mesh == null) continue;
                CrashPlacements.Cloud cloud = src.Placements.CloudFor(mesh);
                _host.Rnd.UpdateInstances(leaf.Mesh, cloud.Matrices, cloud.DrawDistances);
            }
        }
    }

    // Ray-picks the nearest crash placement under a viewport ray (CPU). The props are drawn hardware-instanced,
    // so the ordinary GpuMesh pick cannot reach a single copy — it would have to stand for the whole cloud. This
    // tests the ray against the prototype's own geometry at each copy's matrix instead, and resolves the hit back
    // to that copy's tree node. Only rows whose prototype leaf is visible are considered.
    public SceneNode? PickCrash(Vector3 origin, Vector3 dir, out float bestT)
    {
        bestT = float.PositiveInfinity;
        Instance? hit = null;
        Formats.Translokator.Object? hitRow = null;
        foreach (CrashSource src in _crashSources)
        {
            foreach (Formats.Translokator.Object row in src.Placements.Rows)
            {
                foreach (FrameObjectSingleMesh mesh in src.Placements.MeshesOf(row))
                {
                    if (!src.Leaves.TryGetValue(mesh, out SceneNode? leaf)) continue;
                    GpuMesh? gm = leaf.Mesh;
                    if (gm == null || !gm.Visible || gm.PickPositions == null || gm.PickIndices == null) continue;

                    Matrix4x4 local = src.Placements.LocalOf(mesh, row);
                    foreach (Instance copy in row.Instances)
                    {
                        Matrix4x4 world = local * TransformMath.Compose(
                            copy.Quaternion, new Vector3(copy.Scale), copy.Position);
                        if (!PrototypeAabbHit(origin, dir, gm, world, out float tEnter) || tEnter > bestT) continue;

                        Vector3[] pos = gm.PickPositions;
                        uint[] idx = gm.PickIndices;
                        for (int k = 0; k + 2 < idx.Length; k += 3)
                        {
                            Vector3 a = Vector3.Transform(pos[idx[k]], world);
                            Vector3 b = Vector3.Transform(pos[idx[k + 1]], world);
                            Vector3 c = Vector3.Transform(pos[idx[k + 2]], world);
                            if (Picking.IntersectTriangle(origin, dir, a, b, c, out float t) && t < bestT)
                            {
                                bestT = t;
                                hit = copy;
                                hitRow = row;
                            }
                        }
                    }
                }
            }
        }
        // Only the winner is materialised — a pick must not build 57 000 nodes on its way past them.
        return hit != null && hitRow != null ? CrashNodeFor(hit, hitRow) : null;
    }

    // Broad phase: ray vs one copy's world AABB (the prototype's 8 local-AABB corners transformed by its matrix).
    private static bool PrototypeAabbHit(Vector3 o, Vector3 d, GpuMesh mesh, Matrix4x4 world, out float tEnter)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int k = 0; k < 8; k++)
        {
            var corner = new Vector3(
                (k & 1) == 0 ? mesh.LocalMin.X : mesh.LocalMax.X,
                (k & 2) == 0 ? mesh.LocalMin.Y : mesh.LocalMax.Y,
                (k & 4) == 0 ? mesh.LocalMin.Z : mesh.LocalMax.Z);
            Vector3 wp = Vector3.Transform(corner, world);
            min = Vector3.Min(min, wp);
            max = Vector3.Max(max, wp);
        }
        return Picking.IntersectAabb(o, d, min, max, out tEnter);
    }

    /// <summary>
    /// The prototype geometry and world matrix of each selected crash placement, for the silhouette highlight.
    /// An instanced mesh has no World of its own — every copy shares one buffer — so a selected copy can only be
    /// outlined by drawing the prototype again at that copy's matrix.
    /// </summary>
    public IReadOnlyList<(GpuMesh Mesh, Matrix4x4 World)> CrashSelectionOutlines(IReadOnlyList<SceneNode> selected)
    {
        var outlines = new List<(GpuMesh, Matrix4x4)>();
        foreach (SceneNode node in selected)
        {
            if (node.Source is not TranslokatorInstanceAdapter adapter) continue;
            foreach (CrashSource src in _crashSources)
            {
                if (!ReferenceEquals(src.Placements.Document, adapter.Document)) continue;
                Matrix4x4 placement = TransformMath.Compose(
                    adapter.Instance.Quaternion, new Vector3(adapter.Instance.Scale), adapter.Instance.Position);

                foreach (FrameObjectSingleMesh mesh in src.Placements.MeshesOf(adapter.Owner))
                {
                    if (!src.Leaves.TryGetValue(mesh, out SceneNode? leaf) || leaf.Mesh == null) continue;
                    outlines.Add((leaf.Mesh, src.Placements.LocalOf(mesh, adapter.Owner) * placement));
                }
            }
        }
        return outlines;
    }

    /// <summary>The tree node of the row a placement belongs to, and the placement's own node — so an edit that
    /// adds or removes a copy can keep the tree in step with the table.</summary>
    public SceneNode? CrashRowNode(Formats.Translokator.Object row)
    {
        foreach (CrashSource src in _crashSources)
        {
            if (src.RowNodes.TryGetValue(row, out SceneNode? node)) return node;
        }
        return null;
    }

    /// <summary>The loaded crash placement layer, or null when city_crash is not in the scene — the entry point
    /// for the edit commands.</summary>
    public CrashPlacements? CrashLayer => _crashSources.Count > 0 ? _crashSources[0].Placements : null;

    /// <summary>
    /// The prototype mesh rows a crash placement is a copy of. A copy is drawn by the hardware instancer from
    /// its row's prototype, so the copy's own tree node carries no geometry — reaching the shape means going
    /// back to the prototype, the same way an actor's does.
    /// </summary>
    public IReadOnlyList<SceneNode> CrashPrototypeRows(SceneNode placementNode)
    {
        if (placementNode.Source is not TranslokatorInstanceAdapter placement) return [];
        var rows = new List<SceneNode>();
        foreach (CrashSource src in _crashSources)
        {
            foreach (FrameObjectSingleMesh mesh in src.Placements.MeshesOf(placement.Owner))
            {
                if (src.Leaves.TryGetValue(mesh, out SceneNode? leaf)) rows.Add(leaf);
            }
        }
        return rows;
    }

    /// <summary>How many copies of a crash prototype the city draws. Zero when the node is not one.</summary>
    public int CrashCopyCount(SceneNode prototypeLeaf)
    {
        if (prototypeLeaf.Source is not FrameNodeAdapter { Frame: FrameObjectSingleMesh mesh }) return 0;
        int copies = 0;
        foreach (CrashSource src in _crashSources)
        {
            if (src.Leaves.ContainsKey(mesh)) copies += src.Placements.CloudFor(mesh).Matrices.Length;
        }
        return copies;
    }

    /// <summary>
    /// Re-uploads the instance cloud of a prototype whose GPU mesh was just replaced, and reports how many
    /// copies now draw the new shape. A fresh mesh comes back with no instances at all, so without this an
    /// edited prop would collapse from tens of thousands of copies to the one at the prototype's own
    /// transform. Zero when the node is not a crash prototype.
    /// </summary>
    public int RefreshCrashInstances(SceneNode prototypeLeaf)
    {
        if (_host.Rnd == null || prototypeLeaf.Mesh == null) return 0;
        if (prototypeLeaf.Source is not FrameNodeAdapter { Frame: FrameObjectSingleMesh mesh }) return 0;

        int copies = 0;
        foreach (CrashSource src in _crashSources)
        {
            if (!src.Leaves.ContainsKey(mesh)) continue;
            CrashPlacements.Cloud cloud = src.Placements.CloudFor(mesh);
            if (cloud.Matrices.Length == 0) continue;
            _host.Rnd.UpdateInstances(prototypeLeaf.Mesh, cloud.Matrices, cloud.DrawDistances);
            copies += cloud.Matrices.Length;
        }
        return copies;
    }

    // Adds one .nav tree bucket labelled with its object count (summed over the given NavPoint type ids).
    // Empty buckets are skipped so a district only shows the categories it actually has.
    private static void AddNavBucket(SceneNode parent, string label, Dictionary<int, int> counts, params int[] types)
    {
        int n = 0;
        foreach (int t in types) n += counts.GetValueOrDefault(t);
        if (n > 0) parent.AddChild(new SceneNode($"{label} — {n}", "NavLayer", false));
    }

    // Uploads a district's translucent hull overlay. Decodes the cooked meshes once (cached on the source as the
    // geometry pool); the instance matrices are always taken from the CURRENT .col so placement edits made while
    // the overlay was hidden are reflected on show. Idempotent — a source already rendering is left alone.
    private void ShowCollisionOverlay(CollisionSource src, CollisionRenderData? prebuilt = null)
    {
        if (src.Rendered) return;
        if (src.Decoded == null)
        {
            CollisionRenderData? built = prebuilt;
            if (built == null)
            {
                try { built = CollisionSceneBuilder.Build(src.Document.Collision, src.Document.ScaleOf); }
                catch { return; }
            }
            src.Decoded = built;
            src.CoverageAttemptKey = MeshSetKey(src.Document.Collision);
        }
        // Rebuild the instance matrices from the current placements (cheap; no re-decode) so edits show up.
        _host.Rnd!.SetCollisionDistrict(
            src.Sds, CollisionSceneBuilder.RebuildInstances(src.Decoded, src.Document.Collision, src.Document.ScaleOf));
        src.Rendered = true;
        src.Document.RenderDirty = false;
    }

    // Drops a district's hull overlay (leaving its "Collisions" tree layer in place).
    private void HideCollisionOverlay(CollisionSource src)
    {
        if (!src.Rendered) return;
        _host.Rnd!.RemoveCollisionDistrict(src.Sds);
        src.Rendered = false;
    }

    private void AddSeasonGround(bool winter)
    {
        string ground = winter ? "ground_zima" : "ground_leto";
        string groundSds = Path.Combine(MafiaEnvironment.PcFolder, "sds", "ground", ground + ".sds");
        if (File.Exists(groundSds))
        {
            _host.Rnd!.Textures.AddFolder(SdsMeshLoader.EnsureExtracted(new FileInfo(groundSds)));
        }
    }

    // Enter streaming mode: clear the scene, then StreamStep populates it by camera position.
    private void EnterStreaming()
    {
        _streaming = true;
        ResetScene();
        AddSeasonGround(_winter);

        // Shared city layer (FrameResource city_univers) — load in background, not unloaded while streaming.
        string cuSds = MafiaEnvironment.CityUniversSds;
        if (File.Exists(cuSds)) _loadQueue.Enqueue((new FileInfo(cuSds), "city_univers", null));

        // Crash objects (city_crash) — shared layer for the whole map, also not unloaded while streaming.
        var crashItems = new List<(FileInfo File, string Label, string? District)>();
        AddCrashItem(crashItems, _winter);
        foreach (var it in crashItems) _loadQueue.Enqueue(it);

        _host.RaiseSceneChanged();
    }

    // Per frame (in streaming mode): desired = ∪ districts of zones containing the camera; load missing ones,
    // unload those gone beyond the zones (+margin). One district per frame — don't freeze for long.
    private void StreamStep()
    {
        List<AreaZone>? zones = _host.Catalogs.Zones;
        if (zones == null || zones.Count == 0) return;
        Vector3 cam = _host.Rnd!.Camera.Position;

        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AreaZone z in zones)
            if (z.Contains(cam)) foreach (string d in z.Districts) desired.Add(d);

        if (desired.Count == 0) return; // camera outside all zones — keep the current set, don't flicker

        // keep = with margin (hysteresis): what's nearby — don't unload.
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AreaZone z in zones)
            if (z.Contains(cam, StreamMargin)) foreach (string d in z.Districts) keep.Add(d);

        bool changed = false;

        foreach (string name in _loadedDistricts.Keys.ToList())
        {
            // The crash layer is whole-map (never named by a zone) — only its toggle unloads it.
            if (string.Equals(name, CrashLayerKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (!keep.Contains(name)) { UnloadDistrict(name); changed = true; }
        }

        // Start ONE background load of a missing district, if nothing is currently loading/building.
        if (_loadTask == null && !_building)
        {
            foreach (string name in desired)
            {
                if (_loadedDistricts.ContainsKey(name)) continue;
                MapArea? area = null;
                foreach (MapArea a in _host.Catalogs.Areas) if (a.BaseName == name) { area = a; break; }
                if (area != null) StartBackgroundLoad(area.FileFor(_winter), name, name);
                else _loadedDistricts[name] = new DistrictLoad(); // no file — mark it, don't retry
                break;
            }
        }

        if (changed)
        {
            _host.RaiseSceneChanged();
        }
    }

    // Starts the background preparation of one .sds. The placeholder in _loadedDistricts (for streaming)
    // prevents re-requesting.
    private void StartBackgroundLoad(FileInfo file, string label, string? district)
    {
        if (district != null) _loadedDistricts[district] = new DistrictLoad();
        string folder = file.Directory?.Name ?? "sds"; // source folder of SDS (city / ground / …)
        _loadCtx = (label, district, folder, _loadGen, file);
        // Load city_crash with a special loader: prototypes from frame_resource + instances from Translokator.
        bool crash = file.Name.StartsWith("city_crash", StringComparison.OrdinalIgnoreCase);
        bool collision = CollisionEnabled && !crash; // decode this district's collision in the background load
        SceneRenderer renderer = _host.Rnd!;
        List<string> districtNames = _host.Catalogs.DistrictNames;
        _loadCts = new CancellationTokenSource();
        CancellationToken ct = _loadCts.Token;
        _loadTask = Task.Run(() => LoadAndPrepare(renderer, file, label, districtNames, crash, collision, ct));
    }

    // Background pipeline: extract → parse → detached SceneNode tree → GPU resources for every mesh
    // leaf. All of it is UI-thread-free: file IO and vendor parsing; plain-POCO tree construction
    // (nothing is data-bound until the root attaches on the UI thread; ChildrenView is lazy); and
    // device-only resource creation (the D3D11 device is free-threaded — only the immediate context
    // must stay on the UI thread; TextureLibrary is thread-safe). On cancellation (scene reset,
    // district unload, dispose) or failure, every mesh created so far is released HERE — device
    // object release is free-threaded too — and null is returned.
    private static PreparedLoad? LoadAndPrepare(SceneRenderer renderer, FileInfo file, string label,
        List<string> districtNames, bool crash, bool collision, CancellationToken ct)
    {
        var prepared = new List<(SceneNode Leaf, GpuMesh Mesh)>();
        try
        {
            // First visit unpacks the whole SDS — heavy. AddFolder completes before the task result is
            // observed, so attached meshes always find their texture folder registered (same ordering
            // guarantee the old UI-thread code had).
            string extracted = SdsMeshLoader.EnsureExtracted(file);
            renderer.Textures.AddFolder(extracted);

            // Some archives cannot be looked at alone: a car takes its chrome, glass, wheels and headlight
            // textures from the shared car library, and without it two thirds of what its materials name is
            // missing — which the texture cache answers with white. Registering the companion's folder is
            // enough for the TEXTURES; its shared PARTS are geometry the car does not reference and cannot
            // be mounted without the wheel table in its EDS record (still untyped — see the plan's P5).
            foreach (FileInfo companion in StageCompanions.For(file))
            {
                try { renderer.Textures.AddFolder(SdsMeshLoader.EnsureExtracted(companion)); }
                catch (Exception) { /* a missing or unreadable library is a duller car, not a failed load */ }
            }
            ct.ThrowIfCancellationRequested();

            List<SdsFrameNode> roots;
            ISceneDocument? document;
            CrashPlacements? placements = null;
            if (crash)
            {
                (roots, _, document, placements) = SdsMeshLoader.LoadCrashHierarchy(file);
            }
            else
            {
                (roots, _, document) = SdsMeshLoader.LoadHierarchy(file, districtNames);
            }
            ct.ThrowIfCancellationRequested();

            // SDS node → FrameResource node → frame tree; collect mesh leaves. The document wrapper mirrors
            // the real SDS layout, hosts the frame-resource property tab and keeps the loaded scene document
            // (and its frame objects) alive for transform editing.
            var meshLeaves = new List<SceneNode>();
            var sds = new SceneNode(label, "Sds", true);
            // The document carries its source archive (ISceneDocument.SourceArchive), so an edited object
            // under this node can be saved (re-serialize into the extracted folder) and built (repack → .sds).
            // Not expanded on creation: opening an SDS should show what it holds (FrameResource, Collisions, AI,
            // Actors), not dump a district's whole frame tree into the list.
            var frNode = new SceneNode("FrameResource", "FrameResource", true) { Source = document };
            foreach (SdsFrameNode r in roots) frNode.AddChild(SceneTree.BuildSceneTree(r, meshLeaves));
            sds.AddChild(frNode);

            foreach (SceneNode leaf in meshLeaves)
            {
                ct.ThrowIfCancellationRequested();
                GpuMesh gm = renderer.CreateMeshGpu(leaf.Pending!);
                gm.Owner = leaf; // so a viewport ray-pick resolves back to this tree node
                prepared.Add((leaf, gm));
            }

            // The crash archive's placement layer: one selectable node per copy, grouped by the table row it
            // belongs to (57 648 copies in the shipped city, so a flat list would be a wall of rows). The
            // prototype-mesh → leaf index lets an edited copy find the instanced mesh to re-upload.
            SceneNode? crashLayer = null;
            Dictionary<FrameObjectSingleMesh, SceneNode>? crashLeaves = null;
            if (crash && placements != null)
            {
                crashLayer = BuildCrashTree(placements);
                sds.AddChild(crashLayer);
                crashLeaves = new Dictionary<FrameObjectSingleMesh, SceneNode>();
                foreach (SceneNode leaf in meshLeaves)
                {
                    if (leaf.Source is FrameNodeAdapter fa && fa.Frame is FrameObjectSingleMesh sm)
                    {
                        crashLeaves[sm] = leaf;
                    }
                }
            }

            // ALWAYS build the selectable "Collisions" tree layer for a district that has a .col (cheap parse), so
            // placements are browsable/editable regardless of the overlay toggle. Decode the cooked hulls into the
            // render overlay (CPU-heavy) only when the toggle is on. The layer node grafts under sds here (POCO);
            // it data-binds when the Sds root attaches on the UI thread, and the overlay uploads in BeginBuild.
            SceneNode? collisionLayer = null;
            CollisionDocumentAdapter? collisionDoc = null;
            CollisionRenderData? collisionData = null;
            if (!crash)
            {
                (SceneNode Layer, CollisionDocumentAdapter Doc)? tree = BuildCollisionTree(file, extracted);
                if (tree != null)
                {
                    collisionLayer = tree.Value.Layer;
                    collisionDoc = tree.Value.Doc;
                    sds.AddChild(collisionLayer);
                    if (collision)
                    {
                        try { collisionData = CollisionSceneBuilder.Build(collisionDoc.Collision, collisionDoc.ScaleOf); }
                        catch { collisionData = null; }
                    }
                }
            }
            // Navigation-graph overlay (.nov): decode each district road graph into line segments (CPU
            // only — safe on the loader thread; the overlay toggle only gates drawing, so always prepare
            // it). Best-effort: a missing or bad .nov just yields no overlay, never a failed load.
            IReadOnlyList<Vector3>? navLines = null;
            IReadOnlyList<Vector3>? navMeshLines = null;
            IReadOnlyList<Vector3>? navWorldLines = null;
            if (!crash)
            {
                // .nov (NAV_OBJ): the AI navigation graph + Kynogon AI-mesh.
                int novVerts = 0, novEdges = 0, novCells = 0, novBoxes = 0;
                try
                {
                    var graph = new List<Vector3>();
                    var mesh = new List<Vector3>();
                    foreach (string nov in Directory.GetFiles(extracted, "*.nov", SearchOption.AllDirectories))
                    {
                        Formats.Navigation.ObjDataFile obj = Formats.Navigation.ObjDataFile.Load(nov);
                        graph.AddRange(obj.GraphLineVertices());
                        mesh.AddRange(obj.AiMeshBoxLines());
                        novVerts += obj.GraphVertexCount; novEdges += obj.GraphEdgeCount; novCells += obj.AiMeshCellCount;
                    }
                    novBoxes = mesh.Count / 24;
                    if (graph.Count > 0) navLines = graph;
                    if (mesh.Count > 0) navMeshLines = mesh;
                }
                catch { navLines = null; navMeshLines = null; }

                // .nav (NAV_AIWORLD): AI path objects — cover / vault-over / waypoints / pedestrian markers.
                var navTypes = new Dictionary<int, int>();
                try
                {
                    var world = new List<Vector3>();
                    foreach (string nav in Directory.GetFiles(extracted, "*.nav", SearchOption.AllDirectories))
                    {
                        Formats.Navigation.AiWorldFile aw = Formats.Navigation.AiWorldFile.Load(nav);
                        world.AddRange(aw.PathObjectBoxLines());
                        foreach (KeyValuePair<int, int> kv in aw.PathObjectTypeCounts())
                            navTypes[kv.Key] = navTypes.GetValueOrDefault(kv.Key) + kv.Value;
                    }
                    if (world.Count > 0) navWorldLines = world;
                }
                catch { navWorldLines = null; }

                // One "AI" section in the scene tree, split by function: "Usable" (things the AI/player uses —
                // cover, vault-over, actions, from .nav) and "Path" (the movement network — graph + AI-mesh, from
                // .nov). Drawing is still driven by the toolbar toggles; these grafted POCO nodes data-bind when
                // the SDS root attaches on the UI thread.
                var ai = new SceneNode("AI", "Navigation", true);
                if (navWorldLines != null)
                {
                    var usable = new SceneNode("Interactive", "Navigation", true);
                    AddNavBucket(usable, "Cover / vault-over", navTypes, 7);
                    AddNavBucket(usable, "Waypoints", navTypes, 3, 4);
                    AddNavBucket(usable, "Pedestrian (sidewalk / crossing / station)", navTypes, 8, 9, 10);
                    AddNavBucket(usable, "Hierarchy (groups / world parts)", navTypes, 1, 2);
                    AddNavBucket(usable, "Other", navTypes, 6, 11);
                    if (usable.Children.Count > 0) ai.AddChild(usable);
                }
                if (navLines != null || navMeshLines != null)
                {
                    var path = new SceneNode("Path", "Navigation", true);
                    if (navLines != null) path.AddChild(new SceneNode($"Graph — {novVerts} nodes, {novEdges} edges", "NavLayer", false));
                    if (navMeshLines != null) path.AddChild(new SceneNode($"AI-mesh — {novCells} cells, {novBoxes} boxes", "NavLayer", false));
                    ai.AddChild(path);
                }
                if (ai.Children.Count > 0) sds.AddChild(ai);
            }
            ct.ThrowIfCancellationRequested();

            // "Actors" section: everything the .act pack places, grouped by what it is. Each leaf carries an
            // ActorNodeAdapter, so selecting it fills the property panel with the actor's own fields. The ones
            // with no geometry also become viewport glyphs (ShowActors gates drawing).
            ActorMarkerRenderData? actorMarkers = null;
            List<(SceneNode Node, Vector3 Position)>? actorPickables = null;
            var actorNodes = new Dictionary<ActorEntry, SceneNode>();
            ActorPlacements? actorPlacements = null;
            if (document is SceneDocumentAdapter sceneDoc && sceneDoc.Placements.All.Count > 0)
            {
                ActorPlacements placements2 = sceneDoc.Placements;
                actorPlacements = placements2;
                // Its own save unit: an edit is enlisted by walking UP to the nearest ISceneDocument, and the
                // actors hang beside the FrameResource branch rather than under it.
                var actors = new SceneNode("Actors", "Actors", true)
                {
                    Source = new ActorDocumentAdapter(placements2, file, sceneDoc),
                };

                // Grouped by the entity type itself ("C_Sound", "LightEntity") — the tree stays a plain list of
                // type → actor. Counts and coverage live in the property panel, not in the row labels.
                var invisible = new HashSet<ActorEntry>(placements2.Invisible);
                foreach (IGrouping<string, ActorEntry> group in placements2.All
                             .GroupBy(a => a.TypeName.Length > 0 ? a.TypeName : a.Type.ToString())
                             .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var section = new SceneNode(group.Key, "Actors", true);
                    foreach (ActorEntry actor in group)
                    {
                        ActorNodeAdapter adapter = sceneDoc.ActorNode(actor);
                        var node = new SceneNode(adapter.Name, "Actor", false) { Source = adapter };
                        section.AddChild(node);
                        actorNodes[actor] = node;
                    }
                    actors.AddChild(section);
                }
                sds.AddChild(actors);

                if (placements2.Invisible.Count > 0)
                {
                    // Same walk the rebuild uses, so the initial state and every later one agree on which
                    // actors have a glyph and on the order a picked index resolves through.
                    var glyphs = new List<ActorEntry>(placements2.Invisible.Count);
                    actorPickables = new List<(SceneNode, Vector3)>(placements2.Invisible.Count);
                    ActorGlyphSet.Collect(placements2, actorNodes, glyphs, actorPickables);
                    actorMarkers = glyphs.Count > 0 ? ActorMarkerBuilder.Build(glyphs) : null;
                }
            }
            ct.ThrowIfCancellationRequested();

            // The rigs. Kept as skeletons rather than as finished lines: a bone can be dragged, and the overlay
            // is then rebuilt from these (see RefreshRig).
            List<SkeletonData> skeletons = CollectSkeletons(roots);

            // The helper nodes — dummies, points, volumes. Built here rather than at draw time: it is a walk
            // of the whole archive, and the result is one immutable buffer.
            HelperGlyphRenderData helpers = HelperGlyphBuilder.BuildFrames(roots);

            return new PreparedLoad
            {
                Sds = sds,
                Roots = roots,
                Helpers = helpers,
                Meshes = prepared,
                CollisionLayer = collisionLayer,
                CollisionDoc = collisionDoc,
                Collision = collisionData,
                CrashLayer = crashLayer,
                Crash = placements,
                CrashLeaves = crashLeaves,
                NavLines = navLines,
                NavMeshLines = navMeshLines,
                Skeletons = skeletons,
                NavWorldLines = navWorldLines,
                ActorMarkers = actorMarkers,
                ActorPickables = actorPickables,
                ActorPlacements = actorPlacements,
                ActorNodes = actorNodes.Count > 0 ? actorNodes : null,
                MeshNodeByFrame = ActorLayer.BuildMeshRows(meshLeaves),
                // Every row, not just the ones with geometry: a prototype's root is a holder, and resolving an
                // actor to the row a user would have clicked is what keeps Tab on an actor and Tab on its frame
                // sending the same object.
                FrameNodeByFrame = ActorLayer.BuildFrameRows(frNode),
            };
        }
        catch (Exception ex)
        {
            foreach ((_, GpuMesh gm) in prepared) gm.Dispose();
            if (ex is not OperationCanceledException) Debug.WriteLine("Load failed " + label + ": " + ex);
            return null;
        }
    }

    // Background preparation ready → attach the tree, register the district, queue meshes for attach.
    /// <summary>
    /// Every skinned model's rig in one line list: a segment from each bone to its parent, plus a small
    /// three-axis cross at every bone so a rig that is mostly flat still reads as a set of points. World
    /// space, like the meshes — the rest transforms are model-space, so they go through the model's own
    /// matrix. Null when nothing loaded has a rig, which is everything except cars and characters.
    /// </summary>
    /// <summary>Every skinned model's rig under these roots, in tree order.</summary>
    internal static List<SkeletonData> CollectSkeletons(IReadOnlyList<SdsFrameNode> roots)
    {
        var found = new List<SkeletonData>();
        foreach (SdsFrameNode root in roots) Walk(root);
        return found;

        void Walk(SdsFrameNode node)
        {
            if (node.Skeleton is { } rig) found.Add(rig);
            foreach (SdsFrameNode c in node.Children) Walk(c);
        }
    }

    /// <summary>
    /// Redraws the rig of the archive <paramref name="node"/> belongs to. Called after a bone moves: the
    /// overlay is one immutable vertex buffer per archive, so a moved bone only shows up once it is rebuilt.
    /// </summary>
    public void RefreshRig(SceneNode node)
    {
        for (SceneNode? n = node; n != null; n = n.Parent)
        {
            if (!_rigs.TryGetValue(n, out List<SkeletonData>? skeletons)) continue;
            _host.Rnd?.SetSkeletonDistrict(n, HelperGlyphBuilder.BuildRig(skeletons));
            PoseSkinnedMeshes(n);
            return;
        }
    }

    /// <summary>
    /// Queues the helper glyphs of the archive <paramref name="node"/> belongs to for a rebuild — everything
    /// a bone carries (climb boxes, locks, handles) moves with it, and a dummy dragged by the gizmo is its own
    /// glyph. Coalesced to one rebuild per frame in <see cref="Tick"/>: the layer is an immutable buffer per
    /// archive, and a drag would otherwise rebuild it on every mouse move.
    /// </summary>
    public void RefreshHelpers(SceneNode node)
    {
        for (SceneNode? n = node; n != null; n = n.Parent)
        {
            if (_helperRoots.ContainsKey(n)) { _helperDirty.Add(n); return; }
        }
    }

    private void RebuildDirtyHelpers()
    {
        if (_helperDirty.Count == 0) return;
        foreach (SceneNode sds in _helperDirty)
        {
            if (_helperRoots.TryGetValue(sds, out IReadOnlyList<SdsFrameNode>? roots))
                _host.Rnd?.SetHelperDistrict(sds, HelperGlyphBuilder.BuildFrames(roots));
            _helperPicks[sds] = CollectGlyphPicks(sds);
        }
        _helperDirty.Clear();
    }

    /// <summary>
    /// Nearest glyph under the ray — a helper node or a bone — or null. Only what is DRAWN can be hit: a
    /// layer that is switched off is not silently clickable, and a placeholder that was left out of the
    /// drawing is left out of the picking with it.
    /// </summary>
    public SceneNode? PickGlyph(Vector3 origin, Vector3 dir, out float bestT, float parallelSlack = -1f)
    {
        bestT = float.PositiveInfinity;
        SceneNode? hit = null;
        if (_host.Rnd is not { } renderer) return null;
        // Nothing drawn, nothing to hit — and this runs on every mouse move, so it leaves before the walk.
        if (!renderer.ShowHelpers && !renderer.ShowSkeleton) return null;

        // A pick set the last edit invalidated must never decide a click (the same reason the actor layer
        // pulls its rebuild forward here).
        RebuildDirtyHelpers();

        foreach (List<(SceneNode Node, Vector3 At, float Radius)> picks in _helperPicks.Values)
        {
            // Only the layers actually being drawn take part — the rig and the helpers switch separately.
            var candidates = new List<SceneNode>(picks.Count);
            var anchors = new List<Vector3>(picks.Count);
            var radii = new List<float>(picks.Count);
            foreach ((SceneNode node, Vector3 at, float radius) in picks)
            {
                bool isBone = node.Source is BoneNodeAdapter;
                if (isBone ? !renderer.ShowSkeleton : !renderer.ShowHelpers) continue;
                candidates.Add(node);
                anchors.Add(at);
                radii.Add(radius);
            }

            int index = ActorPicking.Pick(anchors, radii, origin, dir, out float t, parallelSlack);
            if (index >= 0 && t < bestT) { bestT = t; hit = candidates[index]; }
        }

        if (hit == null) bestT = float.PositiveInfinity;
        return hit;
    }

    // Every drawn glyph of one archive as a click target: the tree node it selects, where its glyph sits, and
    // how far off centre a click still counts. Built from the SCENE TREE rather than from the glyph data, so
    // a hit resolves straight to the row a click should select.
    private static List<(SceneNode Node, Vector3 At, float Radius)> CollectGlyphPicks(SceneNode sds)
    {
        var picks = new List<(SceneNode, Vector3, float)>();
        Walk(sds);
        return picks;

        void Walk(SceneNode node)
        {
            if (HelperGlyphBuilder.DrawsGlyph(node.Source))
            {
                picks.Add((node, HelperGlyphBuilder.GlyphAnchor(node.Source),
                    HelperGlyphBuilder.PickRadius(node.Source)));
            }
            foreach (SceneNode child in node.Children) Walk(child);
        }
    }

    /// <summary>
    /// Puts every skinned mesh under <paramref name="sds"/> into the pose its own bones are in now. This is
    /// what makes the body follow the bone rather than only the rig overlay: the vertices are blended on the
    /// GPU against a palette, and the palette is what changes here.
    /// </summary>
    private void PoseSkinnedMeshes(SceneNode sds)
    {
        foreach (SceneNode leaf in sds.DescendantMeshLeaves())
        {
            if (leaf.Mesh is not { } mesh || !mesh.IsSkinned) continue;
            // The rest transforms of the mesh's OWN model — not of some other rig in the archive that happens
            // to have the same number of bones.
            if (leaf.Source is not FrameNodeAdapter adapter) continue;
            if (adapter.Frame is not FrameObjectModel model || model.RestTransform is not { } rest) continue;
            mesh.SetPose(rest);
        }
    }

    private void BeginBuild()
    {
        Task<PreparedLoad?> task = _loadTask!;
        _buildCtx = _loadCtx;
        _loadTask = null;
        _loadCts?.Dispose();
        _loadCts = null;

        PreparedLoad? load = null;
        try { load = task.Result; }
        catch (Exception ex) { Debug.WriteLine("Load failed " + _buildCtx.label + ": " + ex); }
        if (load == null) return; // cancelled or failed — its meshes are already released

        // Scene was reset / district unloaded while loading — the result is stale: release its meshes.
        if (_buildCtx.gen != _loadGen
            || (_buildCtx.district != null && !_loadedDistricts.ContainsKey(_buildCtx.district)))
        {
            foreach ((_, GpuMesh gm) in load.Meshes) gm.Dispose();
            return;
        }

        SceneNode folder = _host.Tree.GetOrCreateFolder(_buildCtx.folder);
        folder.AddChild(load.Sds);

        // On screen now, so on the register: there is one extracted working copy per archive, and a second
        // editor opening the same one would be editing the same folder from a different picture of it.
        OpenArchives.Acquire(_buildCtx.file, _host);

        // Scene filter: hide BEFORE attach (descendant leaves inherit _visible=false, so their meshes
        // arrive hidden). On the UI thread so it never races a filter toggle.
        foreach (SceneNode frameRes in load.Sds.Children)
            foreach (SceneNode sc in frameRes.Children)
                _host.Tree.ApplySceneFilter(sc);

        // The flattened view the resource editor binds to. Rebuilt whole rather than patched: the stage holds
        // one archive, replaced whole, so there is nothing to patch incrementally. Skipped for the map, which
        // binds the real roots and would only be paying for a list nothing reads.
        if (!_host.IsMapViewport) _host.Tree.RebuildStageRoots();

        _buildMeshes = new List<GpuMesh>();
        _buildQueue = new Queue<(SceneNode Leaf, GpuMesh Mesh)>(load.Meshes);
        _building = true;

        if (_buildCtx.district != null)
            _loadedDistricts[_buildCtx.district] = new DistrictLoad
            {
                SdsNode = load.Sds, Folder = folder, Meshes = _buildMeshes, Archive = _buildCtx.file,
            };

        // Register this district's collision layer (built for any district with a .col; the crash prop layer has
        // none) and, when the overlay toggle is on, upload its hulls — pre-decoded in the background load, or
        // decoded now if the toggle flipped on before this district finished loading. The tree layer is already
        // grafted under load.Sds (attached above with the SDS subtree).
        if (_buildCtx.district != CrashLayerKey && load.CollisionDoc != null && load.CollisionLayer != null)
        {
            var source = new CollisionSource
            {
                Sds = load.Sds,
                Document = load.CollisionDoc,
                Layer = load.CollisionLayer,
            };
            _collisionSources.Add(source);
            if (CollisionEnabled) ShowCollisionOverlay(source, load.Collision);
        }

        // Register the crash placement layer (city_crash only). Its tree node is already grafted under load.Sds;
        // this is what lets the edit commands, the placement picker and the live instance refresh find it.
        if (load.Crash != null && load.CrashLayer != null && load.CrashLeaves != null)
        {
            var source = new CrashSource
            {
                Sds = load.Sds,
                Placements = load.Crash,
                Layer = load.CrashLayer,
                Leaves = load.CrashLeaves,
            };
            // Row nodes are built in table order, so this pairs them up without a name lookup.
            for (int i = 0; i < load.Crash.Rows.Count && i < load.CrashLayer.Children.Count; i++)
            {
                source.RowNodes[load.Crash.Rows[i]] = load.CrashLayer.Children[i];
            }
            _crashSources.Add(source);
        }

        // Navigation-graph overlay: uploaded per district (keyed by its SDS node); ShowNav gates drawing.
        if (load.NavLines != null) _host.Rnd!.SetNavDistrict(load.Sds, load.NavLines);
        if (load.NavMeshLines != null) _host.Rnd!.SetNavMeshDistrict(load.Sds, load.NavMeshLines);
        if (load.Skeletons is { Count: > 0 } skeletons)
        {
            _rigs[load.Sds] = skeletons;
            _host.Rnd!.SetSkeletonDistrict(load.Sds, HelperGlyphBuilder.BuildRig(skeletons));
        }
        // Helper glyphs (dummies, points, volumes): own toggle (ShowHelpers), same per-archive keying. The
        // roots are kept so an edit can rebuild the layer without re-reading the archive.
        if (load.Helpers is { IsEmpty: false } helpers) _host.Rnd!.SetHelperDistrict(load.Sds, helpers);
        if (load.Roots != null)
        {
            _helperRoots[load.Sds] = load.Roots;
            _helperPicks[load.Sds] = CollectGlyphPicks(load.Sds);
        }
        // .nav path objects (cover / vault-over markers): separate toggle (ShowNavWorld), same keying.
        if (load.NavWorldLines != null) _host.Rnd!.SetNavWorldDistrict(load.Sds, load.NavWorldLines);
        // Actor glyphs (sounds, lights, triggers…): own toggle (ShowActors), same per-district keying. The mesh
        // map goes in with them, since hiding an actor has to find the geometry it places.
        Actors.Install(load.Sds, load.ActorMarkers, load.ActorPickables, load.ActorPlacements, load.ActorNodes,
            load.MeshNodeByFrame, load.FrameNodeByFrame);

        if (load.Meshes.Count == 0) { _building = false; _host.RaiseSceneChanged(); }
    }

    // Attach prepared meshes to the render list under a per-frame time budget. Every step is O(1)
    // (the GPU resources already exist), so streaming a district never hitches a frame.
    private void AttachStep()
    {
        if (_buildCtx.gen != _loadGen
            || (_buildCtx.district != null && !_loadedDistricts.ContainsKey(_buildCtx.district)))
        {
            // Stale mid-attach: attached meshes are torn down by ResetScene/UnloadDistrict (they own
            // _buildMeshes); the still-queued ones were never attached anywhere — release them here.
            while (_buildQueue.Count > 0) _buildQueue.Dequeue().Mesh.Dispose();
            _building = false;
            return;
        }

        long start = Stopwatch.GetTimestamp();
        while (_buildQueue.Count > 0)
        {
            (SceneNode leaf, GpuMesh gm) = _buildQueue.Dequeue();
            AttachPreparedMesh(leaf, gm);
            // Selected (single or multi) while its GPU mesh was still streaming in (Mesh was null, so no outline):
            // now that the geometry has landed, light up its outline; re-run the selection UI only for the active.
            if (_host.Selection.Contains(leaf))
            {
                _host.Selection.UpdateSelectionHighlight();
                if (ReferenceEquals(leaf, _host.Selection.Active)) _host.RaiseSelectionChanged();
            }
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= AttachBudgetMs) break;
        }

        if (_buildQueue.Count == 0)
        {
            _building = false;
            // Frame the camera only in single-area mode (in streaming we do NOT frame — the camera isn't reset,
            // and the first city_univers load wouldn't drive it beyond all zones). The stage asks for it on
            // every load: there the camera is meant to follow what was just put in front of it.
            if ((!_hasFramedOnce || _frameNextLoad) && !_streaming && _buildMeshes.Count > 0)
            {
                _host.FrameCameraOver(_buildMeshes);
                _hasFramedOnce = true;
                _frameNextLoad = false;
            }
            _host.RaiseSceneChanged();
        }
    }

    // Attaches one background-prepared mesh to the render list and its owning district (bounds/counters).
    private void AttachPreparedMesh(SceneNode leaf, GpuMesh gm)
    {
        _host.Rnd!.AttachMesh(gm);
        // If this leaf's frame was transformed while the district was still attaching, the load-time
        // MeshData.World is stale — re-sync to the frame's current (cascaded) world so the late-attached mesh
        // matches its already-moved siblings.
        if (leaf.Source is IFrameNode fn && leaf.Pending != null && fn.WorldTransform != leaf.Pending.World)
            gm.SetWorld(fn.WorldTransform);
        leaf.Mesh = gm;   // the setter applies the leaf's cascaded visibility to the mesh
        leaf.Pending = null;
        _buildMeshes.Add(gm);
        _host.Tree.MeshCount++;
    }

    // Immediately attaches any still-streaming meshes under `root` (pulled from the build queue), so a delete of
    // `root` captures them like any other mesh (and undo can re-attach them) instead of leaving a ghost that a
    // later AttachStep would attach outside the tree.
    public void DrainPendingUnder(SceneNode root)
    {
        if (!_building || _buildQueue.Count == 0) return;
        for (int i = _buildQueue.Count; i > 0; i--)
        {
            (SceneNode leaf, GpuMesh gm) = _buildQueue.Dequeue();
            if (SceneTree.IsSelfOrDescendantOf(leaf, root)) AttachPreparedMesh(leaf, gm);
            else _buildQueue.Enqueue((leaf, gm));
        }
    }

    private void UnloadDistrict(string name)
    {
        if (!_loadedDistricts.TryGetValue(name, out DistrictLoad? load)) return;
        // A still-loading district: stop the background pipeline early (it releases its own meshes);
        // the district-gone checks in BeginBuild/AttachStep discard whatever still slips through.
        if (_loadTask != null && string.Equals(_loadCtx.district, name, StringComparison.OrdinalIgnoreCase))
            _loadCts?.Cancel();
        if (load.SdsNode is { } sds)
        {
            // Drop selection members that live in this district (their meshes are going away).
            if (_host.Selection.Selected.Any(n => SceneTree.IsSelfOrDescendantOf(n, sds)))
            {
                var keep = _host.Selection.Selected.Where(n => !SceneTree.IsSelfOrDescendantOf(n, sds)).ToList();
                _host.Selection.SetSelection(keep, keep.Count > 0 ? keep[^1] : null);
            }
            // Drop undo/redo entries whose objects have ALL left the scene: those in THIS district (about to
            // detach) plus any already detached by an earlier unload — covers cross-district group edits too.
            // (Discard on the dropped edits releases any detached-delete meshes they were holding.)
            _host.Editing.History.RemoveWhere(a => a is INodeEdit ne &&
                ne.Nodes.All(n => SceneTree.IsSelfOrDescendantOf(n, sds) || !_host.Tree.IsInScene(n)));
            // The unloaded frame resource can no longer be saved from memory — drop its persistence flags.
            if (_host.Persistence.PruneEditedFrames(n => SceneTree.IsSelfOrDescendantOf(n, sds)))
                _host.RaiseDirtyChanged();
        }
        if (load.Meshes != null) _host.Tree.MeshCount -= _host.Rnd!.RemoveMeshes(load.Meshes); // by actual removed count (deletes may have detached some)
        if (load.SdsNode is { } node)
        {
            _host.Rnd!.RemoveCollisionDistrict(node); // drop this district's collision overlay with it
            _host.Rnd!.RemoveNavDistrict(node);       // and its .nov graph overlay
            _host.Rnd!.RemoveSkeletonDistrict(node);  // and the rigs of its skinned models
            _rigs.Remove(node);
            _host.Rnd!.RemoveHelperDistrict(node);    // and its helper glyphs
            _helperRoots.Remove(node);
            _helperDirty.Remove(node);
            _helperPicks.Remove(node);
            _host.Rnd!.RemoveNavMeshDistrict(node);   // and its .nov AI-mesh overlay
            _host.Rnd!.RemoveNavWorldDistrict(node);  // and its .nav path-object overlay
            _host.Rnd!.RemoveActorDistrict(node);     // and its actor glyphs
            Actors.Remove(node);                      // and their pick entries, rows and mesh map
            _collisionSources.RemoveAll(s => ReferenceEquals(s.Sds, node)); // its "Collisions" tree node leaves with the SDS subtree
            _crashSources.RemoveAll(s => ReferenceEquals(s.Sds, node));     // …and its "Crash objects" layer
        }
        if (load.SdsNode != null && load.Folder != null)
        {
            _host.Tree.RemoveSds(load.SdsNode, load.Folder);
        }
        if (load.Archive is { } archive) OpenArchives.Release(archive, _host);
        _loadedDistricts.Remove(name);
    }

    // Shared reset+enqueue: clears the current scene and queues a new set of .sds for incremental loading.
    private void LoadSet(IReadOnlyCollection<(FileInfo File, string Label, string? District)> items)
    {
        ResetScene();
        foreach (var it in items) _loadQueue.Enqueue(it);
        _host.RaiseSceneChanged();
    }

    // Clears the current scene: discards an in-flight background load and empties the queue, tree, folders and counters.
    private void ResetScene()
    {
        _host.Selection.Select(null); // the selected node is about to disappear — drop it and its highlight box
        _host.Editing.History.Clear(); // the nodes the undo/redo entries reference are being unloaded
        _host.Persistence.Reset();
        _loadGen++; // discard the result of a still-in-flight background load
        _loadCts?.Cancel(); // and stop it early — it releases its own GPU resources on the way out
        _loadQueue.Clear();
        _host.Rnd?.Clear();
        _host.Rnd?.ClearCollision();
        _host.Rnd?.ClearNov();
        _host.Rnd?.ClearNavWorld();
        _host.Rnd?.ClearSkeletons();
        _rigs.Clear();
        _host.Rnd?.ClearHelpers();
        _helperRoots.Clear();
        _helperDirty.Clear();
        _helperPicks.Clear();
        _host.Rnd?.SetHelperHighlight(null);
        _host.Rnd?.ClearActors();
        Actors.Clear();
        // The car-physics overlay, which used to be the one layer a reset forgot: its lines survived a
        // restore-from-backup and went on drawing collision the archive no longer had.
        _host.Rnd?.ClearPartShapes();
        _host.CarCollisionEditing.Forget();
        _host.HitBoxes.Forget();
        _collisionSources.Clear();
        _crashSources.Clear();
        _host.Tree.Clear();
        _loadedDistricts.Clear();
        OpenArchives.ReleaseAll(_host); // nothing of ours is loaded any more
    }

    /// <summary>
    /// Clears the scene AND waits (bounded) for a still-running background load to end, so the caller may
    /// rewrite archives and extracted folders on disk (restore-from-backup). <see cref="ResetScene"/> alone
    /// only CANCELS the load — the pipeline observes the token at checkpoints and can keep extracted files
    /// open for a while, racing a folder delete. The ordinary <see cref="Tick"/>/BeginBuild path then
    /// discards the finished task as stale (generation mismatch) and releases its meshes; a load stuck past
    /// the grace in an uncancellable stage is left to the caller's delete-retry to contend with.
    /// </summary>
    public void ResetForExternalChange()
    {
        ResetScene();
        try { _loadTask?.Wait(TimeSpan.FromSeconds(8)); }
        catch (AggregateException) { /* cancelled/faulted — BeginBuild observes the result either way */ }
    }

    // The background pipeline touches the device (resource creation) and TextureLibrary — neither may
    // be released underneath it. Cancel and give it a short grace to reach a token checkpoint; if it is
    // still inside a long, uncancellable stage (first-visit SDS unpack, vendor parse — routinely longer
    // than any acceptable UI wait), hand GPU-stack ownership to a continuation that releases everything
    // on the UI thread once the task actually ends. The window closes back to the launcher, so the
    // process (and its dispatcher) keeps running. Returns TRUE when teardown was deferred to that
    /// continuation — the host must then skip its synchronous base-Dispose path.
    public bool ShutdownDeferred(Func<Action?> tearDown)
    {
        _loadCts?.Cancel();
        Task<PreparedLoad?>? task = _loadTask;
        _loadTask = null;
        _loadCts = null; // not disposed while the task may still poll the token; GC collects it later

        if (_building)
        {
            while (_buildQueue.Count > 0) _buildQueue.Dequeue().Mesh.Dispose(); // never attached
            _building = false;
        }

        if (task != null)
        {
            try { task.Wait(100); } catch { /* result observed below / in the continuation */ }
            if (!task.IsCompleted)
            {
                Action? releaseGpu = tearDown(); // detach from WPF now; device outlives the loader
                System.Windows.Threading.Dispatcher dispatcher = _host.Dispatcher;
                task.ContinueWith(t =>
                {
                    try
                    {
                        if (t.Status == TaskStatus.RanToCompletion && t.Result is { } late)
                            foreach ((_, GpuMesh gm) in late.Meshes) gm.Dispose();
                    }
                    catch { /* cancelled/faulted loads release their own meshes */ }
                    if (releaseGpu != null) dispatcher.BeginInvoke(releaseGpu);
                }, TaskScheduler.Default);
                return true;
            }
            try
            {
                if (task.Result is { } load)
                    foreach ((_, GpuMesh gm) in load.Meshes) gm.Dispose();
            }
            catch { /* cancelled/faulted loads release their own meshes */ }
        }

        return false;
    }
}
