using System.Numerics;
using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Domain;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Scene;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// The viewport's actor layer, per resident district: the glyphs drawn for actors that place nothing visible,
/// the entries a click is tested against, and the way from an actor to the geometry it places (which hangs in
/// the FrameResource branch, not under the actor).
///
/// Split out of <see cref="DistrictStreamer"/> because it is a world of its own: an actor is not a frame node,
/// its glyph is not a GpuMesh, and none of the eye/pick/outline machinery the streamer has for meshes reaches
/// it. Everything here is keyed by the district's SDS tree node, the same key the renderer uses for its
/// buffers, so a district's actors leave with it.
/// </summary>
internal sealed class ActorLayer
{
    private readonly D3DImageHost _host;

    public ActorLayer(D3DImageHost host) => _host = host;

    // Glyph → its tree node, per district. A glyph has no geometry, so the mesh pick cannot see it; this is
    // what a viewport click tests against.
    private readonly Dictionary<SceneNode, List<(SceneNode Node, Vector3 Position)>> _pickables = new();

    // Per district: which actor governs which frame (the placements know), and each actor's tree node.
    // Together these make a click on a placed mesh select the ACTOR that puts it there — the mesh is only its
    // prototype. _meshRows is the way back, so the actor's geometry can still be outlined.
    private readonly Dictionary<SceneNode, (ActorPlacements Placements, Dictionary<ActorEntry, SceneNode> Nodes)> _scenes = new();
    private readonly Dictionary<SceneNode, Dictionary<FrameObjectBase, SceneNode>> _meshRows = new();

    // Every frame's tree row, not just the ones carrying a mesh — how an actor is resolved to the row a user
    // would have clicked in the tree. The mesh map cannot answer that: a prototype's root is a holder with no
    // geometry of its own, so it is not in there.
    private readonly Dictionary<SceneNode, Dictionary<FrameObjectBase, SceneNode>> _frameRows = new();

    // Districts whose glyphs and pick entries no longer match their actor list: an eye was toggled, an actor
    // moved, or one was deleted, copied or restored. Rebuilding is a full buffer upload, so it is coalesced to
    // once per frame rather than done per node of a cascade.
    private readonly HashSet<SceneNode> _dirty = new();

    // ── District lifetime ──

    /// <summary>Installs a freshly loaded district's actors.</summary>
    public void Install(SceneNode sdsNode, ActorMarkerRenderData? markers,
        List<(SceneNode Node, Vector3 Position)>? pickables, ActorPlacements? placements,
        Dictionary<ActorEntry, SceneNode>? nodes, Dictionary<FrameObjectBase, SceneNode>? meshRows,
        Dictionary<FrameObjectBase, SceneNode>? frameRows = null)
    {
        if (markers != null) _host.Rnd!.SetActorDistrict(sdsNode, markers);
        if (pickables is { Count: > 0 }) _pickables[sdsNode] = pickables;
        if (meshRows != null) _meshRows[sdsNode] = meshRows;
        if (frameRows != null) _frameRows[sdsNode] = frameRows;
        if (placements == null || nodes == null) return;

        _scenes[sdsNode] = (placements, nodes);
        foreach (SceneNode node in nodes.Values) Watch(sdsNode, node);
    }

    /// <summary>Drops a district's actors (unload).</summary>
    public void Remove(SceneNode sdsNode)
    {
        _pickables.Remove(sdsNode);
        _dirty.Remove(sdsNode);
        _scenes.Remove(sdsNode);
        _meshRows.Remove(sdsNode);
        _frameRows.Remove(sdsNode);
    }

    /// <summary>Drops every district's actors (scene reset).</summary>
    public void Clear()
    {
        _pickables.Clear();
        _dirty.Clear();
        _scenes.Clear();
        _meshRows.Clear();
        _frameRows.Clear();
    }

    /// <summary>Mesh leaves keyed by the frame they render, so an actor's subtree can find its geometry.</summary>
    public static Dictionary<FrameObjectBase, SceneNode>? BuildMeshRows(List<SceneNode> meshLeaves)
    {
        if (meshLeaves.Count == 0) return null;
        var map = new Dictionary<FrameObjectBase, SceneNode>(meshLeaves.Count);
        foreach (SceneNode leaf in meshLeaves)
        {
            if (leaf.Source is FrameNodeAdapter fna) map[fna.Frame] = leaf;
        }
        return map.Count > 0 ? map : null;
    }

