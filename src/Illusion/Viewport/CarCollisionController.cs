using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Collisions;
using Illusion.Domain;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Showing what a car is solid with — the overlay layer, and nothing else.
///
/// <para>
/// A car's mesh is not what a bullet hits: its collision is a handful of volumes the PREFAB hangs off each
/// deformable part, and this draws them where the game reads them, one colour per kind. Adding, resizing and
/// removing one is the component tree's, through the aggregate; this controller used to do it too and that
/// made it one of the paths that reached the prefab on their own.
/// </para>
/// </summary>
internal sealed class CarCollisionController
{
    private readonly D3DImageHost _host;

    internal CarCollisionController(D3DImageHost host) => _host = host;

    /// <summary>Whether the overlay is drawing the car's collision.</summary>
    internal bool ShowShapes
    {
        get => _show;
        set
        {
            _show = value;
            RefreshOverlay();
        }
    }

    private bool _show;
    private readonly object _compiledWorldKey = new();
    private MeshData[] _compiledWorldMeshes = [];
    private long _compiledWorldRevision;


    /// <summary>
    /// The volumes of each loaded document, kept between redraws. Reading them means opening the archive's
    /// prefab and every ItemDesc file it lists, and the overlay is now redrawn on every frame of a gizmo
    /// drag — doing that off disk each time would put a dozen file reads inside the drag loop.
    /// </summary>
    private readonly Dictionary<SceneDocumentAdapter, IReadOnlyList<PlacedPhysicsVolume>> _cache = new();

    /// <summary>
    /// Redraws after re-reading the archives. This is the DEFAULT: anything that could have changed a volume
    /// — an add, a remove, a save, a scene change, a number typed into the shape editor — goes through here,
    /// so the cache can never serve a stale shape. Only the gizmo drag takes the cached path, and a drag is
    /// exactly the moment when nothing on disk moves.
    /// </summary>
    internal void RefreshOverlay()
    {
        _cache.Clear();
        Redraw();
    }

    /// <summary>Redraws from what was already read — for the inside of a gizmo drag, which runs per frame.</summary>
    internal void RefreshOverlayWhileDragging() => Redraw();

    /// <summary>
    /// Takes the prefab as the truth again, then redraws — for an edit made through the NUMBERS rather than
    /// through the scene: a collision's size and position typed on its component's row, and the undo of one.
    ///
    /// <para>
    /// Two things go stale on such an edit and only one of them is the cache. A volume that has a stub is
    /// drawn where the STUB is, because the stub is the handle a gizmo drags; typing a new position into the
    /// panel moves the prefab and leaves that handle behind, so the box would sit still no matter what the
    /// number said. Snapping the stubs onto the prefab is what makes the two agree again — the same repair
    /// the loader performs, and safe here for the same reason it is unsafe in
    /// <see cref="RefreshOverlay"/>: nothing is mid-drag, so there is no newer placement to overwrite.
    /// </para>
    /// </summary>
    internal void AdoptPrefabPlacements()
    {
        foreach (SceneDocumentAdapter document in Documents()) document.AdoptPrefabPlacements();
        RefreshOverlay();
    }

    /// <summary>
    /// Drops everything read so far — for a scene reset, where the documents are being unloaded.
    ///
    /// <para>
    /// The cache is keyed by document instance and a reload mints new ones, so a stale entry was never
    /// served; it was simply held. Letting go of it at the reset is what keeps a restored archive from
    /// carrying the memory of the one it replaced.
    /// </para>
    /// </summary>
    internal void Forget() => _cache.Clear();

    /// <summary>
    /// Redraws the car's collision as the PREFAB describes it, which is the copy the game reads.
    ///
    /// <para>
    /// EVERYTHING the loaded cars carry, not just the selected part. Scoping it to the selection meant the
    /// answer to "where is this car solid" depended on what happened to be clicked, and a shape you have to
    /// hunt for by selecting bones one at a time is a shape you cannot judge.
    /// </para>
    /// <para>
    /// A volume that has a stub frame is drawn where the STUB is rather than where the prefab says: the stub
    /// is the handle being dragged, and the save is what makes the two agree. One with no stub — every
    /// window, every snow volume — is drawn where the prefab puts it.
    /// </para>
    /// </summary>
    internal void RefreshSharedWorldChanges()
    {
        if (!_show) return;
        long revision = 0;
        foreach (var document in _host.FrameDocumentNodes().Select(node=>node.Source)
                     .OfType<Illusion.Assets.Compiled.CompiledSceneDocument>().Distinct())
            revision = unchecked(revision + document.WorldCollisionRevision);
        if (revision != _compiledWorldRevision) Redraw();
    }

