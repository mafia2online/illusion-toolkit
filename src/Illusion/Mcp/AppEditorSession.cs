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
        Window?.Viewport ?? throw new InvalidOperationException(NotOpen);

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

            // An instanced mesh's bounds span every copy across the map — not where this row is.
            bool bounded = node.Mesh is { Instanced: false };
            Vector3? position = node.Source is IFrameNode frame ? frame.WorldTransform.Translation : null;
            int? triangles = null;
            Vector3 insideMin = default, insideMax = default;
            if (lo is { } min && hi is { } max)
            {
                if (bounded)
                {
                    if (!Overlaps(node.Mesh!.BoundsMin, node.Mesh.BoundsMax, min, max)) continue;
                    triangles = TrianglesInBox(node.Mesh, min, max, out insideMin, out insideMax);
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
                bounded ? [node.Mesh!.BoundsMin.X, node.Mesh.BoundsMin.Y, node.Mesh.BoundsMin.Z] : null,
                bounded ? [node.Mesh!.BoundsMax.X, node.Mesh.BoundsMax.Y, node.Mesh.BoundsMax.Z] : null,
                node.IsSelected,
                triangles,
                triangles > 0 ? [insideMin.X, insideMin.Y, insideMin.Z] : null,
                triangles > 0 ? [insideMax.X, insideMax.Y, insideMax.Z] : null,
                node.Mesh?.PickPositions?.Length,
                node.Mesh?.PickIndices?.Length / 3));
        }
        return found;
    }

    /// <summary>How many of a mesh's triangles reach into a world-space box, and the extent of those
    /// triangles clipped to it. Null when the mesh keeps no CPU geometry to ask.</summary>
    private static int? TrianglesInBox(
        Rendering.Gpu.GpuMesh mesh, Vector3 boxMin, Vector3 boxMax, out Vector3 insideMin, out Vector3 insideMax)
    {
        insideMin = new Vector3(float.MaxValue);
        insideMax = new Vector3(float.MinValue);
        if (mesh.PickPositions is not { } positions || mesh.PickIndices is not { } indices) return null;

        Matrix4x4 world = mesh.World;
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
        if (Window is not { } window) return NotOpen;
        D3DImageHost host = window.Viewport;

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
        if (Window is not { } window) return NotOpen;
        if (window.Viewport.BridgeEditedCount == 0) return "no Blender edit session is open — blender_open first";
        return window.Viewport.RequestBridgePush() ? null : "the connection to Blender is gone — blender_open again";
    }

    public string? OpenInBlender()
    {
        if (Window is not { } window) return NotOpen;
        D3DImageHost host = window.Viewport;
        if (host.BridgeEditedCount > 0) return "a Blender edit session is already open — blender_end first";
        if (host.SelectedNodes.Count == 0) return "nothing is selected — scene_select first";
        host.OpenInBlender();
        return null;
    }

    public string? EndBlenderSession()
    {
        if (Window is not { } window) return NotOpen;
        if (window.Viewport.BridgeEditedCount == 0) return "no Blender edit session is open";
        window.Viewport.EndBridgeEditSession();
        return null;
    }

    public IReadOnlyList<EditorNotice> Notices(int last) => EditorNoticeLog.Last(last);

    public string? Save(out int filesWritten)
    {
        filesWritten = 0;
        if (Window is not { } window) return NotOpen;
        try
        {
            filesWritten = window.Viewport.SaveEdits();
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

    public CameraInfo Camera()
    {
        var pose = Host.CameraPose;
        return new CameraInfo(
            [pose.Position.X, pose.Position.Y, pose.Position.Z], pose.Yaw, pose.Pitch, pose.OrbitDistance);
    }

    public string? LookAt(float[] target, float radius, float[]? fromAxis)
    {
        if (Window is not { } window) return NotOpen;
        if (radius <= 0f) return "radius must be positive";
        window.Viewport.LookAt(
            new Vector3(target[0], target[1], target[2]),
            radius,
            fromAxis != null ? new Vector3(fromAxis[0], fromAxis[1], fromAxis[2]) : null);
        return null;
    }

    public string? SetCamera(float[] position, float[] target)
    {
        if (Window is not { } window) return NotOpen;
        var eye = new Vector3(position[0], position[1], position[2]);
        var at = new Vector3(target[0], target[1], target[2]);
        if (Vector3.DistanceSquared(eye, at) < 1e-6f) return "position and target are the same point";
        window.Viewport.LookFrom(eye, at);
        return null;
    }

    public bool FrameSelection() => Host.FrameSelection();

    public string? SetView(string? renderMode, bool? collision, bool? crash, bool? zones, bool? navigation)
    {
        if (Window is not { } window) return NotOpen;
        D3DImageHost host = window.Viewport;

        RenderMode mode = default;
        if (renderMode != null && !Enum.TryParse(renderMode, ignoreCase: true, out mode))
            return $"unknown shading mode '{renderMode}' — one of {string.Join(", ", Enum.GetNames<RenderMode>())}";
        if ((collision != null || crash != null) && host.BridgeEditedCount > 0)
            return "the collision and crash layers reload part of the scene — blender_end first";

        if (renderMode != null)
        {
            // The mode buttons are nameless (they sit inside a strip with its own namescope) and carry the
            // mode in Tag; checking the right one is what moves the viewport, through the window's handler.
            RadioButton? button = Descendants<RadioButton>(window)
                .FirstOrDefault(b => b.Tag is string tag && tag == mode.ToString());
            if (button != null) button.IsChecked = true;
            host.RenderMode = mode;
        }
        if (zones is { } showZones) window.ZonesToggle.IsChecked = showZones;
        if (crash is { } showCrash) window.CrashToggle.IsChecked = showCrash;
        if (collision is { } showCollision) window.CollisionToggle.IsChecked = showCollision;
        if (navigation is { } showNavigation) window.AiNavToggle.IsChecked = showNavigation;
        return null;
    }

    public string? Screenshot(string path, int width, int height)
    {
        if (Window is not { } window) return NotOpen;
        if (window.Viewport.CaptureFrame(width, height) is not { } pixels) return "the viewport is not rendering yet";
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
        if (Window is not { } window) return NotOpen;
        D3DImageHost host = window.Viewport;
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

    public string? DuplicateSelected(out IReadOnlyList<string> copies)
    {
        copies = [];
        if (Window is not { } window) return NotOpen;
        D3DImageHost host = window.Viewport;
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
        if (Window is not { } window) return NotOpen;
        D3DImageHost host = window.Viewport;
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
        if (Window is not { } window) return NotOpen;
        D3DImageHost host = window.Viewport;
        if (host.BridgeEditedCount > 0) return "a Blender edit session is open — blender_end first";
        if (!host.CanDeleteSelection()) return "nothing deletable is selected";
        deleted = host.SelectedNodes.Count;
        host.DeleteSelected();
        return null;
    }

    public string? Undo()
    {
        if (Window is not { } window) return NotOpen;
        if (!window.Viewport.History.CanUndo) return "nothing to undo";
        window.Viewport.Undo();
        return null;
    }

    public string? Redo()
    {
        if (Window is not { } window) return NotOpen;
        if (!window.Viewport.History.CanRedo) return "nothing to redo";
        window.Viewport.Redo();
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