    // ── Glyphs and pick entries ──

    /// <summary>Marks every resident district's glyphs and pick entries for rebuild — after an actor was
    /// deleted, copied or restored, neither matches the actor list any more.</summary>
    public void MarkAllDirty()
    {
        foreach (SceneNode sdsNode in _scenes.Keys) _dirty.Add(sdsNode);
    }

    /// <summary>Marks the district owning these placements stale — an actor of it moved or changed.</summary>
    public void MarkDirty(ActorPlacements placements)
    {
        if (DistrictOf(placements) is { } sdsNode) _dirty.Add(sdsNode);
    }

    /// <summary>
    /// Rebuilds the glyphs AND the pick entries of every district that went stale, both out of one walk over
    /// the LIVE actor list. Deriving them together is what keeps a picked index pointing at the glyph it was
    /// aimed at; deriving them from the list rather than from a load-time snapshot is what makes a deleted
    /// actor stop being clickable, a copy start being clickable, and a moved one take its marker with it.
    /// </summary>
    public void RebuildDirty()
    {
        if (_dirty.Count == 0 || _host.Rnd == null) return;

        foreach (SceneNode sdsNode in _dirty)
        {
            if (!_scenes.TryGetValue(sdsNode,
                    out (ActorPlacements Placements, Dictionary<ActorEntry, SceneNode> Nodes) scene)) continue;

            int capacity = scene.Placements.Invisible.Count;
            var visible = new List<ActorEntry>(capacity);
            var pickables = new List<(SceneNode Node, Vector3 Position)>(capacity);
            ActorGlyphSet.Collect(scene.Placements, scene.Nodes, visible, pickables);

            if (pickables.Count > 0) _pickables[sdsNode] = pickables;
            else _pickables.Remove(sdsNode);
            _host.Rnd.SetActorDistrict(sdsNode, visible.Count > 0 ? ActorMarkerBuilder.Build(visible) : null);
        }
        _dirty.Clear();
    }

    /// <summary>Nearest actor glyph under the ray, or null. Glyphs draw over everything, so they win a pick
    /// outright — clicking the marker you can see selects that actor, wall in between or not.
    /// <paramref name="parallelSlack"/> is <see cref="ActorPicking.ParallelSlack"/> for the viewport's camera.</summary>
    public SceneNode? Pick(Vector3 origin, Vector3 dir, out float bestT, float parallelSlack = -1f)
    {
        // A click must never be tested against entries the last edit already invalidated: an actor pick wins
        // outright over the geometry behind it, so one stale marker would swallow every click near it. The
        // rebuild is normally the render loop's, this only pulls it forward when an edit landed in between.
        RebuildDirty();

        bestT = float.PositiveInfinity;
        SceneNode? hit = null;
        foreach (List<(SceneNode Node, Vector3 Position)> list in _pickables.Values)
        {
            var positions = new Vector3[list.Count];
            for (int i = 0; i < list.Count; i++) positions[i] = list[i].Position;

            int index = ActorPicking.Pick(positions, origin, dir, ActorMarkerBuilder.Radius, out float t,
                parallelSlack);
            if (index >= 0 && t < bestT)
            {
                bestT = t;
                hit = list[index].Node;
            }
        }
        if (hit == null) bestT = float.PositiveInfinity;
        return hit;
    }

    // ── Rows ──

    /// <summary>
    /// Registers an actor the editor just created (a copy) with the district that owns
    /// <paramref name="placements"/>, so its glyph is drawn and its marker can be clicked. The row is kept even
    /// when the copy is undone: identity is what everything keys on, the actor list is what decides what is
    /// drawn, and re-adding it on redo would otherwise wire a second visibility handler onto the same node.
    /// </summary>
    public void AddActorRow(ActorPlacements placements, ActorEntry actor, SceneNode node)
    {
        if (DistrictOf(placements) is not { } sdsNode) return;
        if (_scenes[sdsNode].Nodes.TryAdd(actor, node)) Watch(sdsNode, node);
        _dirty.Add(sdsNode);
    }

    /// <summary>The tree row a frame object's geometry hangs on, when its district is resident.</summary>
    public SceneNode? MeshRowOf(FrameObjectBase frame)
    {
        foreach (Dictionary<FrameObjectBase, SceneNode> map in _meshRows.Values)
        {
            if (map.TryGetValue(frame, out SceneNode? leaf)) return leaf;
        }
        return null;
    }

