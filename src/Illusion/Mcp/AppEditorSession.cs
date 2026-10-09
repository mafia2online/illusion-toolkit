using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Assets.World;
using Illusion.Domain;
using Illusion.Rendering.Gizmos;
using Illusion.Scene;
using Illusion.Viewport;
using Illusion.Views;
using RenderMode = Illusion.Rendering.Passes.RenderMode;

namespace Illusion.Mcp;

/// <summary>
/// The application's answer to <see cref="IEditorSession"/>: the open <see cref="MainWindow"/> and its
/// viewport, found afresh on every call — the launcher and the editor replace one another, so a window
/// captured once would be a closed one by the next question.
/// <para>
/// Where the window has a control for something (the area selector, the season, the layer switches, the
/// shading mode) the control is what gets set, so the screen the user is looking at never disagrees with
/// what a client did behind it. Save and Build go to the viewport directly: the window's own handlers end
/// in a modal dialog, and a dialog nobody is there to dismiss would park the UI thread — and the server
/// with it.
/// </para>
/// Every member runs on the UI thread; see the interface.
/// </summary>
internal sealed class AppEditorSession : IEditorSession
{
    private const string NotOpen = "the map editor is not open — call editor_open_area first";

    private static MainWindow? Window => Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();

    private static D3DImageHost Host =>
        TargetHost ?? throw new InvalidOperationException(TargetNotOpen);

    // Which editor the scene tools drive: the map editor, or the resource editor once resource_open (or
    // editor_target) has pointed them there. Both are the same kind of viewport, so every tool that only works
    // on "the scene" — find, select, properties, move, duplicate, delete, camera, screenshot, save, build,
    // undo, Blender — works on either; the ones about districts stay with the map.
    private static bool _resourceTarget;

    private static ResourceEditorWindow? ResourceWindow =>
        Application.Current.Windows.OfType<ResourceEditorWindow>().FirstOrDefault();

    private static D3DImageHost? TargetHost => _resourceTarget ? ResourceWindow?.Stage : Window?.Viewport;

    private static string TargetNotOpen => _resourceTarget
        ? "the resource editor is not open — resource_open first"
        : NotOpen;

    public string? SetTarget(string target)
    {
        switch (target.Trim().ToLowerInvariant())
        {
            case "map":
                _resourceTarget = false;
                return Window == null ? "the target is the map now, but the map editor is not open — editor_open_area" : null;
            case "resource":
                _resourceTarget = true;
                return ResourceWindow == null ? "the target is the resource editor now, but it is not open — resource_open" : null;
            default:
                return $"'{target}' is neither 'map' nor 'resource'";
        }
    }

    public ResourceStatus ResourceStatus()
    {
        if (ResourceWindow is not { } window)
        {
            return new ResourceStatus(false, _resourceTarget ? "resource" : "map", null, null, false, 0, [], false, [], 0, "");
        }
        D3DImageHost stage = window.Stage;
        return new ResourceStatus(
            Open: true,
            Target: _resourceTarget ? "resource" : "map",
            Archive: window.StagedEntry?.Name,
            ArchivePath: window.StagedEntry?.File.FullName,
            Loading: stage.IsLoading,
            Meshes: stage.MeshCount,
            Selection: stage.SelectedNodes.Select(n => n.Name).ToList(),
            UnsavedEdits: stage.HasUnsavedEdits,
            PendingBuild: stage.PendingBuildArchives().Select(f => f.Name).ToList(),
            BlenderObjects: stage.BridgeEditedCount,
            RenderMode: stage.RenderMode.ToString());
    }

    // The game folder, set up from the launcher when no editor has done it yet.
    private static string? EnsureEnvironment()
    {
        if (Assets.MafiaEnvironment.IsInitialized) return null;
        return Application.Current.Windows.OfType<LauncherWindow>().FirstOrDefault() is { } launcher
            ? launcher.PrepareEnvironment()
            : "the game folder is not set yet — open the toolkit's launcher first";
    }

    public string? OpenResource(string archive, out string? archivePath)
    {
        archivePath = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        var sds = new FileInfo(Path.IsPathRooted(archive)
            ? archive
            : Path.Combine(Assets.MafiaEnvironment.PcFolder, "sds", archive.EndsWith(".sds", StringComparison.OrdinalIgnoreCase)
                ? archive
                : archive + ".sds"));
        if (!sds.Exists)
        {
            // A bare name: look it up in the library, the way the browser's search would.
            string wanted = Path.GetFileNameWithoutExtension(archive);
            Assets.Library.LibraryEntry? hit = Assets.Library.LibraryCatalog
                .Build(Path.Combine(Assets.MafiaEnvironment.PcFolder, "sds")).AllEntries
                .FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return $"no archive '{archive}' — resource_list finds them";
            sds = hit.File;
        }

        if (ResourceWindow == null)
        {
            // From the launcher the editor takes its place, the way the tile does; beside the map editor it is
            // a second window, the way the File menu opens it.
            if (Application.Current.Windows.OfType<LauncherWindow>().FirstOrDefault() is { } launcher)
            {
                if (launcher.OpenResourceEditor() is { } failed) return failed;
            }
            else
            {
                new ResourceEditorWindow().Show();
            }
        }
        if (ResourceWindow is not { } window) return "the resource editor did not open";
        if (window.TargetStage is { } stage && stage.BridgeEditedCount > 0) return "a Blender edit session is open there — blender_end first";

        // Already on the stage: nothing to load. Staging it again would reload it — and a reload empties the
        // undo history, so the editor_undo after a few car_tuning_set calls found nothing to undo.
        if (string.Equals(window.StagedEntry?.File.FullName, sds.FullName, StringComparison.OrdinalIgnoreCase))
        {
            _resourceTarget = true;
            archivePath = sds.FullName;
            return null;
        }
        // The window answers two situations with a dialog, and a dialog here is a tool call that does not
        // return until a person clicks. Both are settled before it is asked.
        if (Assets.Sds.OpenArchives.IsHeldByAnyoneElse(sds, window.Stage))
        {
            return $"{sds.Name} is loaded in another editor window — two editors on one working copy overwrite each "
                + "other's saves. Work on it there (editor_target map), or load something else there first";
        }
        if (window.Stage.HasUnsavedEdits)
        {
            D3DImageHost.SaveReport saved = window.Stage.SaveEditsReport();
            if (!saved.Complete)
            {
                return "what is on the stage has edits that could not be saved, and opening another archive would drop them: "
                    + string.Join("; ", saved.NotSaved);
            }
        }
        window.Reveal(sds);
        _resourceTarget = true;
        archivePath = sds.FullName;
        return null;
    }

