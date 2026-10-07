using System.Numerics;
using Illusion.Formats.Actors;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Actors;

/// <summary>
/// Where a scene's actors put its frame objects.
///
/// A frame object an actor places is a PROTOTYPE: it sits at the origin with an identity matrix, and the
/// spawn transform lives in the actor pack (.act) instead — which is why such objects all pile up at (0,0,0)
/// until the pack is read. This resolves each actor to its frame object and hands out the matrix that
/// belongs in front of that object's own world transform, for the whole subtree under it.
///
/// The link is by hash, never by name: an actor names its frame, but many name one that lives in another
/// archive (ambient sounds mostly), and a scene reference's own name is the DEFINITION's, not the frame's.
/// Measured across the shipped game: 27385 actors resolve, every scene reference is claimed by exactly one
/// actor, and no frame is shared by two actors — so one matrix per subtree is enough, no instancing.
/// </summary>
public sealed class ActorPlacements
{
    private readonly Dictionary<FrameObjectBase, Matrix4x4> _byFrame;
    private readonly Dictionary<FrameObjectBase, ActorEntry> _actorByTarget;

    private ActorPlacements(Dictionary<FrameObjectBase, Matrix4x4> byFrame,
        Dictionary<FrameObjectBase, ActorEntry> actorByTarget)
    {
        _byFrame = byFrame;
        _actorByTarget = actorByTarget;
    }

    /// <summary>Placed subtree roots (one per resolved actor).</summary>
    public int PlacedCount => _actorByTarget.Count;

    /// <summary>Frame objects covered, including everything under a placed root.</summary>
    public int CoveredCount => _byFrame.Count;

    /// <summary>Actors whose frame is not in this scene — ambient sounds and the like. Nothing to place.</summary>
    public int UnresolvedCount { get; private init; }

    /// <summary>Every actor the scene's packs declare, in pack order.</summary>
    public IReadOnlyList<ActorEntry> All => _allList;

    private List<ActorEntry> _allList = new();

    /// <summary>The actors nothing draws: either their frame is not in this scene, or it is but its subtree
    /// carries no mesh. These are what the viewport marks with a glyph — see <see cref="ActorMarkerBuilder"/>.</summary>
    public IReadOnlyList<ActorEntry> Invisible => _invisibleList;

    private List<ActorEntry> _invisibleList = new();

    /// <summary>Whether this actor is one of the <see cref="Invisible"/> ones, i.e. whether it is represented by
    /// a glyph rather than by geometry.</summary>
    public bool HasGlyph(ActorEntry actor) => _invisibleSet.Contains(actor);

    private HashSet<ActorEntry> _invisibleSet = new();

    /// <summary>The frame an actor places, when this scene has it.</summary>
    public FrameObjectBase? TargetOf(ActorEntry actor) =>
        _targetByActor.TryGetValue(actor, out FrameObjectBase? frame) ? frame : null;

    private Dictionary<ActorEntry, FrameObjectBase> _targetByActor = new();

    /// <summary>The matrix to apply in front of <paramref name="frame"/>'s own world transform, or identity
    /// when no actor places it.</summary>
    public Matrix4x4 For(FrameObjectBase frame) =>
        _byFrame.TryGetValue(frame, out Matrix4x4 m) ? m : Matrix4x4.Identity;

    public bool TryGet(FrameObjectBase frame, out Matrix4x4 placement) => _byFrame.TryGetValue(frame, out placement);

    /// <summary>The actor that places <paramref name="frame"/> — set only on the subtree root it targets.</summary>
    public ActorEntry? ActorOf(FrameObjectBase frame) =>
        _actorByTarget.TryGetValue(frame, out ActorEntry? actor) ? actor : null;

    /// <summary>The actor that governs <paramref name="frame"/>, whether it is the frame the actor names or
    /// anything under it. This is the one a viewport click should select: the mesh you see is a prototype, and
    /// the actor is what puts it there.</summary>
    public ActorEntry? ActorCovering(FrameObjectBase frame) =>
        _actorByCoveredFrame.TryGetValue(frame, out ActorEntry? actor) ? actor : null;

    private Dictionary<FrameObjectBase, ActorEntry> _actorByCoveredFrame = new();

    public static ActorPlacements Empty { get; } =
        new(new Dictionary<FrameObjectBase, Matrix4x4>(), new Dictionary<FrameObjectBase, ActorEntry>());