    private void Redraw()
    {
        if (_host.Rnd is not { } renderer) return;
        renderer.ShowPartShapes = _show;
        if (!_show) { renderer.SetPartShapeLines([], []); renderer.RemoveCollisionDistrict(_compiledWorldKey); _compiledWorldMeshes = []; return; }

        var lines = new List<Vector3>();
        var colors = new List<Vector4>();
        foreach (SceneDocumentAdapter document in Documents())
        {
            if (!_cache.TryGetValue(document, out IReadOnlyList<PlacedPhysicsVolume>? volumes))
            {
                _cache[document] = volumes = document.PhysicsVolumes();
            }
            foreach (PlacedPhysicsVolume volume in volumes)
            {
                Matrix4x4 world = volume.Stub != null
                    ? document.Node(volume.Stub).WorldTransform
                    : volume.World;

                int before = lines.Count;
                if (volume.Shape != null) CarCollisionShapes.AppendWireframe(lines, volume.Shape, world);
                else CarCollisionShapes.AppendBox(lines, volume.Volume.Size * 0.5f, world);

                // One colour per volume, because a car's collision is several different things drawn on top
                // of one another and an unlabelled wireframe cannot say which is which.
                Vector4 tint = ColorOf(volume);
                for (int i = before; i < lines.Count; i++) colors.Add(tint);
            }
        }
        var seenCompiledMeshes = new HashSet<MeshData>(ReferenceEqualityComparer.Instance);
        var worldMeshes = new List<MeshData>();
        long worldRevision = 0;
        foreach (var document in _host.FrameDocumentNodes().Select(n => n.Source)
                     .OfType<Illusion.Assets.Compiled.CompiledSceneDocument>().Distinct())
        {
            foreach (MeshData mesh in document.CollisionMeshes().Where(seenCompiledMeshes.Add))
            {
                if (mesh.Instances is not null) { worldMeshes.Add(mesh); continue; }
                for (int triangle = 0; triangle + 2 < mesh.Indices.Length; triangle += 3)
                    for (int edge = 0; edge < 3; edge++)
                    {
                        uint first = mesh.Indices[triangle + edge];
                        uint second = mesh.Indices[triangle + (edge + 1) % 3];
                        lines.Add(Vector3.Transform(mesh.Positions[first], mesh.World));
                        lines.Add(Vector3.Transform(mesh.Positions[second], mesh.World));
                        colors.Add(new Vector4(1f, 0.45f, 0.25f, 0.95f));
                        colors.Add(new Vector4(1f, 0.45f, 0.25f, 0.95f));
                    }
            }
            worldRevision = unchecked(worldRevision + document.WorldCollisionRevision);
        }
        if (_compiledWorldRevision != worldRevision || !_compiledWorldMeshes.SequenceEqual(worldMeshes) || (worldMeshes.Count > 0 && !renderer.HasCollisionData))
        {
            renderer.SetCollisionDistrict(_compiledWorldKey, new CollisionRenderData
            {
                Meshes = worldMeshes.Select((mesh, index) => new CollisionRenderMesh
                {
                    Hash = (ulong)index + 1, Positions = mesh.Positions, Indices = mesh.Indices,
                    SourceTriangle = Enumerable.Range(0, mesh.Indices.Length / 3).ToArray(),
                    Parts = [new CollisionRenderPart(0, mesh.Indices.Length, 0, new Vector3(1, .45f, .25f))],
                    Instances = mesh.Instances!, LocalMin = mesh.Positions.Aggregate(Vector3.Min), LocalMax = mesh.Positions.Aggregate(Vector3.Max)
                }).ToArray()
            });
            _compiledWorldMeshes = worldMeshes.ToArray(); _compiledWorldRevision = worldRevision;
        }
        renderer.SetPartShapeLines(lines, colors);
    }

    /// <summary>
    /// What colour a volume is drawn in — its KIND, and its surface when it names one.
    ///
    /// <para>
    /// The kinds are not variants of one thing. A type-5 volume places a physics shape and is the body, the
    /// doors, the bumpers; a type-0 volume IS the glass (all 527 shipped window volumes are type 0 and none
    /// is anything else); type 6 is a zone — the engine bay, the snow. A shape that names a physics surface
    /// is a different thing again, and takes that surface's own colour from the catalog the world's collision
    /// is painted with, so the two layers read the same way.
    /// </para>
    /// </summary>
    private static Vector4 ColorOf(PlacedPhysicsVolume volume)
    {
        if (volume.Volume.NamesShape)
        {
            // A surface that was asked for wins: it is the one thing here a person set deliberately, and
            // seeing it is how they know it took.
            ushort raw = (volume.Shape?.Element as Illusion.Formats.ItemDesc.RigidBodyElement)?.MaterialId ?? 0;
            if (raw != 0)
            {
                Vector3 named = CollisionMaterialCatalog.ColorForRawId(raw);
                return new Vector4(named, 0.95f);
            }
            return new Vector4(1f, 0.45f, 0.25f, 0.95f);     // solid, no surface named — the old orange
        }

        return volume.Volume.VolumeType switch
        {
            0 => new Vector4(0.42f, 0.80f, 0.95f, 0.75f),    // glass
            6 => new Vector4(0.95f, 0.80f, 0.35f, 0.60f),    // a zone: engine bay, snow
            _ => new Vector4(0.70f, 0.55f, 0.95f, 0.60f),    // the rare type 2, so it is never mistaken
        };
    }

    /// <summary>Every frame document on the stage — for callers outside this controller that need the open
    /// graph rather than the file (a frame minted this session, which is on disk only after a save).</summary>
    internal IEnumerable<SceneDocumentAdapter> StageDocuments() => Documents();

    /// <summary>Every frame document on the stage right now. A document with no car prefab answers with an
    /// empty list and costs one manifest read, which is why this can afford to ask all of them.</summary>
    private IEnumerable<SceneDocumentAdapter> Documents()
    {
        var seen = new HashSet<SceneDocumentAdapter>();
        foreach (SceneNode root in _host.Tree.Roots)
        {
            foreach (SceneDocumentAdapter document in DocumentsUnder(root, seen)) yield return document;
        }
    }

    private static IEnumerable<SceneDocumentAdapter> DocumentsUnder(SceneNode node, HashSet<SceneDocumentAdapter> seen)
    {
        if (node.Source is SceneDocumentAdapter document && seen.Add(document)) yield return document;
        foreach (SceneNode child in node.Children)
        {
            foreach (SceneDocumentAdapter found in DocumentsUnder(child, seen)) yield return found;
        }
    }

}