    public IReadOnlyList<LibraryItem> Library(string? query, string? folder, int limit)
    {
        if (EnsureEnvironment() is { } notReady) throw new InvalidOperationException(notReady);
        return Assets.Library.LibraryCatalog.Build(Path.Combine(Assets.MafiaEnvironment.PcFolder, "sds")).AllEntries
            .Where(e => (query == null || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        && (folder == null || e.FolderPath.Contains(folder, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Max(1, limit))
            .Select(e => new LibraryItem(e.Name, Path.GetRelativePath(Path.Combine(Assets.MafiaEnvironment.PcFolder, "sds"), e.File.FullName),
                e.Resource.ToString(), e.Size,
                File.Exists(Path.Combine(Assets.MafiaEnvironment.ExtractedDir(e.File), "SDSContent.xml"))))
            .ToList();
    }

    public EditorStatus Status()
    {
        if (Window is not { } window)
        {
            return new EditorStatus(false, null, false, false, 0, [], false, [], 0, "", _resourceTarget ? "resource" : "map");
        }

        D3DImageHost host = window.Viewport;
        string? area = window.WholeMapCheck.IsChecked == true
            ? "(whole map)"
            : (window.AreaCombo.SelectedItem as MapArea)?.BaseName;
        return new EditorStatus(
            EditorOpen: true,
            Area: area,
            Winter: window.WinterToggle.IsChecked == true,
            Loading: host.IsLoading,
            Meshes: host.MeshCount,
            Selection: host.SelectedNodes.Select(n => n.Name).ToList(),
            Target: _resourceTarget ? "resource" : "map",
            UnsavedEdits: host.HasUnsavedEdits,
            PendingBuild: host.PendingBuildArchives().Select(f => f.Name).ToList(),
            BlenderObjects: host.BridgeEditedCount,
            RenderMode: host.RenderMode.ToString());
    }

    public IReadOnlyList<string> Areas() =>
        Window is { } window ? window.Viewport.Areas.Select(a => a.BaseName).ToList() : [];

    public string? EnsureEditor()
    {
        if (Window != null) return null;
        return Application.Current.Windows.OfType<LauncherWindow>().FirstOrDefault() is { } launcher
            ? launcher.OpenMapEditor()
            : "neither the launcher nor the map editor is open";
    }

    public string? LoadArea(string area, bool winter, bool discardUnsavedEdits)
    {
        if (Window is not { } window) return NotOpen;
        D3DImageHost host = window.Viewport;
        if (host.BridgeEditedCount > 0) return "a Blender edit session is open — blender_end first";

        MapArea? target = host.Areas.FirstOrDefault(
            a => string.Equals(a.BaseName, area, StringComparison.OrdinalIgnoreCase));
        if (target == null) return $"no area named '{area}' — editor_list_areas has the names";
        if (winter && target.Winter == null) return $"'{target.BaseName}' has no winter variant";

        // A reload resets the scene: the edited frames, the list of what is unsaved and the undo history all
        // go with it, and nothing says so. Asking for the area already on screen reloads nothing and is
        // always allowed; anything else has to be told that the edits may go.
        bool reloads = window.WholeMapCheck.IsChecked == true
            || (window.WinterToggle.IsChecked == true) != winter
            || !ReferenceEquals(window.AreaCombo.SelectedItem, target);
        if (reloads && host.HasUnsavedEdits && !discardUnsavedEdits)
        {
            return "the scene holds unsaved edits, and loading another area or season drops them together "
                + "with the undo history — editor_save first, or pass discardUnsavedEdits=true to give them up";
        }

        // Each control reloads the scene when it changes and only then, so an area that is already the one
        // shown costs nothing here. The season goes first: the area selector's reload then reads it.
        if (window.WholeMapCheck.IsChecked == true) window.WholeMapCheck.IsChecked = false;
        if ((window.WinterToggle.IsChecked == true) != winter) window.WinterToggle.IsChecked = winter;
        if (!ReferenceEquals(window.AreaCombo.SelectedItem, target)) window.AreaCombo.SelectedItem = target;
        // Opening an area is asking for the map, the way resource_open is asking for the resource editor.
        // Left on the resource editor, the find, select, delete, save and build that follow an
        // editor_open_area went on acting on whatever car was on its stage.
        _resourceTarget = false;
        return null;
    }

    public IReadOnlyList<SceneObjectInfo> Find(
        string? nameContains, string? kind, float[]? boxMin, float[]? boxMax, int limit)
    {
        D3DImageHost host = Host;
        Vector3? lo = boxMin is { Length: 3 } ? new Vector3(boxMin[0], boxMin[1], boxMin[2]) : null;
        Vector3? hi = boxMax is { Length: 3 } ? new Vector3(boxMax[0], boxMax[1], boxMax[2]) : null;

        var found = new List<SceneObjectInfo>();
        foreach (SceneNode node in AllNodes(host))
        {
            if (found.Count >= limit) break;
            // A crash copy only has a tree node once it has been clicked or its row expanded, so the tree
            // cannot answer for the copies — they are searched from the placement data below.
            if (node.Kind == CrashCopyKind) continue;
            if (nameContains != null && !node.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (kind != null && !string.Equals(node.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;

            // What the row stands where: a drawn mesh, or — for a collision placement — its hull. An instanced
            // mesh has neither: its bounds span every copy across the map, not where this row is.
            Shape? shape = ShapeOf(node, lo is { } boxLo && hi is { } boxHi ? (boxLo, boxHi) : null);
            Vector3? position = node.Source is IFrameNode frame ? frame.WorldTransform.Translation : null;
            int? triangles = null;
            Vector3 insideMin = default, insideMax = default;
            if (lo is { } min && hi is { } max)
            {
                if (shape is { } solid)
                {
                    if (!Overlaps(solid.Min, solid.Max, min, max)) continue;
                    triangles = TrianglesInBox(solid, min, max, out insideMin, out insideMax);
                    if (triangles == 0) continue;   // its box reaches in; its geometry does not
                }
                else if (position is not { } p || !Overlaps(p, p, min, max))
                {
                    continue;
                }
            }

            found.Add(new SceneObjectInfo(
                node.Name, node.Kind, PathOf(node),
                position is { } at ? [at.X, at.Y, at.Z] : null,
                shape is { } b0 ? [b0.Min.X, b0.Min.Y, b0.Min.Z] : null,
                shape is { } b1 ? [b1.Max.X, b1.Max.Y, b1.Max.Z] : null,
                node.IsSelected,
                triangles,
                triangles > 0 ? [insideMin.X, insideMin.Y, insideMin.Z] : null,
                triangles > 0 ? [insideMax.X, insideMax.Y, insideMax.Z] : null,
                shape?.Positions?.Length ?? node.Mesh?.PickPositions?.Length,
                (shape?.Indices?.Length ?? node.Mesh?.PickIndices?.Length) / 3));
        }

        if (kind == null || string.Equals(kind, CrashCopyKind, StringComparison.OrdinalIgnoreCase))
            FindCrashCopies(host, nameContains, lo, hi, limit, found);
        return found;
    }

    private const string CrashCopyKind = "CrashInstance";

    /// <summary>
    /// The crash layer's copies — trees, lamps, bins: 57 000 of them in the shipped city — found from the
    /// placement table itself. A copy matches a name by its own label ("copy #id") or by the prop it is a
    /// copy of, and a box by its prototype's TRIANGLES stood at the copy's matrix, the same test a mesh gets:
    /// "is this volume free" is asked of what is drawn, and a lamp post is drawn well away from its origin.
    /// Only the copies that are returned are given a tree node (which is what a later select names).
    /// </summary>
    private static void FindCrashCopies(
        D3DImageHost host, string? nameContains, Vector3? lo, Vector3? hi, int limit, List<SceneObjectInfo> found)
    {
        foreach ((Formats.Translokator.Object row, IReadOnlyList<(Rendering.Gpu.GpuMesh Mesh, Matrix4x4 Local)> prototypes)
                 in host.Streamer.CrashRows())
        {
            bool propNamed = nameContains == null
                || row.Name.String.Contains(nameContains, StringComparison.OrdinalIgnoreCase);
            foreach (Formats.Translokator.Instance copy in row.Instances)
            {
                if (found.Count >= limit) return;
                if (!propNamed && !$"copy #{copy.ID}".Contains(nameContains!, StringComparison.OrdinalIgnoreCase)) continue;

                int? triangles = null;
                Vector3 insideMin = default, insideMax = default;
                if (lo is { } min && hi is { } max)
                {
                    if (prototypes.Count == 0)
                    {
                        // Nothing to test but where it stands (its prototype keeps no CPU geometry).
                        if (!Overlaps(copy.Position, copy.Position, min, max)) continue;
                    }
                    else
                    {
                        Matrix4x4 placed = DistrictStreamer.CrashWorld(copy);
                        int count = 0;
                        insideMin = new Vector3(float.MaxValue);
                        insideMax = new Vector3(float.MinValue);
                        foreach ((Rendering.Gpu.GpuMesh mesh, Matrix4x4 local) in prototypes)
                        {
                            count += TrianglesInBox(mesh, local * placed, min, max, ref insideMin, ref insideMax);
                        }
                        if (count == 0) continue;
                        triangles = count;
                    }
                }

                if (host.Streamer.CrashNodeFor(copy, row) is not { } node) continue;
                found.Add(new SceneObjectInfo(
                    node.Name, node.Kind, PathOf(node),
                    [copy.Position.X, copy.Position.Y, copy.Position.Z],
                    null, null,
                    node.IsSelected,
                    triangles,
                    triangles > 0 ? [insideMin.X, insideMin.Y, insideMin.Z] : null,
                    triangles > 0 ? [insideMax.X, insideMax.Y, insideMax.Z] : null));
            }
        }
    }

    // A prototype's triangles at one copy's matrix against a box, the copy's own box asked first: a query
    // walks every copy in the city, and all but a handful are nowhere near.
    private static int TrianglesInBox(
        Rendering.Gpu.GpuMesh mesh, Matrix4x4 world, Vector3 boxMin, Vector3 boxMax,
        ref Vector3 insideMin, ref Vector3 insideMax)
    {
        if (mesh.PickPositions is not { } positions || mesh.PickIndices is not { } indices) return 0;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int k = 0; k < 8; k++)
        {
            Vector3 corner = Vector3.Transform(
                new Vector3(
                    (k & 1) == 0 ? mesh.LocalMin.X : mesh.LocalMax.X,
                    (k & 2) == 0 ? mesh.LocalMin.Y : mesh.LocalMax.Y,
                    (k & 4) == 0 ? mesh.LocalMin.Z : mesh.LocalMax.Z),
                world);
            min = Vector3.Min(min, corner);
            max = Vector3.Max(max, corner);
        }
        if (!Overlaps(min, max, boxMin, boxMax)) return 0;

        int count = 0;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            Vector3 a = Vector3.Transform(positions[indices[i]], world);
            Vector3 b = Vector3.Transform(positions[indices[i + 1]], world);
            Vector3 c = Vector3.Transform(positions[indices[i + 2]], world);
            if (!TriangleBoxTest.Overlaps(a, b, c, boxMin, boxMax)) continue;
            count++;
            insideMin = Vector3.Min(insideMin, Vector3.Max(boxMin, Vector3.Min(a, Vector3.Min(b, c))));
            insideMax = Vector3.Max(insideMax, Vector3.Min(boxMax, Vector3.Max(a, Vector3.Max(b, c))));
        }
        return count;
    }

    /// <summary>The triangles a row occupies and their world-space bounds. Positions are null for a mesh that
    /// keeps no CPU geometry — its bounds still stand.</summary>
    private readonly record struct Shape(Vector3[]? Positions, uint[]? Indices, Matrix4x4 World, Vector3 Min, Vector3 Max);

    // Decoded hulls, by the cooked bytes they came from: a re-cooked hull is another array and is decoded again.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Hull> Hulls = new();

    // A decoded hull and the box of its vertices in its own space - what a placement of it is first tried against.
    private sealed record Hull(Vector3[] Vertices, uint[] Triangles, Vector3 Min, Vector3 Max);

    /// <param name="box">A world box the caller is asking about: a hull whose own box, stood where the placement
    /// stands, does not reach it is answered with that looser box and without its vertices being put through
    /// the placement's matrix - a search of a district tries every placement of every collision file.</param>
    private static Shape? ShapeOf(SceneNode node, (Vector3 Min, Vector3 Max)? box = null)
    {
        if (node.Mesh is { Instanced: false } mesh)
        {
            return new Shape(mesh.PickPositions, mesh.PickIndices, mesh.World, mesh.BoundsMin, mesh.BoundsMax);
        }
        if (node.Source is not Assets.Adapters.CollisionInstanceAdapter placement) return null;

        // by the file's own index of its hulls: a scan of the list for every placement was hulls x placements
        byte[]? cooked = placement.Document.MeshFor(placement.Instance.Hash)?.CookedMesh;
        if (cooked == null) return null;
        Hull hull = Hulls.GetValue(cooked, static bytes =>
        {
            try
            {
                Formats.Collisions.CookedTriangleMesh decoded = Formats.Collisions.CookedTriangleMesh.Decode(bytes);
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                foreach (Vector3 vertex in decoded.Vertices)
                {
                    lo = Vector3.Min(lo, vertex);
                    hi = Vector3.Max(hi, vertex);
                }
                return new Hull(decoded.Vertices, Array.ConvertAll(decoded.Triangles, i => (uint)i), lo, hi);
            }
            catch (Formats.Collisions.CollisionDecodeException)
            {
                return new Hull([], [], default, default);
            }
        });
        if (hull.Vertices.Length == 0) return null;

        Matrix4x4 world = placement.WorldTransform;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        if (box is { } asked)
        {
            // the eight corners of the hull's own box: it holds every vertex, so a miss here is a miss
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = Vector3.Transform(new Vector3((i & 1) == 0 ? hull.Min.X : hull.Max.X,
                    (i & 2) == 0 ? hull.Min.Y : hull.Max.Y, (i & 4) == 0 ? hull.Min.Z : hull.Max.Z), world);
                min = Vector3.Min(min, corner);
                max = Vector3.Max(max, corner);
            }
            if (!Overlaps(min, max, asked.Min, asked.Max)) return new Shape(hull.Vertices, hull.Triangles, world, min, max);
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
        }
        foreach (Vector3 vertex in hull.Vertices)
        {
            Vector3 at = Vector3.Transform(vertex, world);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }
        return new Shape(hull.Vertices, hull.Triangles, world, min, max);
    }

    /// <summary>How many of a mesh's triangles reach into a world-space box, and the extent of those
    /// triangles clipped to it. Null when the mesh keeps no CPU geometry to ask.</summary>
    private static int? TrianglesInBox(
        Shape shape, Vector3 boxMin, Vector3 boxMax, out Vector3 insideMin, out Vector3 insideMax)
    {
        insideMin = new Vector3(float.MaxValue);
        insideMax = new Vector3(float.MinValue);
        if (shape.Positions is not { } positions || shape.Indices is not { } indices) return null;

        Matrix4x4 world = shape.World;
        int count = 0;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            Vector3 a = Vector3.Transform(positions[indices[i]], world);
            Vector3 b = Vector3.Transform(positions[indices[i + 1]], world);
            Vector3 c = Vector3.Transform(positions[indices[i + 2]], world);
            if (!TriangleBoxTest.Overlaps(a, b, c, boxMin, boxMax)) continue;
            count++;
            insideMin = Vector3.Min(insideMin, Vector3.Max(boxMin, Vector3.Min(a, Vector3.Min(b, c))));
            insideMax = Vector3.Max(insideMax, Vector3.Min(boxMax, Vector3.Max(a, Vector3.Max(b, c))));
        }
        return count;
    }

    public string? Select(IReadOnlyList<string> names)
    {
        if (TargetHost is not { } host) return TargetNotOpen;

        var nodes = new List<SceneNode>(names.Count);
        foreach (string name in names)
        {
            if (Resolve(host, name, out SceneNode? node) is { } unresolved) return unresolved;
            if (host.BridgeEditedCount > 0 && !host.BridgeSession.IsEditedNode(node!))
                return $"'{name}' is not part of the open Blender session — blender_end first";
            nodes.Add(node!);
        }
        host.Selection.SetSelection(nodes, nodes.Count > 0 ? nodes[^1] : null);
        return null;
    }

    public string? RequestBlenderPush()
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.BridgeEditedCount == 0) return "no Blender edit session is open — blender_open first";
        return host.RequestBridgePush() ? null : "the connection to Blender is gone — blender_open again";
    }