    /// <summary>The packs these placements came from, with the file each was read from (empty when the packs
    /// were fed in directly, as the probes do) — the save side writes back through this.</summary>
    public IReadOnlyList<(ActorsFile Pack, string Path)> Packs { get; private init; } = [];

    /// <summary>The pack an actor belongs to, for a write-back that must touch only that file.</summary>
    public ActorsFile? PackOf(ActorEntry actor) =>
        _packByActor.TryGetValue(actor, out ActorsFile? pack) ? pack : null;

    private Dictionary<ActorEntry, ActorsFile> _packByActor = new();

    /// <summary>
    /// Re-applies an actor's transform to the subtree it places, after that transform was edited. Only the
    /// matrices change — which frames the actor covers is fixed by the pack, not by where it stands.
    /// </summary>
    public void Refresh(ActorEntry actor)
    {
        if (TargetOf(actor) is not { } target) return;
        Respread(target, actor.Transform, _byFrame, new HashSet<FrameObjectBase>());
    }

    /// <summary>
    /// Drops an actor from the resolved set: it no longer places anything, so the frames it covered fall back to
    /// their own transforms (which is the origin — they are prototypes). The frame objects themselves stay in the
    /// FrameResource; without an actor the game never spawns them, exactly as it never spawns an unreferenced
    /// prototype. Returns the frame it used to place, so the caller can hide that geometry.
    /// </summary>
    public FrameObjectBase? Detach(ActorEntry actor)
    {
        _allList.Remove(actor);
        _invisibleList.Remove(actor);
        _invisibleSet.Remove(actor);
        _packByActor.Remove(actor);

        if (!_targetByActor.Remove(actor, out FrameObjectBase? target)) return null;
        _actorByTarget.Remove(target);
        Unclaim(target, new HashSet<FrameObjectBase>());
        return target;
    }

    /// <summary>
    /// Re-attaches an actor dropped by <see cref="Detach"/> (undo), restoring its placement — and the pack it
    /// belongs to, which every later edit of it goes through. Leaving that out is silent: the actor comes back
    /// in the tree and in the viewport, but <see cref="PackOf"/> returns null for it, and the editor's delete
    /// and duplicate both skip an actor with no pack — so undoing a delete once made that actor permanently
    /// un-deletable for the rest of the session, with no error to show for it.
    /// </summary>
    public void Attach(ActorEntry actor, ActorsFile pack, FrameObjectBase? target, int index, bool hadGlyph)
    {
        _allList.Insert(Math.Clamp(index, 0, _allList.Count), actor);
        _packByActor[actor] = pack;
        if (hadGlyph && _invisibleSet.Add(actor)) _invisibleList.Add(actor);

        if (target == null) return;
        _targetByActor[actor] = target;
        _actorByTarget[target] = actor;
        Claim(target, actor, _actorByCoveredFrame);
        Respread(target, actor.Transform, _byFrame, new HashSet<FrameObjectBase>());
    }

    /// <summary>
    /// Registers an actor the editor just created (a copy), right after <paramref name="after"/>.
    /// <paramref name="target"/> is the clone of the object it places, or null when it places nothing and is
    /// drawn as a glyph instead. A copy that places an object gets a glyph too when that object turns out to
    /// carry no mesh — the same rule the initial resolve uses, so a trigger's copy stays reachable.
    /// </summary>
    public void AddCopy(ActorEntry copy, ActorEntry after, ActorsFile pack, FrameObjectBase? target = null)
    {
        ArgumentNullException.ThrowIfNull(copy);
        int index = _allList.IndexOf(after);
        _allList.Insert(index < 0 ? _allList.Count : index + 1, copy);
        _packByActor[copy] = pack;

        if (target != null)
        {
            _targetByActor[copy] = target;
            _actorByTarget[target] = copy;
            Claim(target, copy, _actorByCoveredFrame);
            Respread(target, copy.Transform, _byFrame, new HashSet<FrameObjectBase>());
            if (HasMesh(target, new HashSet<FrameObjectBase>())) return;
        }
        if (_invisibleSet.Add(copy)) _invisibleList.Add(copy);
    }