    /// <summary>
    /// Records a mesh row the editor just created (a cloned actor prototype), so everything that reaches an
    /// actor's geometry through its frames — the transform sync, the eye, the selection outline — finds the
    /// new object too. Kept across an undo: the row keeps its identity, and what decides whether anything is
    /// drawn is the placements, not this map.
    /// </summary>
    public void AddMeshRow(ActorPlacements placements, FrameObjectBase frame, SceneNode leaf)
    {
        if (DistrictOf(placements) is not { } sdsNode) return;
        if (!_meshRows.TryGetValue(sdsNode, out Dictionary<FrameObjectBase, SceneNode>? map))
        {
            _meshRows[sdsNode] = map = new Dictionary<FrameObjectBase, SceneNode>();
        }
        map[frame] = leaf;
    }

    /// <summary>
    /// Records the tree row of a frame the editor just brought into the scene (an object carried from another
    /// archive), so <see cref="RowOf"/> and <see cref="PrototypeRow"/> find it like any row the load built.
    /// </summary>
    public void AddFrameRow(ActorPlacements placements, FrameObjectBase frame, SceneNode row)
    {
        if (DistrictOf(placements) is not { } sdsNode) return;
        if (!_frameRows.TryGetValue(sdsNode, out Dictionary<FrameObjectBase, SceneNode>? map))
        {
            _frameRows[sdsNode] = map = new Dictionary<FrameObjectBase, SceneNode>();
        }
        map[frame] = row;
    }

    /// <summary>The actor node governing a frame object, or null when no actor places it.</summary>
    public SceneNode? ActorRowFor(FrameObjectBase frame)
    {
        foreach ((ActorPlacements placements, Dictionary<ActorEntry, SceneNode> nodes) in _scenes.Values)
        {
            if (placements.ActorCovering(frame) is { } actor && nodes.TryGetValue(actor, out SceneNode? node))
            {
                return node;
            }
        }
        return null;
    }

    // ── The geometry an actor places ──

    /// <summary>Re-uploads the world matrices of the geometry an actor places, after that actor moved. The
    /// placement was already refreshed by the adapter, so each frame's node reports its new world. An actor
    /// with no geometry moves its glyph instead, which is a district rebuild — hence the dirty mark either
    /// way, since the marker is also what the click test uses.</summary>
    public void SyncMeshes(SceneNode actorRow)
    {
        if (actorRow.Source is not ActorNodeAdapter actor) return;
        MarkDirty(actor.Placements);
        if (actor.Target is not { } target) return;
        Walk(target, leaf =>
        {
            if (leaf.Mesh is { Instanced: false } mesh && leaf.Source is IFrameNode fn)
            {
                mesh.SetWorld(fn.WorldTransform);
            }
        });
    }

    /// <summary>
    /// The tree row of the prototype an actor places — the row a user would have clicked in the tree to reach
    /// the same object. That geometry hangs under the FrameResource branch and never under the actor, so a
    /// walk down from the actor row itself finds nothing.
    ///
    /// It resolves through the ROW index rather than by walking the frame graph, because those two do not agree.
    /// <c>FrameObjectBase.Children</c> is the union of both parent slots, so a frame reachable from two parents
    /// appears under both; the tree gives it to whichever parent claims it first. Walking the graph therefore
    /// pulls in objects the tree hangs elsewhere — a proxy or LOD twin of the very object being edited among
    /// them. Editing that twin changes nothing the game draws, which is exactly how "I edited it and the game
    /// looks the same" happens.
    /// </summary>
    public SceneNode? PrototypeRow(SceneNode actorRow)
    {
        if (actorRow.Source is not ActorNodeAdapter actor || actor.Target is not { } target) return null;
        foreach (Dictionary<FrameObjectBase, SceneNode> map in _frameRows.Values)
        {
            if (map.TryGetValue(target, out SceneNode? row)) return row;
        }
        // A district loaded before the row index existed, or a target the tree never placed: fall back to the
        // mesh rows of the frame graph rather than refusing the export outright.
        foreach (Dictionary<FrameObjectBase, SceneNode> map in _meshRows.Values)
        {
            if (map.TryGetValue(target, out SceneNode? row)) return row;
        }
        return null;
    }