    public string? OpenInBlender()
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.BridgeEditedCount > 0) return "a Blender edit session is already open — blender_end first";
        if (host.SelectedNodes.Count == 0) return "nothing is selected — scene_select first";
        host.OpenInBlender();
        return null;
    }

    public string? EndBlenderSession()
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.BridgeEditedCount == 0) return "no Blender edit session is open";
        host.EndBridgeEditSession();
        return null;
    }

    public IReadOnlyList<EditorNotice> Notices(int last) => EditorNoticeLog.Last(last);

    public string? Save(out int filesWritten, out IReadOnlyList<string> notSaved)
    {
        filesWritten = 0;
        notSaved = [];
        if (TargetHost is not { } host) return TargetNotOpen;
        try
        {
            D3DImageHost.SaveReport report = host.SaveEditsReport();
            filesWritten = report.Written;
            notSaved = report.NotSaved;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "failed to save: " + ex.Message;
        }
    }

    public BuildOutcome Build()
    {
        D3DImageHost host = Host;
        // The viewport's own save, not only the frame documents the build re-saves: a material library
        // edited in this session has to be on disk before the archive that names it is packed.
        //
        // And when that save does not complete, nothing is packed: an archive packed now would ship meshes
        // naming materials that are not in the library on disk, with a backup taken and "built" reported.
        D3DImageHost.SaveReport saved = host.SaveEditsReport();
        if (!saved.Complete) return new BuildOutcome([], [], saved.NotSaved);

        D3DImageHost.BuildReport report = host.BuildEdits(createBackup: true);
        return new BuildOutcome(
            report.Packed.Select(p => (p.Archive, p.Backup)).ToList(),
            report.Failed.Select(f => (f.Archive, f.Error)).ToList(),
            [],
            [.. report.Packed.SelectMany(p => (p.Dropped ?? []).Select(file => $"{Path.GetFileName(p.Archive)}: {file}"))]);
    }

    public string? MirrorToWinter(out SeasonMirrorOutcome? outcome)
    {
        outcome = null;
        if (Window is not { } window) return NotOpen;
        D3DImageHost host = window.Viewport;
        if (host.BridgeEditedCount > 0) return "a Blender edit session is open — blender_end first";
        if (window.WholeMapCheck.IsChecked == true || window.AreaCombo.SelectedItem is not MapArea area)
        {
            return "load one district first — the whole map has no single winter archive";
        }
        if (area.Winter == null) return $"'{area.BaseName}' has no winter variant";
        if (window.WinterToggle.IsChecked == true)
        {
            return "the winter variant is loaded — load the summer one: it is the summer scene that gets mirrored";
        }

        try
        {
            // The mirror reads the working copy on disk, so what is only in memory has to be written first —
            // and when that could not be written, the mirror would carry yesterday's scene into winter.
            D3DImageHost.SaveReport saved = host.SaveEditsReport();
            if (!saved.Complete) return "the save a mirror starts with did not complete: " + string.Join("; ", saved.NotSaved);
            Assets.Sds.SeasonMirror.Report? report =
                Assets.Sds.SeasonMirror.ToWinter(area.Summer, area.Winter, out string? reason);
            if (report == null) return reason ?? "the winter archive could not be written";

            host.MarkArchiveModified(area.Winter);
            _resourceTarget = false;      // a map-only tool: what follows it is about the map
            outcome = new SeasonMirrorOutcome(area.Winter.FullName, report.Matched, report.Added, report.Dropped,
                report.Reassigned, report.Ambiguous, report.Files, report.Textures);
            host.RaiseNotice(
                $"Mirrored {area.BaseName} into {area.Winter.Name}: {report.Matched} object(s) settled, "
                + $"{report.Added} added, {report.Dropped} dropped, {report.Reassigned} re-pointed slot(s) carried over, "
                + $"{report.Files.Count} file(s) and {report.Textures.Count} texture(s) written — Build packs it"
                + (report.Ambiguous == 0 ? "" : $". {report.Ambiguous} object(s) could not be told from a namesake "
                    + "and kept their summer materials — check them in winter"),
                isError: report.Ambiguous > 0);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or Formats.SdsFormatException)
        {
            return "failed to mirror: " + ex.Message;
        }
    }

    public CameraInfo Camera()
    {
        var pose = Host.CameraPose;
        return new CameraInfo(
            [pose.Position.X, pose.Position.Y, pose.Position.Z], pose.Yaw, pose.Pitch, pose.OrbitDistance);
    }

    public string? LookAt(float[] target, float radius, float[]? fromAxis)
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (radius <= 0f) return "radius must be positive";
        host.LookAt(
            new Vector3(target[0], target[1], target[2]),
            radius,
            fromAxis != null ? new Vector3(fromAxis[0], fromAxis[1], fromAxis[2]) : null);
        return null;
    }

    public string? SetCamera(float[] position, float[] target)
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        var eye = new Vector3(position[0], position[1], position[2]);
        var at = new Vector3(target[0], target[1], target[2]);
        if (Vector3.DistanceSquared(eye, at) < 1e-6f) return "position and target are the same point";
        host.LookFrom(eye, at);
        return null;
    }

    public bool FrameSelection() => Host.FrameSelection();

    public string? SetView(string? renderMode, bool? collision, bool? crash, bool? zones, bool? navigation,
        bool discardUnsavedEdits = false)
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        Window? owner = _resourceTarget ? ResourceWindow : Window;
        if (_resourceTarget && (collision != null || crash != null || zones != null || navigation != null))
        {
            return "the collision, crash, zone and navigation layers belong to the map — the resource editor draws "
                + "an archive's own collision from its Render tab";
        }

        RenderMode mode = default;
        if (renderMode != null && !Enum.TryParse(renderMode, ignoreCase: true, out mode))
            return $"unknown shading mode '{renderMode}' — one of {string.Join(", ", Enum.GetNames<RenderMode>())}";
        if ((collision != null || crash != null) && host.BridgeEditedCount > 0)
            return "the collision and crash layers reload part of the scene — blender_end first";
        // Switching the CRASH layer off unloads it, and with it the unsaved edits made there and their undo
        // entries — a crash copy moved and never saved is simply gone. The same loss editor_open_area
        // refuses, by the same rule: not while edits are unsaved, unless the caller says they may go.
        // (The collision layer is only hidden when it is switched off: its placements, their edits and
        // their undo entries stay, so there is nothing to guard there.)
        bool unloads = crash == false && Window is { } map && map.CrashToggle.IsChecked == true;
        if (unloads && host.HasUnsavedEdits && !discardUnsavedEdits)
        {
            return "switching the crash layer off unloads it, and unsaved edits made in it are dropped together with "
                + "their undo entries — editor_save first, or pass discardUnsavedEdits=true to give them up";
        }

        if (renderMode != null)
        {
            // The mode buttons are nameless (they sit inside a strip with its own namescope) and carry the
            // mode in Tag; checking the right one is what moves the viewport, through the window's handler.
            RadioButton? button = owner == null ? null : Descendants<RadioButton>(owner)
                .FirstOrDefault(b => b.Tag is string tag && tag == mode.ToString());
            if (button != null) button.IsChecked = true;
            host.RenderMode = mode;
        }
        if (Window is not { } window) return null;
        if (zones is { } showZones) window.ZonesToggle.IsChecked = showZones;
        if (crash is { } showCrash) window.CrashToggle.IsChecked = showCrash;
        if (collision is { } showCollision) window.CollisionToggle.IsChecked = showCollision;
        if (navigation is { } showNavigation) window.AiNavToggle.IsChecked = showNavigation;
        return null;
    }

    public string? Screenshot(string path, int width, int height)
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.CaptureFrame(width, height) is not { } pixels) return "the viewport is not rendering yet";
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            // Bgr32, not Bgra32: the target's alpha is whatever the passes left in it, and a viewer would
            // show the sky as a hole.
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream file = File.Create(path);
            encoder.Save(file);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "could not write the picture: " + ex.Message;
        }
    }

    public string? Move(string name, float[]? position, float[]? offset)
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, name, out SceneNode? node) is { } unresolved) return unresolved;
        if (node!.Source is not IFrameNode frame) return $"'{name}' is a {node.Kind} — it has no transform to move";
        if (host.BridgeEditedCount > 0 && !host.BridgeSession.IsEditedNode(node))
            return $"'{name}' is not part of the open Blender session — blender_end first";

        // A number too large for a float arrives as Infinity; written into a transform it is saved as one.
        if ((position ?? []).Concat(offset ?? []).Any(v => !float.IsFinite(v))) return "position and offset must be finite numbers";
        Vector3 delta = Vector3.Zero;
        if (position != null) delta = new Vector3(position[0], position[1], position[2]) - frame.WorldTransform.Translation;
        if (offset != null) delta += new Vector3(offset[0], offset[1], offset[2]);
        if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y) || !float.IsFinite(delta.Z)) return "that is further than a position can be";
        if (delta == Vector3.Zero) return null;

        // The gizmo's own path, start to finish: it is what knows how each kind of object moves (a frame,
        // a collision placement, an actor with geometry elsewhere) and it records the one undoable edit.
        host.Selection.SetSelection([node], node);
        host.GizmoBeginDrag(GizmoMode.Move);
        host.GizmoApplyWorldDelta(Matrix4x4.CreateTranslation(delta));
        host.GizmoEndDrag();
        return null;
    }

    public string? ImportActor(string sourceActFile, string actorName, string newName, float[] position)
    {
        if (Window is not { } window) return NotOpen;
        D3DImageHost host = window.Viewport;
        if (host.BridgeEditedCount > 0) return "a Blender edit session is open — blender_end first";
        if (position is not { Length: 3 } || position.Any(v => !float.IsFinite(v))) return "position takes three finite numbers";
        if (!File.Exists(sourceActFile)) return $"no such file: {sourceActFile}";

        SceneNode? actorsRow = AllNodes(host).FirstOrDefault(n => n.Source is Assets.Adapters.ActorDocumentAdapter);
        if (actorsRow == null) return "the loaded area has no actor pack to add to";

        Formats.Actors.ActorsFile source;
        try
        {
            source = Formats.Actors.ActorsFile.Load(sourceActFile);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            return "the source pack could not be read: " + ex.Message;
        }
        Formats.Actors.ActorEntry? actor = source.Actors.FirstOrDefault(
            a => string.Equals(a.EntityName, actorName, StringComparison.OrdinalIgnoreCase));
        if (actor == null) return $"no actor named '{actorName}' in {Path.GetFileName(sourceActFile)}";

        SceneNode? node = host.ActorEditing.Import(
            actorsRow, source, actor, newName, new Vector3(position[0], position[1], position[2]), out string? reason);
        if (node == null) return reason ?? "the pack refused the actor";
        _resourceTarget = false;          // a map-only tool: what follows it is about the map
        return null;
    }

    public string? ImportObject(string sourceArchive, string name, string newName, float[] position, float? yawDegrees,
        string? collision, int occurrence, out ObjectImportOutcome? outcome)
    {
        outcome = null;
        if (position is not { Length: 3 } || position.Any(v => !float.IsFinite(v))) return "position takes three finite numbers";
        if (yawDegrees is { } yaw && !float.IsFinite(yaw)) return "yawDegrees is not a finite number";
        if (Window is not { } window) return NotOpen;
        D3DImageHost host = window.Viewport;
        if (host.BridgeEditedCount > 0) return "a Blender edit session is open — blender_end first";
        if (window.WholeMapCheck.IsChecked == true || window.AreaCombo.SelectedItem is not MapArea area)
        {
            return "load one district first — an import needs one archive to go into";
        }
        FileInfo destination = area.FileFor(window.WinterToggle.IsChecked == true);

        Assets.Collisions.CollisionChoice hulls = Assets.Collisions.CollisionChoice.Auto;
        if (!string.IsNullOrEmpty(collision) && !Enum.TryParse(collision, ignoreCase: true, out hulls))
        {
            return $"collision '{collision}' is none of auto, convex, box, mesh, none";
        }
        string? refused = host.ObjectImporting.Import(destination, sourceArchive, name, newName,
            new Vector3(position[0], position[1], position[2]), yawDegrees, out outcome, hulls, occurrence);
        if (refused == null) _resourceTarget = false;     // a map-only tool: what follows it is about the map
        return refused;
    }

    public string? DuplicateSelected(out IReadOnlyList<string> copies)
    {
        copies = [];
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.BridgeEditedCount > 0) return "a Blender edit session is open — blender_end first";
        if (!host.CanDuplicateSelection()) return "nothing in the selection can be duplicated";
        var before = new HashSet<SceneNode>(host.SelectedNodes);
        host.DuplicateSelected();
        // A duplicate leaves its copies selected; a selection that did not move means nothing was copied.
        var made = host.SelectedNodes.Where(n => !before.Contains(n)).ToList();
        if (made.Count == 0) return "nothing was duplicated — editor_notices says why";
        copies = made.Select(n => n.Name).ToList();
        return null;
    }

    public IReadOnlyList<ObjectProperty> Properties(string name)
    {
        D3DImageHost host = Host;
        if (Resolve(host, name, out SceneNode? node) is { } unresolved) throw new InvalidOperationException(unresolved);
        if (node!.Source is not Domain.Properties.IPropertySource source) return [];
        var rows = new List<ObjectProperty>();
        foreach (Domain.Properties.PropertyGroup group in source.GetPropertyGroups())
        {
            foreach (Domain.Properties.PropertyDescriptor p in group.Properties)
            {
                rows.Add(new ObjectProperty(group.Title, p.Id, p.Label, p.Kind.ToString(), p.IsReadOnly || p.Set == null, Show(p)));
            }
        }
        return rows;
    }

    public string? SetProperty(string name, string propertyId, string value)
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, name, out SceneNode? node) is { } unresolved) return unresolved;
        if (node!.Source is not Domain.Properties.IPropertySource source) return $"'{name}' has no properties";
        Domain.Properties.PropertyDescriptor? property = source.GetPropertyGroups()
            .SelectMany(g => g.Properties)
            .FirstOrDefault(p => string.Equals(p.Id, propertyId, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(p.Label, propertyId, StringComparison.OrdinalIgnoreCase));
        if (property == null) return $"'{name}' has no property '{propertyId}' — object_properties lists them";
        if (property.IsReadOnly || property.Set == null) return $"'{property.Label}' is read-only";
        if (!TryParse(property, value, out object? parsed)) return $"'{value}' is not a {property.Kind} value";
        // A name is boxed with a zero hash on the way in (the adapter derives the real one), so the generic
        // "did it change" comparison cannot see an unchanged name — and renaming a frame to its own name
        // would still land on the undo stack as an edit.
        if (parsed is Domain.Properties.HashNameValue renamed
            && property.Get() is Domain.Properties.HashNameValue current
            && string.Equals(current.Name, renamed.Name, StringComparison.Ordinal))
        {
            return null;
        }
        host.CommitPropertyEdit(node, property, property.Get(), parsed);
        return null;
    }

    private static string Show(Domain.Properties.PropertyDescriptor p)
    {
        object? v = p.Get();
        IFormatProvider inv = System.Globalization.CultureInfo.InvariantCulture;
        return v switch
        {
            null => "",
            float f => f.ToString("0.######", inv),
            Vector3 vec => string.Create(inv, $"{vec.X:0.######}, {vec.Y:0.######}, {vec.Z:0.######}"),
            ulong h => "0x" + h.ToString("X16", inv),
            bool b => b ? "true" : "false",
            IReadOnlyList<string> lines => string.Join(" | ", lines.Take(6)),
            IFormattable f => f.ToString(null, inv),
            _ => v.ToString() ?? "",
        };
    }

    /// <summary>Turns the text a tool was handed into the boxed value a property takes — or refuses it.
    /// Internal so the refusals can be checked without an editor on screen (<c>--probe-editor-tools</c>).</summary>
    internal static bool TryParse(Domain.Properties.PropertyDescriptor p, string text, out object? value)
    {
        IFormatProvider inv = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles Num = System.Globalization.NumberStyles.Float;
        value = null;
        switch (p.Kind)
        {
            case Domain.Properties.PropertyKind.Int or Domain.Properties.PropertyKind.Flags:
                if (!long.TryParse(text, System.Globalization.NumberStyles.Integer, inv, out long n) || n < p.Min || n > p.Max) return false;
                value = n;
                return true;
            case Domain.Properties.PropertyKind.Float:
                // "NaN" and "Infinity" parse, and so does "1e100" — as infinity. None of them is a draw
                // distance or a light's range, and a setter would write them into the file as given.
                if (!float.TryParse(text, Num, inv, out float f) || !float.IsFinite(f)) return false;
                value = f;
                return true;
            case Domain.Properties.PropertyKind.Bool:
                if (!bool.TryParse(text, out bool b)) return false;
                value = b;
                return true;
            case Domain.Properties.PropertyKind.Text:
                value = text;
                return true;
            case Domain.Properties.PropertyKind.HashName:
                // The same rule as the property panel: an empty name would keep the hash of the old one.
                // The hash is the adapter's to derive, so it travels as zero.
                if (string.IsNullOrWhiteSpace(text)) return false;
                value = new Domain.Properties.HashNameValue(0, text);
                return true;
            case Domain.Properties.PropertyKind.UInt64Hex:
                string hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
                if (!ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, inv, out ulong h)) return false;
                value = h;
                return true;
            case Domain.Properties.PropertyKind.Vector3:
                string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 3 || !float.TryParse(parts[0], Num, inv, out float x)
                    || !float.TryParse(parts[1], Num, inv, out float y) || !float.TryParse(parts[2], Num, inv, out float z)
                    || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                {
                    return false;
                }
                value = new Vector3(x, y, z);
                return true;
            default:
                return false;
        }
    }

    public string? DeleteSelected(out int deleted)
    {
        deleted = 0;
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.BridgeEditedCount > 0) return "a Blender edit session is open — blender_end first";
        if (!host.CanDeleteSelection()) return "nothing deletable is selected";
        deleted = host.SelectedNodes.Count;
        host.DeleteSelected();
        return null;
    }

    // The archive the tuning tools work on: the one on the resource editor's stage.
    private static FileInfo? TuningArchive(out string? refusal)
    {
        refusal = null;
        if (ResourceWindow?.StagedEntry is not { } entry)
        {
            refusal = "no archive is on the resource editor's stage — resource_open a car first";
            return null;
        }
        return entry.File;
    }

    private static TuningFieldInfo Describe(int table, Assets.EntityData.TuningBandView band,
        Assets.EntityData.TuningElementView element, Assets.EntityData.TuningFieldView field) =>
        new(table, band.Title, element.Title, field.Label, field.Name, field.Kind.ToString(), field.Value);

    public string? Tuning(int table, string? query, int limit, out IReadOnlyList<TuningTableInfo> tables,
        out IReadOnlyList<TuningFieldInfo> fields)
    {
        tables = [];
        fields = [];
        if (TuningArchive(out string? refusal) is not { } archive) return refusal;
        if (Assets.EntityData.CarTuning.Read(archive) is not { } tuning || tuning.Tables.Count == 0)
        {
            return $"{archive.Name} carries no tuning table the toolkit can read";
        }
        tables = tuning.Tables.Select(t => new TuningTableInfo(t.Index + 1, t.Label, t.TypeName, t.FieldCount)).ToList();
        if (table < 1 || table > tuning.Tables.Count) return $"there is no table {table} — {tuning.Tables.Count} in all";

        var found = new List<TuningFieldInfo>();
        foreach (Assets.EntityData.TuningBandView band in tuning.Tables[table - 1].Bands)
        {
            foreach (Assets.EntityData.TuningElementView element in band.Elements)
            {
                foreach (Assets.EntityData.TuningFieldView field in element.Rows)
                {
                    if (found.Count >= limit) break;
                    if (query != null && !field.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        && !field.Label.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add(Describe(table, band, element, field));
                }
            }
        }
        fields = found;
        return null;
    }

    public string? SetTuning(int table, string field, string? band, string? element, string value, out TuningFieldInfo? result)
    {
        result = null;
        if (ResourceWindow is not { } window || TuningArchive(out string? refusal) is not { } archive)
        {
            return TuningArchive(out string? why) == null ? why : "the resource editor is not open";
        }
        if (Assets.EntityData.CarTuning.Read(archive) is not { } tuning) return $"{archive.Name} carries no tuning table";
        if (table < 1 || table > tuning.Tables.Count) return $"there is no table {table} — {tuning.Tables.Count} in all";
        Assets.EntityData.TuningTableView chosen = tuning.Tables[table - 1];

        var matches = (
            from b in chosen.Bands
            where band == null || string.Equals(b.Title, band, StringComparison.OrdinalIgnoreCase)
            from e in b.Elements
            where element == null || string.Equals(e.Title, element, StringComparison.OrdinalIgnoreCase)
            from f in e.Rows
            where string.Equals(f.Name, field, StringComparison.OrdinalIgnoreCase)
                  || string.Equals(f.Label, field, StringComparison.OrdinalIgnoreCase)
                  || f.Name.EndsWith("." + field, StringComparison.OrdinalIgnoreCase)
            select (b, e, f)).ToList();
        if (matches.Count == 0)
        {
            string where = string.Join(" / ", new[] { band, element }.Where(w => w != null));
            return $"table {table} has no field '{field}'" + (where.Length > 0 ? $" in {where}" : "") + " — car_tuning lists them";
        }
        if (matches.Count > 1)
        {
            return $"'{field}' is in {matches.Count} places — give band and/or element: "
                + string.Join("; ", matches.Take(8).Select(m => $"{m.b.Title} / {m.e.Title ?? "-"}"));
        }
        (Assets.EntityData.TuningBandView inBand, Assets.EntityData.TuningElementView inElement,
            Assets.EntityData.TuningFieldView target) = matches[0];

        IFormatProvider inv = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles Num = System.Globalization.NumberStyles.Float;
        Assets.EntityData.TuningEditing.TuningValue parsed;
        switch (target.Kind)
        {
            case Assets.EntityData.TuningFieldKind.Number when float.TryParse(value, Num, inv, out float f):
                parsed = Assets.EntityData.TuningEditing.TuningValue.Of(f);
                break;
            case Assets.EntityData.TuningFieldKind.Integer when long.TryParse(value, System.Globalization.NumberStyles.Integer, inv, out long n):
                parsed = Assets.EntityData.TuningEditing.TuningValue.Of(n);
                break;
            case Assets.EntityData.TuningFieldKind.Flag when bool.TryParse(value, out bool flag) || value is "0" or "1":
                parsed = Assets.EntityData.TuningEditing.TuningValue.Of(value is "1" || (bool.TryParse(value, out bool b2) && b2) ? 1L : 0L);
                break;
            case Assets.EntityData.TuningFieldKind.Vector:
                string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 3 || !float.TryParse(parts[0], Num, inv, out float x)
                    || !float.TryParse(parts[1], Num, inv, out float y) || !float.TryParse(parts[2], Num, inv, out float z))
                {
                    return $"'{value}' is not 'x, y, z'";
                }
                parsed = Assets.EntityData.TuningEditing.TuningValue.Of(x, y, z);
                break;
            case Assets.EntityData.TuningFieldKind.Text:
                if (target.Capacity > 0 && value.Length >= target.Capacity) return $"'{value}' is longer than the {target.Capacity - 1} characters the field holds";
                parsed = Assets.EntityData.TuningEditing.TuningValue.Of(value);
                break;
            default:
                return $"'{value}' is not a {target.Kind} value";
        }

        Assets.EntityData.TuningEditing.Change? change = Assets.EntityData.TuningEditing.Set(
            chosen.Path, chosen.Index, target.Offset, parsed, target.Name, out string? unwritten);
        if (change == null) return $"{target.Name} was not written: {unwritten ?? "the field could not be found"}";
        // The undo entry and the build list below are the resource editor's; with the map as the target the
        // editor_undo and editor_build that follow would act on the map and leave this where it is.
        _resourceTarget = true;

        // The same bookkeeping the Tuning tab does: an undo entry, the archive on the build list, the panel re-read.
        D3DImageHost stage = window.TargetStage;
        void Reload() => window.Scene.Selection.ReloadTuning();
        stage.History.Push(new WorkingCopyEdit(new TuningValueEdit(change, Reload), () => stage.MarkArchiveModified(archive)));
        stage.MarkArchiveModified(archive);
        Reload();
        stage.RaiseNotice($"{target.Name} set. Build to write it into the archive.", isError: false);

        Assets.EntityData.TuningFieldView? after = Assets.EntityData.CarTuning.Read(archive)?.Tables[table - 1].Bands
            .FirstOrDefault(b => b.Title == inBand.Title)?.Elements
            .FirstOrDefault(e => e.Title == inElement.Title)?.Rows
            .FirstOrDefault(r => r.Offset == target.Offset);
        result = Describe(table, inBand, inElement, after ?? target);
        return null;
    }

    public string? BuildArchive(string archive, string? memoryFrom, bool dropMissing, out PackedArchive? result)
    {
        result = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (string.IsNullOrWhiteSpace(archive) || !Path.IsPathRooted(archive)) return "give the archive's full path";
        var sds = new FileInfo(archive);
        if (!sds.Exists || !sds.Extension.Equals(".sds", StringComparison.OrdinalIgnoreCase)) return $"no such archive: {archive}";
        string extracted = Assets.MafiaEnvironment.ExtractedDir(sds);
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml")))
        {
            return $"{sds.Name} has no working copy at {extracted} — nothing to pack";
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(memoryFrom))
            {
                var reference = new FileInfo(memoryFrom);
                if (!Path.IsPathRooted(memoryFrom) || !reference.Exists) return $"no such archive to take memory requirements from: {memoryFrom}";
                Assets.Sds.SdsWriter.AdoptMemoryRequirements(sds, reference);
            }
            // This tool is for edits made by hand in the working copy, where a renamed or deleted file is a
            // slip as often as an intention — and a pack does not skip such an entry quietly: it leaves the
            // resource out AND unsays it in the manifest, so putting the file back later changes nothing.
            IReadOnlyList<string> missing = Assets.Sds.SdsWriter.MissingEntries(extracted);
            if (missing.Count > 0 && !dropMissing)
            {
                return $"{missing.Count} file(s) the manifest names are not in the working copy: "
                    + string.Join(", ", missing.Take(12)) + (missing.Count > 12 ? ", …" : "")
                    + " — put them back, or pass dropMissing=true to build without them (their manifest entries are then removed for good)";
            }
            Assets.Sds.SdsWriter.PackResult packed = Assets.Sds.SdsWriter.PackSds(sds, createBackup: true);
            result = new PackedArchive(packed.Archive, packed.Backup, packed.Dropped.Count > 0 ? packed.Dropped : null);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Formats.FileFormatException)
        {
            return "could not pack the archive (is the game running?): " + ex.Message;
        }
    }

    public string? SubstituteCar(string source, string target, out CarSubstituteInfo? result)
    {
        result = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return "name the car and the one it replaces";
        if (OpenWithEdits(source) is { } unsaved) return unsaved;
        // The target's working copy is about to be replaced under whoever has it loaded: an editor that then
        // saves writes the OLD car's scene into the new car's folder.
        if (CarArchives(target).FirstOrDefault(a => Assets.Sds.OpenArchives.HoldersOf(a).Count > 0) is { } held)
        {
            return $"{held.Name} is open in an editor — its working copy is about to be replaced; open something else there first";
        }
        try
        {
            if (Assets.Cars.CarCloner.Substitute(source, target, out string? refusal) is not { } outcome) return refusal;
            result = new CarSubstituteInfo(outcome.Model,
                [.. outcome.Packed.Select(p => new PackedArchive(p.Archive, p.Backup))], outcome.Notes);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Formats.FileFormatException)
        {
            return "could not write the archives (is the game running?): " + ex.Message;
        }
    }

    public string? ExportCarForM2o(string car, string? output, string? resource, out M2oExportInfo? result)
    {
        result = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (string.IsNullOrWhiteSpace(car)) return "name the car to export";
        if (!string.IsNullOrWhiteSpace(output) && !Path.IsPathRooted(output)) return "give the output folder's full path";
        try
        {
            if (Assets.Cars.CarM2oExport.Export(car, string.IsNullOrWhiteSpace(output) ? null : output,
                    string.IsNullOrWhiteSpace(resource) ? null : resource, out string? refusal) is not { } exported)
            {
                return refusal;
            }
            result = new M2oExportInfo(exported.Folder, exported.Resource, exported.Model, exported.Title, exported.BasedOn,
                exported.Vehicles, exported.Materials, exported.Files, exported.Notes);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Formats.FileFormatException)
        {
            return "could not export the car: " + ex.Message;
        }
    }

    public string? CloneCar(string source, string name, bool traffic, string? title, out CarCloneInfo? result)
    {
        result = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (OpenWithEdits(source) is { } unsaved) return unsaved;
        try
        {
            if (Assets.Cars.CarCloner.Clone(source, name, traffic, title, out string? refusal) is not { } outcome) return refusal;
            result = new CarCloneInfo(outcome.Name, outcome.VehicleId, outcome.TrafficRows, outcome.TextId,
                [.. outcome.Packed.Select(p => new PackedArchive(p.Archive, p.Backup))], outcome.Notes);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "could not write the archives (is the game running?): " + ex.Message;
        }
    }

    // A car's archives, summer and winter, by archive or model name.
    private static IEnumerable<FileInfo> CarArchives(string car)
    {
        string stem = Path.GetFileNameWithoutExtension(car).ToLowerInvariant();
        foreach (string suffix in new[] { "", "_z" })
        {
            yield return new FileInfo(Path.Combine(Assets.MafiaEnvironment.PcFolder, "sds", "cars", stem + suffix + ".sds"));
        }
    }

    // A clone and a substitution are made from the working copy ON DISK. A car open in an editor with edits
    // not yet saved would be copied without them, and nothing would say so.
    private static string? OpenWithEdits(string car)
    {
        foreach (FileInfo archive in CarArchives(car))
        {
            if (Assets.Sds.OpenArchives.HoldersOf(archive).OfType<D3DImageHost>().Any(h => h.HasUnsavedEdits))
            {
                return $"{archive.Name} is open in an editor with unsaved edits — editor_save first, "
                    + "or the copy would be made from what is on disk, without them";
            }
        }
        return null;
    }

    public string? HideTriangles(string name, float[] boxMin, float[] boxMax, string? material, bool apply, int sample, string? shared,
        out HiddenTrianglesInfo? result)
    {
        result = null;
        if (TargetHost is not { } host) return TargetNotOpen;
        GeometryEditController.SharedGeometry sharing;
        if (string.IsNullOrWhiteSpace(shared)) sharing = GeometryEditController.SharedGeometry.Refuse;
        else if (string.Equals(shared, "own", StringComparison.OrdinalIgnoreCase)) sharing = GeometryEditController.SharedGeometry.OwnCopy;
        else if (string.Equals(shared, "all", StringComparison.OrdinalIgnoreCase)) sharing = GeometryEditController.SharedGeometry.All;
        else return "shared is 'own' or 'all', or left out";
        if (boxMin is not { Length: 3 } || boxMax is not { Length: 3 } || boxMin.Concat(boxMax).Any(v => !float.IsFinite(v)))
            return "boxMin and boxMax are [x, y, z], finite numbers";
        if (Resolve(host, name, out SceneNode? node) is { } unresolved) return unresolved;
        if (apply && host.BridgeEditedCount > 0) return "a Blender session is open — blender_end first";
        if (PlanHiddenTriangles(node!, new Vector3(boxMin[0], boxMin[1], boxMin[2]), new Vector3(boxMax[0], boxMax[1], boxMax[2]),
                material, out Assets.Sds.TriangleHider.Plan? found) is { } notAMesh)
        {
            return notAMesh;
        }
        Assets.Sds.TriangleHider.Plan plan = found!;
        if (apply && plan.Changes.Count > 0 && host.GeometryEditing.HideTriangles(node!, plan.Changes, sharing) is { } refused)
        {
            return sharing == GeometryEditController.SharedGeometry.Refuse && host.GeometryEditing.GeometrySharersOf(node!) is { Count: > 0 } others
                ? $"{refused} ({others.Count}: {others.Names}) — shared: 'own' cuts this mesh alone, 'all' cuts every one of them"
                : refused;
        }

        static float[] P(Vector3 v) => [v.X, v.Y, v.Z];
        int levels = plan.Triangles.Count == 0 ? 0 : plan.Triangles.Max(t => t.Lod) + 1;
        result = new HiddenTrianglesInfo(
            plan.Triangles.Count,
            [.. Enumerable.Range(0, levels).Select(l => plan.Triangles.Count(t => t.Lod == l))],
            apply && plan.Changes.Count > 0,
            [.. plan.Triangles.Take(Math.Clamp(sample, 0, 500)).Select(t => new TriangleInfo(t.Lod, t.Material, P(t.A), P(t.B), P(t.C)))]);
        return null;
    }

    /// <summary>The triangles of a scene node's mesh whose corners all lie inside a world-space box, and the index
    /// edits that hide them. One implementation for the tool and for Tools → Hide triangles. Null on success.</summary>
    internal static string? PlanHiddenTriangles(SceneNode node, Vector3 min, Vector3 max, string? material,
        out Assets.Sds.TriangleHider.Plan? plan)
    {
        plan = null;
        if (node.Source is not Assets.Adapters.FrameNodeAdapter { Frame: Formats.Frames.ObjectTypes.FrameObjectSingleMesh mesh } adapter)
        {
            return $"'{node.Name}' is a {node.Kind} — only a mesh has triangles to hide";
        }
        plan = Assets.Sds.TriangleHider.Find(mesh, ((IFrameNode)adapter).WorldTransform, min, max,
            string.IsNullOrWhiteSpace(material) ? null : material);
        return null;
    }

    /// <summary>The same for triangles picked one by one on the mesh's first level of detail (Tools → Hide
    /// triangles, by clicking): the plan that hides them and what lies on them on the other levels, and how many
    /// levels the mesh has. Null on success.</summary>
    internal static string? PlanPickedTriangles(SceneNode node, IReadOnlyCollection<int> picked, out Assets.Sds.TriangleHider.Plan? plan,
        out int levels)
    {
        plan = null;
        levels = 0;
        if (node.Source is not Assets.Adapters.FrameNodeAdapter { Frame: Formats.Frames.ObjectTypes.FrameObjectSingleMesh mesh } adapter)
        {
            return $"'{node.Name}' is a {node.Kind} — only a mesh has triangles to hide";
        }
        levels = mesh.Geometry?.LOD?.Length ?? 0;
        plan = Assets.Sds.TriangleHider.FindPicked(mesh, ((IFrameNode)adapter).WorldTransform, picked);
        return null;
    }

    public string? MeshMaterials(string name, int? slot, string? material, out IReadOnlyList<MeshSlotInfo> slots)
    {
        slots = [];
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, name, out SceneNode? node) is { } unresolved) return unresolved;
        if (node!.Source is not Assets.Adapters.FrameNodeAdapter adapter || adapter.GetMaterials() is not { Count: > 0 } before)
        {
            return $"'{name}' is a {node.Kind} with no material slots";
        }

        int changed = -1;
        if (slot != null || !string.IsNullOrWhiteSpace(material))
        {
            if (slot is not { } index || string.IsNullOrWhiteSpace(material)) return "to re-point a slot give both: slot and material";
            if (index < 0 || index >= before.Count) return $"'{name}' has slots 0 to {before.Count - 1}";
            if (host.BridgeEditedCount > 0) return "a Blender session is open — blender_end first";
            if (Assets.MafiaMaterials.FindHashByName(material) is not { } hash)
            {
                return $"no material named '{material}' in the loaded libraries — search_materials has the names (the spelling is exact)";
            }
            // The slot is a row of the mesh's material block, and a block can serve several objects: all of them
            // would change in the file and in the game, while this answer - and the viewport - showed one.
            if (host.SlotAssignObstacle(node) is { } shared) return shared;
            if (!host.AssignSlotMaterial(node, index, hash)) return $"slot {index} of '{name}' could not be re-pointed";
            changed = index;
        }
        slots = [.. adapter.GetMaterials().Select((m, i) => new MeshSlotInfo(i, m.Name, m.TriangleCount, i == changed))];
        return null;
    }

    public string? UnusedHulls(bool apply, out IReadOnlyList<UnusedHullsInfo> result)
    {
        result = [];
        if (TargetHost is not { } host) return TargetNotOpen;
        if (apply && host.BridgeEditedCount > 0) return "a Blender session is open — blender_end first";

        // Counted before the sweep: afterwards a layer's hull list no longer says what it carried.
        var layers = AllNodes(host)
            .Where(n => n.Source is Assets.Adapters.CollisionDocumentAdapter)
            .Select(n => (Node: n, File: ((Assets.Adapters.CollisionDocumentAdapter)n.Source!).Collision))
            .Select(l => (l.Node, Placements: l.File.Instances.Count, Hulls: l.File.Meshes.Count))
            .ToList();
        if (layers.Count == 0) return "the open scene has no collision file";

        Dictionary<SceneNode, int> unused = host.CollisionEditing.SweepUnusedHulls(layers.Select(l => l.Node), apply)
            .ToDictionary(l => l.Layer, l => l.Unused);
        result = [.. layers.Select(l =>
        {
            int n = unused.GetValueOrDefault(l.Node);
            return new UnusedHullsInfo(PathOf(l.Node), l.Placements, l.Hulls, n, apply && n > 0);
        })];
        return null;
    }

    public string? CrashPlacements(float[] boxMin, float[] boxMax, string? nameContains, bool delete, int limit, int maxDelete,
        out IReadOnlyList<CrashPlacementInfo> result, out int total)
    {
        result = [];
        total = 0;
        if (TargetHost is not { } host) return TargetNotOpen;
        if (boxMin is not { Length: 3 } || boxMax is not { Length: 3 }) return "boxMin and boxMax are [x, y, z]";
        // A box that holds nothing by construction answers "count 0", and that reads as "nothing stands here".
        if (boxMin.Concat(boxMax).Any(v => !float.IsFinite(v))) return "boxMin and boxMax must be finite numbers";
        for (int axis = 0; axis < 3; axis++)
        {
            if (boxMin[axis] > boxMax[axis])
            {
                return $"boxMin is above boxMax on {"xyz"[axis]} ({boxMin[axis]} > {boxMax[axis]}) — such a box holds "
                    + "nothing; give the lower corner first";
            }
        }
        if (host.Streamer.CrashLayer is not { } layer) return "the crash layer is not in the scene — view_set crash=true first";
        if (delete && host.BridgeEditedCount > 0) return "a Blender session is open — blender_end first";

        var lo = new Vector3(boxMin[0], boxMin[1], boxMin[2]);
        var hi = new Vector3(boxMax[0], boxMax[1], boxMax[2]);
        var hits = new List<(Formats.Translokator.Object Row, Formats.Translokator.Instance Placement)>();
        foreach (Formats.Translokator.Object row in layer.Rows)
        {
            if (!string.IsNullOrWhiteSpace(nameContains)
                && !row.Name.String.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (Formats.Translokator.Instance placement in row.Instances)
            {
                Vector3 p = placement.Position;
                if (p.X >= lo.X && p.X <= hi.X && p.Y >= lo.Y && p.Y <= hi.Y && p.Z >= lo.Z && p.Z <= hi.Z)
                    hits.Add((row, placement));
            }
        }

        total = hits.Count;
        // A wide box with no name is the whole city table — tens of thousands of placements and their twins as
        // one edit, of which the list would show the first two hundred. Refused whole instead: the caller
        // says how many it means to remove.
        if (delete && hits.Count > Math.Max(0, maxDelete))
        {
            return $"{hits.Count} placements are in the box — more than maxDelete ({maxDelete}); nothing was deleted. "
                + "List them first (delete=false), narrow the box or the name, or raise maxDelete";
        }
        // A delete lists everything it removed, whatever the listing limit: what went has to be readable.
        result = [.. hits.Take(delete ? hits.Count : Math.Max(0, limit)).Select(h => new CrashPlacementInfo(
            h.Row.Name.String, h.Placement.ID, [h.Placement.Position.X, h.Placement.Position.Y, h.Placement.Position.Z],
            layer.Document.HasTwinOf(h.Placement, h.Row), layer.Document.Node(h.Placement, h.Row).SeasonLinked))];
        if (!delete || hits.Count == 0) return null;

        // The viewport's own delete, so the edit is the one the Delete key makes: undoable, the streaming grid
        // kept in step, the twin in the other season gone with a linked placement.
        List<SceneNode> nodes = [.. hits.Select(h => host.Streamer.CrashNodeFor(h.Placement, h.Row)).OfType<SceneNode>()];
        if (nodes.Count != hits.Count) return "some placements could not be given a tree node — nothing was deleted";
        // That delete works on the selection. What the user had selected is put back afterwards, less
        // whatever of it was just deleted — a tool that lists and removes props has no business leaving the
        // editor with nothing selected.
        List<SceneNode> selectedBefore = [.. host.Selection.Selected];
        SceneNode? activeBefore = host.Selection.Active;
        host.Selection.SetSelection(nodes, nodes[^1]);
        host.CrashEditing.DeleteSelected();
        var gone = new HashSet<SceneNode>(nodes);
        List<SceneNode> kept = [.. selectedBefore.Where(n => !gone.Contains(n))];
        host.Selection.SetSelection(kept,
            activeBefore != null && kept.Contains(activeBefore) ? activeBefore : kept.Count > 0 ? kept[^1] : null);
        return null;
    }

    public string? Undo()
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (!host.History.CanUndo) return "nothing to undo";
        return host.TryUndo();
    }

    public string? Redo()
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (!host.History.CanRedo) return "nothing to redo";
        return host.TryRedo();
    }

    // ── Scene tree helpers ──

    private static IEnumerable<SceneNode> AllNodes(D3DImageHost host)
    {
        var stack = new Stack<SceneNode>(host.Roots.Reverse());
        while (stack.Count > 0)
        {
            SceneNode node = stack.Pop();
            yield return node;
            for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
    }

    private static string PathOf(SceneNode node)
    {
        var parts = new List<string>();
        for (SceneNode? n = node; n != null; n = n.Parent) parts.Add(n.Name);
        parts.Reverse();
        return string.Join('/', parts);
    }

    private static bool Overlaps(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax) =>
        aMin.X <= bMax.X && aMax.X >= bMin.X
        && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y
        && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;

    /// <summary>
    /// One node for a name: the object's name, or — when the text has a slash in it — the tail of its tree
    /// path. Returns the refusal; on an ambiguous name it lists the paths, which is what the caller needs to
    /// ask again.
    /// </summary>
    private static string? Resolve(D3DImageHost host, string name, out SceneNode? node)
    {
        node = null;
        bool byPath = name.Contains('/');
        var matches = new List<SceneNode>();
        foreach (SceneNode candidate in AllNodes(host))
        {
            bool hit = byPath
                ? ("/" + PathOf(candidate)).EndsWith("/" + name.TrimStart('/'), StringComparison.OrdinalIgnoreCase)
                : string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase);
            if (hit) matches.Add(candidate);
        }

        if (matches.Count == 0) return $"no object named '{name}' in the loaded scene — scene_find has the names";
        if (matches.Count > 1)
        {
            // A mesh and the "LOD n" rows it grows share one object; the mesh row stands for all of them.
            var distinct = matches.Where(m => m.Lod == 0).ToList();
            if (distinct.Count == 1) matches = distinct;
        }
        if (matches.Count > 1)
        {
            return $"'{name}' names {matches.Count} objects — give a path suffix: "
                + string.Join("; ", matches.Take(8).Select(PathOf));
        }
        node = matches[0];
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            if (element is T match) yield return match;
            foreach (T deeper in Descendants<T>(element)) yield return deeper;
        }
    }

    // The city's load zones, read afresh from the working copy of city_univers on every call: they are a few
    // hundred small objects, and a tool that moves one must not answer from what stood there before.
    private string? OpenLoadZones(string? copy, out Assets.World.LoadZones? zones)
    {
        zones = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        try
        {
            IReadOnlyList<FileInfo> copies = Assets.World.LoadZones.Copies();
            FileInfo? which = string.IsNullOrWhiteSpace(copy)
                ? copies[0]
                : copies.FirstOrDefault(c => string.Equals(Assets.World.LoadZones.CopyName(c), copy, StringComparison.OrdinalIgnoreCase));
            if (which == null)
            {
                return $"no copy of city_univers.sds named '{copy}' - there are: " + string.Join(", ", copies.Select(Assets.World.LoadZones.CopyName));
            }
            // The districts' names: the open map editor has them already; without one the city folder is read.
            IReadOnlyCollection<string> districts = Window?.Viewport.Catalogs.DistrictNames is { Count: > 0 } known
                ? known
                : [.. Assets.World.MapCatalog.Build(Assets.MafiaEnvironment.CityFolder, f => Assets.Sds.SdsMeshLoader.EnsureExtracted(f))
                    .Areas.Select(a => a.BaseName)];
            zones = Assets.World.LoadZones.Open(f => Assets.Sds.SdsMeshLoader.EnsureExtracted(f), districts, which);
            return null;
        }
        catch (Exception ex) when (ZoneWrites.IsFileTrouble(ex))
        {
            return "could not read the load zones of city_univers: " + ex.Message;
        }
    }

    private static float[] Xyz(Vector3 v) => [v.X, v.Y, v.Z];

    public string? ZonesAt(float[] point, float near, string? copy, out IReadOnlyList<LoadZoneInfo> zones, out IReadOnlyList<string> districts,
        out IReadOnlyList<string> copies)
    {
        zones = [];
        districts = [];
        copies = [];
        if (point is not { Length: 3 } || point.Any(v => !float.IsFinite(v))) return "point is [x, y, z], finite numbers";
        if (!float.IsFinite(near) || near < 0) return "near is a distance in metres, 0 or more";
        if (OpenLoadZones(copy, out Assets.World.LoadZones? all) is { } failed) return failed;
        copies = [.. Assets.World.LoadZones.Copies().Select(Assets.World.LoadZones.CopyName)];

        var at = new Vector3(point[0], point[1], point[2]);
        zones = [.. all!.At(at, near).Select(z =>
        {
            (Vector3 min, Vector3 max) = Assets.World.LoadZones.WorldBox(z.Zone);
            return new LoadZoneInfo(z.Name, all.DistrictsOf(z.Name), z.Inside, z.OutsideBy, Xyz(min), Xyz(max),
                Assets.World.LoadZones.LoadsOnArrival(z.Name), !Assets.World.LoadZones.IsShipped(z.Name));
        })];
        districts = all.DistrictsAt(at);
        return null;
    }

    public string? ZonesMap(string district, float[] from, float[] to, float step, float z, string? copy, out IReadOnlyList<string> rows)
    {
        rows = [];
        if (string.IsNullOrWhiteSpace(district)) return "name the district, e.g. greenfield";
        if (from is not { Length: 2 } || to is not { Length: 2 } || from.Concat(to).Any(v => !float.IsFinite(v)))
            return "from and to are [x, y], finite numbers";
        // Below a tenth of a metre a step is no longer a step at the city's coordinates: a float there is
        // spaced a quarter of a millimetre apart, and a column "one step on" came out as the same column.
        if (!float.IsFinite(step) || step < 0.1f || !float.IsFinite(z)) return "step is a distance of 0.1 m or more and z a height";
        float x0 = MathF.Min(from[0], to[0]), x1 = MathF.Max(from[0], to[0]);
        float y0 = MathF.Min(from[1], to[1]), y1 = MathF.Max(from[1], to[1]);
        if ((x1 - x0) / step > 200 || (y1 - y0) / step > 200) return "that is more than 200 steps a side - take a larger step or a smaller box";
        if (OpenLoadZones(copy, out Assets.World.LoadZones? all) is { } failed) return failed;

        // Rows and columns are counted, and each stands at its own multiple of the step: adding the step to a
        // running float drifts, and with a step too small to change the float it never arrives.
        int columns = (int)MathF.Floor(((x1 - x0) / step) + 0.001f) + 1, lineCount = (int)MathF.Floor(((y1 - y0) / step) + 0.001f) + 1;
        var lines = new List<string>(lineCount);
        for (int line = 0; line < lineCount; line++)
        {
            float y = y1 - (line * step);
            var row = new System.Text.StringBuilder($"{y,8:F0} ");
            for (int column = 0; column < columns; column++)
            {
                float x = x0 + (column * step);
                var holding = all!.At(new Vector3(x, y, z)).ToList();
                row.Append(holding.Any(h => all.DistrictsOf(h.Name).Contains(district, StringComparer.OrdinalIgnoreCase)) ? '#'
                    : holding.Count > 0 ? '+' : '.');
            }
            lines.Add(row.ToString());
        }
        rows = lines;
        return null;
    }

    public string? ZoneCreate(string name, string like, float[] boxMin, float[] boxMax, string[] districts, bool apply, out LoadZoneInfo? zone)
    {
        zone = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (boxMin is not { Length: 3 } || boxMax is not { Length: 3 } || boxMin.Concat(boxMax).Any(v => !float.IsFinite(v)))
            return "boxMin and boxMax are [x, y, z], finite numbers";
        if (districts is not { Length: 1 or 2 } || districts.Any(string.IsNullOrWhiteSpace)) return "districts is one or two district names";
        var archive = new FileInfo(Assets.MafiaEnvironment.CityUniversSds);
        // asked of a dry run too: one that says "it can be made" while the write would be refused says nothing
        if (ZoneWrites.StructureBlocked(archive) is { } blocked) return blocked;
        try
        {
            IReadOnlyCollection<string> known = Window?.Viewport.Catalogs.DistrictNames is { Count: > 0 } names
                ? names
                : [.. Assets.World.MapCatalog.Build(Assets.MafiaEnvironment.CityFolder, f => Assets.Sds.SdsMeshLoader.EnsureExtracted(f))
                    .Areas.Select(a => a.BaseName)];
            Assets.World.LoadZones zones = Assets.World.LoadZones.Open(f => Assets.Sds.SdsMeshLoader.EnsureExtracted(f), known, archive,
                forNewZones: true);
            var min = new Vector3(boxMin[0], boxMin[1], boxMin[2]);
            var max = new Vector3(boxMax[0], boxMax[1], boxMax[2]);
            // worked out in memory first: what the zone would be is the answer of a dry run, and of a write too
            if (zones.Create(name, like, min, max, districts[0], districts.Length > 1 ? districts[1] : null) is { } refused) return refused;
            (Vector3 lo, Vector3 hi) = Assets.World.LoadZones.WorldBox(zones.Volumes[name]);
            zone = new LoadZoneInfo(name, zones.DistrictsOf(name), true, 0f, Xyz(lo), Xyz(hi), Assets.World.LoadZones.LoadsOnArrival(name), Added: true);
            if (!apply) return null;
            if (Window?.Viewport is { } viewport)
            {
                // With the map editor open: written the way its own button writes, so the zone is a step of the
                // editor's undo history - and the step is on the MAP editor's history (see ZoneMoveFace).
                if (viewport.ZoneEditing.Create(name, like, min, max, districts[0], districts.Length > 1 ? districts[1] : null) is { } unmade) return unmade;
                _resourceTarget = false;
                return null;
            }
            zones.Save();
            ZoneWrites.Landed(zones, name);
            return null;
        }
        catch (Exception ex) when (ZoneWrites.IsFileTrouble(ex))
        {
            return "could not add the zone to city_univers: " + ex.Message;
        }
    }

    public string? ZoneDelete(string name, bool apply, out LoadZoneInfo? zone)
    {
        zone = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (string.IsNullOrWhiteSpace(name)) return "name is the zone to take out";
        if (Assets.World.LoadZones.IsShipped(name)) return $"{name} came with the game - only a zone that was added is taken out";
        var archive = new FileInfo(Assets.MafiaEnvironment.CityUniversSds);
        if (ZoneWrites.StructureBlocked(archive) is { } blocked) return blocked;
        try
        {
            IReadOnlyCollection<string> known = Window?.Viewport.Catalogs.DistrictNames is { Count: > 0 } names
                ? names
                : [.. Assets.World.MapCatalog.Build(Assets.MafiaEnvironment.CityFolder, f => Assets.Sds.SdsMeshLoader.EnsureExtracted(f))
                    .Areas.Select(a => a.BaseName)];
            Assets.World.LoadZones zones = Assets.World.LoadZones.Open(f => Assets.Sds.SdsMeshLoader.EnsureExtracted(f), known, archive,
                forNewZones: true);
            if (!zones.Volumes.TryGetValue(name, out Formats.Frames.ObjectTypes.FrameObjectArea? volume)) return $"no load zone named '{name}'";
            (Vector3 lo, Vector3 hi) = Assets.World.LoadZones.WorldBox(volume);
            zone = new LoadZoneInfo(name, zones.DistrictsOf(name), false, 0f, Xyz(lo), Xyz(hi), Assets.World.LoadZones.LoadsOnArrival(name), Added: true);
            if (zones.Delete(name) is { } refused) return refused;
            if (!apply) return null;
            if (Window?.Viewport is { } viewport)
            {
                if (viewport.ZoneEditing.Remove(name) is { } kept) return kept;
                _resourceTarget = false;
                return null;
            }
            zones.Save();
            ZoneWrites.Landed(zones, name);
            return null;
        }
        catch (Exception ex) when (ZoneWrites.IsFileTrouble(ex))
        {
            return "could not take the zone out of city_univers: " + ex.Message;
        }
    }

    public string? ZoneMoveFace(string zone, string face, float to, bool apply, string? copy, out IReadOnlyList<LoadZoneMoveInfo> results)
    {
        results = [];
        if (EnsureEnvironment() is { } notReady) return notReady;
        List<string> names = string.IsNullOrWhiteSpace(copy) ? ["base"]
            : string.Equals(copy, "all", StringComparison.OrdinalIgnoreCase)
                ? [.. Assets.World.LoadZones.Copies().Select(Assets.World.LoadZones.CopyName)]
                : [copy];

        // Everything is worked out, in memory, for every copy before any of them is written: a face that can
        // be moved in one copy and not in another must not leave the two telling the game different things.
        var moved = new List<(Assets.World.LoadZones Zones, Assets.World.LoadZoneFaceMove Move, string Copy)>();
        var absent = new List<string>();
        foreach (string name in names)
        {
            if (OpenLoadZones(name, out Assets.World.LoadZones? all) is { } failed) return failed;
            if (!all!.Volumes.ContainsKey(zone ?? ""))
            {
                absent.Add(name);
                continue;
            }
            // The scene is written whole, from what is on disk. An editor that holds the archive has a copy of
            // its own and writes THAT whole on its next save: its copy of the zone must be what the disk has
            // now, and is brought in step after the write (ZoneWrites).
            if (apply && ZoneWrites.Blocked(all, zone!) is { } blocked) return $"{name}: {blocked} (editor_save)";
            if (all.MoveFace(zone!, face, to, out Assets.World.LoadZoneFaceMove? move) is { } refused) return $"{name}: {refused}";
            moved.Add((all, move!, name));
        }
        if (moved.Count == 0) return $"no load zone named '{zone}' in " + string.Join(", ", absent);

        // The base copy alone, with the map editor open: written through the editor's own zone editing, the way
        // its gizmo and its Loading zones window write - so the move is a step of the editor's undo history.
        bool viaEditor = apply && moved.Count == 1 && Window?.Viewport is not null
            && string.Equals(moved[0].Zones.Archive.FullName, new FileInfo(Assets.MafiaEnvironment.CityUniversSds).FullName, StringComparison.OrdinalIgnoreCase);
        if (viaEditor)
        {
            if (Window!.Viewport.ZoneEditing.MoveFace(zone!, face, to) is { } unmoved) return unmoved;
            // The step is on the MAP editor's history. Left pointing at the resource editor, the editor_undo
            // that follows took back whatever that one did last and called it done.
            _resourceTarget = false;
        }

        // Written one copy after another - and all of them, or none: the scene each copy had is kept until the
        // last one is down, and a copy that fails puts the ones before it back.
        var written = new List<(string File, byte[] Before)>();
        if (apply && !viaEditor)
        {
            foreach ((Assets.World.LoadZones all, _, string name) in moved)
            {
                try
                {
                    byte[] before = File.ReadAllBytes(all.SceneFile);
                    all.Save();
                    written.Add((all.SceneFile, before));
                }
                catch (Exception ex) when (ZoneWrites.IsFileTrouble(ex))
                {
                    var stuck = new List<string>();
                    foreach ((string file, byte[] before) in written)
                    {
                        try { File.WriteAllBytes(file, before); }
                        catch (Exception back) when (back is IOException or UnauthorizedAccessException) { stuck.Add(file); }
                    }
                    return $"could not write the scene of city_univers ({name}): {ex.Message} - nothing was changed"
                        + (stuck.Count > 0 ? ", EXCEPT that these could not be put back: " + string.Join(", ", stuck) : "");
                }
            }
            foreach ((Assets.World.LoadZones all, _, _) in moved) ZoneWrites.Landed(all, zone!);
        }

        results = [.. moved.Select(m => new LoadZoneMoveInfo(m.Copy, m.Move.Zone, m.Move.Face, m.Move.From, m.Move.To, Xyz(m.Move.BoxMin),
            Xyz(m.Move.BoxMax), m.Zones.DistrictsOf(m.Move.Zone), apply, apply ? m.Zones.SceneFile : null, m.Zones.Archive.FullName))];
        return null;
    }
}