    /// <summary>
    /// Registers an actor brought in from another archive's pack. It places nothing of this scene — that is
    /// the condition for importing it — so it is one of the invisible ones and gets a glyph.
    /// </summary>
    public void AddImported(ActorEntry actor, ActorsFile pack)
    {
        ArgumentNullException.ThrowIfNull(actor);
        _allList.Add(actor);
        _packByActor[actor] = pack;
        if (_invisibleSet.Add(actor)) _invisibleList.Add(actor);
    }

    /// <summary>
    /// Re-points every scene reference at where its frame object actually sits now, just before the packs are
    /// written. A reference stores a position in the frame resource's object list, and nothing recomputes it:
    /// a copy adds an object, an undone delete puts one back in a rebuilt order, and a reference captured
    /// earlier would then name a different object — silently placing the wrong thing. For an untouched
    /// archive every index is rewritten to the value it already had, so the pack still saves byte for byte.
    /// </summary>
    public void RefreshFrameIndices()
    {
        if (_resource?.FrameObjects == null) return;

        var indexByFrame = new Dictionary<FrameObjectBase, uint>(_resource.FrameObjects.Count);
        uint at = 0;
        foreach (object value in _resource.FrameObjects.Values)
        {
            if (value is FrameObjectBase frame) indexByFrame[frame] = at;
            at++;
        }

        foreach (KeyValuePair<ActorEntry, FrameObjectBase> pair in _targetByActor)
        {
            if (pair.Key.FrameHash == 0) continue;
            if (!indexByFrame.TryGetValue(pair.Value, out uint index)) continue;
            if (_packByActor.TryGetValue(pair.Key, out ActorsFile? pack))
            {
                foreach (ActorSceneReference reference in pack.SceneReferences)
                {
                    if (reference.FrameHash == pair.Key.FrameHash) reference.FrameIndex = index;
                }
            }
        }
    }

    /// <summary>What a frame reference id points at in this scene — a scene folder, an object, or nothing.
    /// For diagnostics: the parent slots are where a hand-built object goes wrong.</summary>
    public string DescribeRef(int refId)
    {
        if (_resource == null) return "(no scene)";
        if (_resource.FrameScenes.TryGetValue(refId, out Formats.Frames.Resources.FrameHeaderScene? scene))
        {
            return $"scene '{scene.Name}'";
        }
        if (_resource.FrameObjects.TryGetValue(refId, out object? value) && value is FrameObjectBase frame)
        {
            return $"{frame.GetType().Name} '{frame.Name}'";
        }
        return $"(dangling #{refId})";
    }

    /// <summary>Resolves packs against the same scene these placements were built from — how a caller checks
    /// that an edited pack, written and read back, still finds the objects it places.</summary>
    public ActorPlacements ResolveAgain(IReadOnlyList<ActorsFile> packs) =>
        _resource == null ? Empty : Build(packs, _resource);

    private FrameResource? _resource;

    private void Unclaim(FrameObjectBase frame, HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return;
        _byFrame.Remove(frame);
        _actorByCoveredFrame.Remove(frame);
        foreach (FrameObjectBase child in frame.Children) Unclaim(child, seen);
    }

    /// <summary>
    /// Reads every actor pack the extracted folder lists and resolves it against <paramref name="resource"/>.
    /// A pack that cannot be read is skipped — a scene still loads without its actors, just unplaced.
    /// </summary>
    public static ActorPlacements Load(SdsManifest manifest, FrameResource resource)
    {
        var packs = new List<ActorsFile>();
        var files = new List<(ActorsFile, string)>();
        foreach (string path in manifest.GetFiles("Actors"))
        {
            try
            {
                ActorsFile pack = ActorsFile.Load(path);
                packs.Add(pack);
                files.Add((pack, path));
            }
            catch (Exception) { /* an unreadable pack costs placement, never the scene */ }
        }
        return Build(packs, resource, files);
    }

