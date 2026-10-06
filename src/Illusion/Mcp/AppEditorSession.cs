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

    public string? OpenResource(string archive)
    {
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
        window.Reveal(sds);
        _resourceTarget = true;
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
            return new EditorStatus(false, null, false, false, 0, [], false, [], 0, "");
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

    public string? LoadArea(string area, bool winter)
    {
        if (Window is not { } window) return NotOpen;
        D3DImageHost host = window.Viewport;
        if (host.BridgeEditedCount > 0) return "a Blender edit session is open — blender_end first";

        MapArea? target = host.Areas.FirstOrDefault(
            a => string.Equals(a.BaseName, area, StringComparison.OrdinalIgnoreCase));
        if (target == null) return $"no area named '{area}' — editor_list_areas has the names";
        if (winter && target.Winter == null) return $"'{target.BaseName}' has no winter variant";

        // Each control reloads the scene when it changes and only then, so an area that is already the one
        // shown costs nothing here. The season goes first: the area selector's reload then reads it.
        if (window.WholeMapCheck.IsChecked == true) window.WholeMapCheck.IsChecked = false;
        if ((window.WinterToggle.IsChecked == true) != winter) window.WinterToggle.IsChecked = winter;
        if (!ReferenceEquals(window.AreaCombo.SelectedItem, target)) window.AreaCombo.SelectedItem = target;
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
            if (nameContains != null && !node.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (kind != null && !string.Equals(node.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;

            // What the row stands where: a drawn mesh, or — for a collision placement — its hull. An instanced
            // mesh has neither: its bounds span every copy across the map, not where this row is.
            Shape? shape = ShapeOf(node);
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
        return found;
    }

    /// <summary>The triangles a row occupies and their world-space bounds. Positions are null for a mesh that
    /// keeps no CPU geometry — its bounds still stand.</summary>
    private readonly record struct Shape(Vector3[]? Positions, uint[]? Indices, Matrix4x4 World, Vector3 Min, Vector3 Max);

    // Decoded hulls, by the cooked bytes they came from: a re-cooked hull is another array and is decoded again.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Tuple<Vector3[], uint[]>> Hulls = new();

    private static Shape? ShapeOf(SceneNode node)
    {
        if (node.Mesh is { Instanced: false } mesh)
        {
            return new Shape(mesh.PickPositions, mesh.PickIndices, mesh.World, mesh.BoundsMin, mesh.BoundsMax);
        }
        if (node.Source is not Assets.Adapters.CollisionInstanceAdapter placement) return null;

        byte[]? cooked = placement.Document.Collision.Meshes.FirstOrDefault(m => m.Hash == placement.Instance.Hash)?.CookedMesh;
        if (cooked == null) return null;
        Tuple<Vector3[], uint[]> hull = Hulls.GetValue(cooked, static bytes =>
        {
            try
            {
                Formats.Collisions.CookedTriangleMesh decoded = Formats.Collisions.CookedTriangleMesh.Decode(bytes);
                return Tuple.Create(decoded.Vertices, Array.ConvertAll(decoded.Triangles, i => (uint)i));
            }
            catch (Formats.Collisions.CollisionDecodeException)
            {
                return Tuple.Create(Array.Empty<Vector3>(), Array.Empty<uint>());
            }
        });
        if (hull.Item1.Length == 0) return null;

        Matrix4x4 world = placement.WorldTransform;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (Vector3 vertex in hull.Item1)
        {
            Vector3 at = Vector3.Transform(vertex, world);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }
        return new Shape(hull.Item1, hull.Item2, world, min, max);
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

    public string? Save(out int filesWritten)
    {
        filesWritten = 0;
        if (TargetHost is not { } host) return TargetNotOpen;
        try
        {
            filesWritten = host.SaveEdits();
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
        host.SaveEdits();
        D3DImageHost.BuildReport report = host.BuildEdits(createBackup: true);
        return new BuildOutcome(
            report.Packed.Select(p => (p.Archive, p.Backup)).ToList(),
            report.Failed.Select(f => (f.Archive, f.Error)).ToList());
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
            // The mirror reads the working copy on disk, so what is only in memory has to be written first.
            host.SaveEdits();
            Assets.Sds.SeasonMirror.Report? report =
                Assets.Sds.SeasonMirror.ToWinter(area.Summer, area.Winter, out string? reason);
            if (report == null) return reason ?? "the winter archive could not be written";

            host.MarkArchiveModified(area.Winter);
            outcome = new SeasonMirrorOutcome(area.Winter.FullName, report.Matched, report.Added, report.Dropped,
                report.Reshaped, report.Files, report.Textures);
            host.RaiseNotice(
                $"Mirrored {area.BaseName} into {area.Winter.Name}: {report.Matched} object(s) kept their winter "
                + $"materials, {report.Added} added, {report.Dropped} dropped, {report.Files.Count} file(s) and "
                + $"{report.Textures.Count} texture(s) written — Build packs it", isError: false);
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

    public string? SetView(string? renderMode, bool? collision, bool? crash, bool? zones, bool? navigation)
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

        Vector3 delta = Vector3.Zero;
        if (position != null) delta = new Vector3(position[0], position[1], position[2]) - frame.WorldTransform.Translation;
        if (offset != null) delta += new Vector3(offset[0], offset[1], offset[2]);
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
        return node == null ? reason ?? "the pack refused the actor" : null;
    }

    public string? ImportObject(string sourceArchive, string name, string newName, float[] position, float? yawDegrees,
        string? collision, out ObjectImportOutcome? outcome)
    {
        outcome = null;
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
        return host.ObjectImporting.Import(destination, sourceArchive, name, newName,
            new Vector3(position[0], position[1], position[2]), yawDegrees, out outcome, hulls);
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

    private static bool TryParse(Domain.Properties.PropertyDescriptor p, string text, out object? value)
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
                if (!float.TryParse(text, Num, inv, out float f)) return false;
                value = f;
                return true;
            case Domain.Properties.PropertyKind.Bool:
                if (!bool.TryParse(text, out bool b)) return false;
                value = b;
                return true;
            case Domain.Properties.PropertyKind.Text:
                value = text;
                return true;
            case Domain.Properties.PropertyKind.UInt64Hex:
                string hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
                if (!ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, inv, out ulong h)) return false;
                value = h;
                return true;
            case Domain.Properties.PropertyKind.Vector3:
                string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 3 || !float.TryParse(parts[0], Num, inv, out float x)
                    || !float.TryParse(parts[1], Num, inv, out float y) || !float.TryParse(parts[2], Num, inv, out float z))
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

        Assets.EntityData.TuningEditing.Change? change =
            Assets.EntityData.TuningEditing.Set(chosen.Path, chosen.Index, target.Offset, parsed, target.Name);
        if (change == null) return $"{target.Name} could not be written";

        // The same bookkeeping the Tuning tab does: an undo entry, the archive on the build list, the panel re-read.
        D3DImageHost stage = window.TargetStage;
        void Reload() => window.Scene.Selection.ReloadTuning();
        stage.History.Push(new TuningValueEdit(change, Reload));
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

    public string? BuildArchive(string archive, string? memoryFrom, out PackedArchive? result)
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
            Assets.Sds.SdsWriter.PackResult packed = Assets.Sds.SdsWriter.PackSds(sds, createBackup: true);
            result = new PackedArchive(packed.Archive, packed.Backup);
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
                exported.Vehicles, exported.Files, exported.Notes);
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

    public string? HideTriangles(string name, float[] boxMin, float[] boxMax, string? material, bool apply, int sample,
        out HiddenTrianglesInfo? result)
    {
        result = null;
        if (TargetHost is not { } host) return TargetNotOpen;
        if (boxMin is not { Length: 3 } || boxMax is not { Length: 3 }) return "boxMin and boxMax are [x, y, z]";
        if (Resolve(host, name, out SceneNode? node) is { } unresolved) return unresolved;
        if (node!.Source is not Assets.Adapters.FrameNodeAdapter { Frame: Formats.Frames.ObjectTypes.FrameObjectSingleMesh mesh } adapter)
        {
            return $"'{name}' is a {node.Kind} — only a mesh has triangles to hide";
        }
        if (apply && host.BridgeEditedCount > 0) return "a Blender session is open — blender_end first";

        Assets.Sds.TriangleHider.Plan plan = Assets.Sds.TriangleHider.Find(mesh, ((IFrameNode)adapter).WorldTransform,
            new Vector3(boxMin[0], boxMin[1], boxMin[2]), new Vector3(boxMax[0], boxMax[1], boxMax[2]),
            string.IsNullOrWhiteSpace(material) ? null : material);
        if (apply && plan.Changes.Count > 0 && host.GeometryEditing.HideTriangles(node, plan.Changes) is { } refused) return refused;

        static float[] P(Vector3 v) => [v.X, v.Y, v.Z];
        int levels = plan.Triangles.Count == 0 ? 0 : plan.Triangles.Max(t => t.Lod) + 1;
        result = new HiddenTrianglesInfo(
            plan.Triangles.Count,
            [.. Enumerable.Range(0, levels).Select(l => plan.Triangles.Count(t => t.Lod == l))],
            apply && plan.Changes.Count > 0,
            [.. plan.Triangles.Take(Math.Clamp(sample, 0, 500)).Select(t => new TriangleInfo(t.Lod, t.Material, P(t.A), P(t.B), P(t.C)))]);
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
        host.Undo();
        return null;
    }

    public string? Redo()
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (!host.History.CanRedo) return "nothing to redo";
        host.Redo();
        return null;
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
}