    /// <summary>The tree row showing a frame object, across every resident district. Null when nothing does.</summary>
    public SceneNode? RowOf(FrameObjectBase frame)
    {
        foreach (Dictionary<FrameObjectBase, SceneNode> map in _frameRows.Values)
        {
            if (map.TryGetValue(frame, out SceneNode? row)) return row;
        }
        return null;
    }

    /// <summary>Every frame's tree row under a FrameResource node, keyed by the frame it shows.</summary>
    public static Dictionary<FrameObjectBase, SceneNode> BuildFrameRows(SceneNode frameResourceNode)
    {
        var map = new Dictionary<FrameObjectBase, SceneNode>();
        IndexRows(frameResourceNode, map);
        return map;
    }

    private static void IndexRows(SceneNode node, Dictionary<FrameObjectBase, SceneNode> map)
    {
        if (node.Source is FrameNodeAdapter fna) map[fna.Frame] = node;
        foreach (SceneNode child in node.Children) IndexRows(child, map);
    }

    /// <summary>GPU meshes to outline for the selected actors — an actor with geometry has no mesh of its own,
    /// so the highlight is the meshes of the subtree it places.</summary>
    public IReadOnlyList<GpuMesh> SelectionOutlines(IReadOnlyList<SceneNode> selected)
    {
        var meshes = new List<GpuMesh>();
        foreach (SceneNode n in selected)
        {
            if (n.Source is not ActorNodeAdapter actor || actor.Target is not { } target) continue;
            Walk(target, leaf =>
            {
                if (leaf.Mesh is { Instanced: false } m) meshes.Add(m);
            });
        }
        return meshes;
    }

    /// <summary>
    /// Shows or hides the geometry of the subtree an actor places — through the tree nodes, not their GPU
    /// meshes. A mesh still queued for upload has no GpuMesh yet, and what the upload applies when it lands is
    /// the NODE's flag; setting the mesh alone leaves such a mesh to attach visible moments later, which is how
    /// a deleted actor's geometry could stay on screen.
    /// </summary>
    public void SetPlacedVisible(FrameObjectBase frame, bool visible) =>
        Walk(frame, leaf =>
        {
            // An instanced prototype is left alone: in city_crash the .tra table copies it across the whole map
            // and one actor is not what puts those copies there, so hiding it would blank a whole row of props.
            // Pending meshes are asked before they exist, which is the point — the node's flag is what the
            // upload applies when it lands.
            bool instanced = leaf.Mesh?.Instanced ?? leaf.Pending?.Instances is { Length: > 0 };
            if (!instanced) leaf.IsVisible = visible;
        });

    // Every mesh row of a subtree, guarded against the cycles a malformed hierarchy can carry.
    private void Walk(FrameObjectBase frame, Action<SceneNode> visit) =>
        Walk(frame, visit, new HashSet<FrameObjectBase>());

    private void Walk(FrameObjectBase frame, Action<SceneNode> visit, HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return;
        foreach (Dictionary<FrameObjectBase, SceneNode> map in _meshRows.Values)
        {
            if (map.TryGetValue(frame, out SceneNode? leaf)) visit(leaf);
        }
        foreach (FrameObjectBase child in frame.Children) Walk(child, visit, seen);
    }

    // An actor node owns nothing the eye's usual cascade can reach: a glyph is not a GpuMesh, and the geometry
    // an actor places hangs under the FrameResource branch, not under the actor. So each actor row is watched,
    // and hiding it either drops its glyph from the district buffer or hides the meshes of what it places.
    private void Watch(SceneNode sdsNode, SceneNode node)
    {
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SceneNode.IsVisible)) return;
            if (node.Source is not ActorNodeAdapter actor) return;

            if (actor.HasGlyph) _dirty.Add(sdsNode);
            else if (actor.Target is { } target) SetPlacedVisible(target, node.IsVisible);
        };
    }

    private SceneNode? DistrictOf(ActorPlacements placements)
    {
        foreach (KeyValuePair<SceneNode, (ActorPlacements Placements, Dictionary<ActorEntry, SceneNode> Nodes)> pair
                 in _scenes)
        {
            if (ReferenceEquals(pair.Value.Placements, placements)) return pair.Key;
        }
        return null;
    }
}