    /// <summary>Resolves already-loaded packs. Separate from <see cref="Load"/> so the probes can feed
    /// packs in directly.</summary>
    public static ActorPlacements Build(IReadOnlyList<ActorsFile> packs, FrameResource resource,
        IReadOnlyList<(ActorsFile Pack, string Path)>? files = null)
    {
        if (packs.Count == 0 || resource.FrameObjects == null) return Empty;

        // FrameIndex is the position within FrameObjects — the same index space the FrameNameTable uses.
        var objects = new List<FrameObjectBase?>(resource.FrameObjects.Count);
        foreach (object value in resource.FrameObjects.Values) objects.Add(value as FrameObjectBase);

        var byFrame = new Dictionary<FrameObjectBase, Matrix4x4>();
        var actorByTarget = new Dictionary<FrameObjectBase, ActorEntry>();
        var targetByActor = new Dictionary<ActorEntry, FrameObjectBase>();
        var actorByCoveredFrame = new Dictionary<FrameObjectBase, ActorEntry>();
        var all = new List<ActorEntry>();
        var invisible = new List<ActorEntry>();
        var packByActor = new Dictionary<ActorEntry, ActorsFile>();
        int unresolved = 0;

        foreach (ActorsFile pack in packs)
        {
            var frameIndexByHash = new Dictionary<ulong, uint>();
            foreach (ActorSceneReference reference in pack.SceneReferences)
            {
                frameIndexByHash[reference.FrameHash] = reference.FrameIndex;
            }

            foreach (ActorEntry actor in pack.Actors)
            {
                all.Add(actor);
                packByActor[actor] = pack;
                if (!actor.IsTyped) { unresolved++; invisible.Add(actor); continue; }

                // An uncompressed pack stores no hashes — derive the key from the name it does store.
                ulong key = actor.FrameHash != 0
                    ? actor.FrameHash
                    : actor.LinkedFrame.Length > 0 ? Fnv64.Hash(actor.LinkedFrame) : 0;

                if (key == 0 || !frameIndexByHash.TryGetValue(key, out uint frameIndex)
                    || frameIndex >= objects.Count || objects[(int)frameIndex] is not { } target)
                {
                    unresolved++;
                    invisible.Add(actor);
                    continue;
                }

                // Two actors targeting one frame do not occur in the shipped game; if a mod ever does it,
                // the first one wins rather than the last, so the result stays deterministic.
                if (!actorByTarget.TryAdd(target, actor)) continue;
                targetByActor[actor] = target;
                Spread(target, actor.Transform, byFrame);
                Claim(target, actor, actorByCoveredFrame);

                // Resolved is not the same as visible: most items, detectors and blockers point at an empty
                // holder frame. Those get a glyph too, otherwise they are unreachable in the viewport.
                if (!HasMesh(target, new HashSet<FrameObjectBase>())) invisible.Add(actor);
            }
        }

        return new ActorPlacements(byFrame, actorByTarget)
        {
            UnresolvedCount = unresolved,
            _allList = all,
            _invisibleList = invisible,
            _invisibleSet = new HashSet<ActorEntry>(invisible),
            _packByActor = packByActor,
            Packs = files ?? [],
            _targetByActor = targetByActor,
            _actorByCoveredFrame = actorByCoveredFrame,
            _resource = resource,
        };
    }

    // Records the whole subtree as governed by this actor, so a click on any mesh under it resolves back to
    // the actor rather than to the prototype frame.
    private static void Claim(FrameObjectBase frame, ActorEntry actor, Dictionary<FrameObjectBase, ActorEntry> into)
    {
        if (!into.TryAdd(frame, actor)) return;
        foreach (FrameObjectBase child in frame.Children) Claim(child, actor, into);
    }

    private static bool HasMesh(FrameObjectBase frame, HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return false;
        if (frame is FrameObjectSingleMesh { Geometry: not null }) return true;
        foreach (FrameObjectBase child in frame.Children)
        {
            if (HasMesh(child, seen)) return true;
        }
        return false;
    }

    // The actor moves the whole subtree, not just the node it names: the prototype is an empty holder and
    // its meshes/collisions hang under it. Guarded against the cycles a malformed hierarchy can carry.
    // TryAdd doubles as the cycle guard here: the first claim wins, which is also what stops two actors from
    // fighting over one subtree.
    private static void Spread(FrameObjectBase frame, Matrix4x4 placement, Dictionary<FrameObjectBase, Matrix4x4> into)
    {
        if (!into.TryAdd(frame, placement)) return;
        foreach (FrameObjectBase child in frame.Children) Spread(child, placement, into);
    }

    // The edit path: the frames are already claimed, only the matrix changes — so the cycle guard has to be
    // its own visited set rather than "already present".
    private static void Respread(FrameObjectBase frame, Matrix4x4 placement,
        Dictionary<FrameObjectBase, Matrix4x4> into, HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return;
        into[frame] = placement;
        foreach (FrameObjectBase child in frame.Children) Respread(child, placement, into, seen);
    }
}
