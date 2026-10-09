using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Drives the running map editor: load an area, find and select objects, send the selection to Blender,
/// read what the editor answered, save, build, move the camera, take a picture of the viewport.
/// <para>
/// Everything here touches the scene or the UI, so every call goes through <see cref="IUiThreadMarshal"/>.
/// The waits (an area streaming in, Blender answering) are polled from the tool's own thread between
/// short hops onto the UI thread — a tool never parks the dispatcher.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class EditorTools
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    [McpServerTool(Name = "editor_status")]
    [Description("What the map editor is doing: whether it is open, the loaded area, whether it is still loading, the selection, unsaved edits, archives waiting for a Build, how many objects are open in Blender, and the shading mode. Call this first.")]
    public static async Task<string> Status(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            return ToolResult.Json(new { success = true, status = await ui.RunAsync(editor.Status) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "editor_target")]
    [Description("Choose which editor the scene tools drive: 'map' (the map editor, the default) or 'resource' (the resource editor — one archive such as a car on a stage). scene_find, scene_select, object_properties, object_set_property, object_move, scene_duplicate_selected, scene_delete_selected, camera_*, viewport_screenshot, view_set (shading only), editor_save, editor_build, editor_undo/redo and the blender_* tools all follow it. resource_open switches to 'resource' by itself.")]
    public static async Task<string> SetTarget(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("'map' or 'resource'.")] string target)
    {
        try
        {
            string? note = await ui.RunAsync(() => editor.SetTarget(target));
            return ToolResult.Json(new { success = true, note, resource = await ui.RunAsync(editor.ResourceStatus) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "resource_list")]
    [Description("List archives of the game's library — cars, characters, city objects, interiors — by name fragment and/or folder fragment ('cars', 'hchar', 'shops'). Each: name, path under pc\\sds, kind, size, and whether it already has a working copy.")]
    public static async Task<string> ResourceList(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Part of the archive name, e.g. 'shubert'. Omit for all.")] string? query = null,
        [Description("Part of the folder path, e.g. 'cars'. Omit for all.")] string? folder = null,
        [Description("At most this many. Default 100.")] int limit = 100)
    {
        try
        {
            IReadOnlyList<LibraryItem> items = await ui.RunAsync(() => editor.Library(query, folder, limit));
            return ToolResult.Json(new { success = true, count = items.Count, archives = items });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "resource_open")]
    [Description("Open an archive in the resource editor (opening the window if needed) and make it the target of the scene tools. Waits until it is on the stage. A car opens with its component tree (doors, bumpers, wheels) and its tuning.")]
    public static async Task<string> ResourceOpen(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The archive: a path under pc\\sds such as 'cars/shubert_38.sds', a full path, or a bare name such as 'shubert_38'.")] string archive,
        [Description("How long to wait for the load, in seconds. Default 120.")] int timeoutSeconds = 120)
    {
        try
        {
            string? wanted = null;
            if (await ui.RunAsync(() => editor.OpenResource(archive, out wanted)) is { } refused) return ToolResult.Invalid(refused);
            DateTime until = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 600));
            ResourceStatus status;
            DateTime? emptySince = null;
            bool staged;
            do
            {
                await Task.Delay(400);
                status = await ui.RunAsync(editor.ResourceStatus);
                // "Something is loaded" is not "what was asked for is loaded": the stage keeps showing the
                // previous archive until the new one takes its place, and that one passes every other test.
                staged = string.Equals(status.ArchivePath, wanted, StringComparison.OrdinalIgnoreCase);
                // An archive with nothing to draw (a texture pack) is on the stage with no meshes for good.
                if (staged && !status.Loading && status.Meshes == 0) emptySince ??= DateTime.UtcNow;
                else emptySince = null;
            }
            while ((!staged || status.Loading || (status.Meshes == 0 && DateTime.UtcNow - emptySince < TimeSpan.FromSeconds(4)))
                   && DateTime.UtcNow < until);
            if (!staged)
            {
                return ToolResult.Json(new
                {
                    success = false,
                    error = $"the resource editor did not put {Path.GetFileName(wanted)} on its stage — it shows "
                        + (status.Archive ?? "nothing") + ". The tools still act on what it shows",
                    resource = status,
                });
            }
            return ToolResult.Json(new { success = true, loaded = !status.Loading && status.Meshes > 0, resource = status });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "resource_status")]
    [Description("The resource editor's state: open or not, which editor the scene tools drive, the archive on its stage, loading, meshes, selection, unsaved edits, pending builds.")]
    public static async Task<string> ResourceStatusTool(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            return ToolResult.Json(new { success = true, resource = await ui.RunAsync(editor.ResourceStatus) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    // The status of the editor editor_target points at: the resource editor's, or the map editor's.
    private static async Task<object> StatusOf(IEditorSession editor, IUiThreadMarshal ui)
    {
        ResourceStatus resource = await ui.RunAsync(editor.ResourceStatus);
        return resource.Target == "resource" ? resource : await ui.RunAsync(editor.Status);
    }

    [McpServerTool(Name = "car_tuning")]
    [Description("A car's tuning (entity data) in the resource editor: its tables — a car ships a stock one and tuned variants, labelled by mass and power — and the fields of one table: band (Body, Engine, Gearbox, Wheels…), element (a wheel, a gear), label, name, kind and value. Filter with a name fragment.")]
    public static async Task<string> CarTuning(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Which table, 1-based. Default 1.")] int table = 1,
        [Description("Part of a field's name or label, e.g. 'mass', 'gear', 'torque'. Omit for all.")] string? query = null,
        [Description("At most this many fields. Default 200.")] int limit = 200)
    {
        try
        {
            IReadOnlyList<TuningTableInfo> tables = [];
            IReadOnlyList<TuningFieldInfo> fields = [];
            string? refused = await ui.RunAsync(() => editor.Tuning(table, query, limit, out tables, out fields));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, tables, count = fields.Count, fields });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_tuning_set")]
    [Description("Set one field of a car's tuning table (undoable with editor_undo; written to the working copy at once, packed into the archive by editor_build). Values: a number, true/false for a flag, 'x, y, z' for a vector, text for a name. When the name repeats — every wheel and gear has the same fields — give band and/or element as car_tuning lists them.")]
    public static async Task<string> CarTuningSet(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The field's name, as car_tuning lists it (e.g. 'Mass', 'Wheel0.Scale'); a wheel's or gear's field may be given without its prefix ('Scale') together with element.")] string field,
        [Description("The new value, as text.")] string value,
        [Description("Which table, 1-based. Default 1.")] int table = 1,
        [Description("The band, when the name repeats across bands.")] string? band = null,
        [Description("The element (a wheel, a gear), when the name repeats inside the band.")] string? element = null)
    {
        try
        {
            TuningFieldInfo? result = null;
            string? refused = await ui.RunAsync(() => editor.SetTuning(table, field, band, element, value, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, field = result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_clone")]
    [Description("Make a new car out of an existing one for single player: copies pc\\sds\\cars\\<source>.sds (and its winter _z twin) to <name in lower case>.sds with the root frame, prefab entry, entity data and geometry buffers renamed, adds the car to vehicles.tbl under a new id (class, price and flags of the source), to PaintCombinations.tbl and AiProps, and — with traffic — to every CARM* traffic row that can pick the source. Then builds the new archives plus tables.sds and ingame.sds (backups kept). Only the main game's tables are edited: the story DLCs ship an ingame.sds of their own and the clone is not in those (the result's notes say which). A source that is not keyed by its model name the way a stock car is, or anything failing on the way, is refused and everything written is taken back. A source open in an editor with unsaved edits is refused — save it first. The game must not be running. Open the clone with resource_open and tune it with car_tuning_set.")]
    public static async Task<string> CarClone(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The car to copy, by archive or model name, e.g. 'shubert_38'.")] string source,
        [Description("The new model name: a letter, then letters, digits and '_', at most 31 characters, e.g. 'Shubert_38_Sport'.")] string name,
        [Description("Let traffic pick the clone wherever it picks the source. Default true.")] bool traffic = true,
        [Description("The name the game shows for the car (garage, shop), e.g. 'Shubert 38 Custom'. Written into the text table of every installed language under a new text id. Omit to share the source car's name.")] string? title = null)
    {
        try
        {
            CarCloneInfo? result = null;
            string? refused = await ui.RunAsync(() => editor.CloneCar(source, name, traffic, title, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, clone = result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "archive_build")]
    [Description("Pack ONE archive's working copy (the extracted folder under <game>\\resources) back into its .sds, keeping a timestamped backup of the archive it replaces. For an edit made directly in the working copy — a script, a table file — that no editor session tracks; editor_build remains the way to pack what the editors changed. This OVERWRITES a game file: the game must not be running.")]
    public static async Task<string> ArchiveBuild(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Full path of the .sds archive in the game folder (pc\\sds\\… or pc\\dlcs\\…).")] string archive,
        [Description("Build although the manifest names files that are not in the working copy. Default false: such a build is refused and the files are listed — put them back first. With true they are left out of the archive AND their manifest entries are removed for good; restoring a file later does not bring its entry back.")] bool dropMissing = false,
        [Description("Full path of an archive to take the per-resource memory requirements from, for an archive that never shipped itself: a cloned car built before requirements were kept takes them from the stock car it was cloned from. Omit normally — a working copy keeps the requirements of the archive it was extracted from.")] string? memoryFrom = null)
    {
        try
        {
            PackedArchive? result = null;
            string? refused = await ui.RunAsync(() => editor.BuildArchive(archive, memoryFrom, dropMissing, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, packed = result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "mesh_hide_triangles")]
    [Description("Cut an opening into a mesh WITHOUT rebuilding it: the triangles of the named mesh whose three corners all lie inside a world-space box are hidden on every level of detail (their indices are pointed at one vertex). No vertex is touched, so a stock facade keeps the channels Blender never sees (shadow-map UVs) — use this, not a Blender push, to open a painted door or garage shutter of a stock building. By default it only REPORTS what the box would take (count per LOD and the first triangles with their material and corners); pass apply=true to hide them as one undoable edit. Saved by editor_save, packed by editor_build. A mesh that shares its geometry with other objects is refused unless 'shared' says what to do about them.")]
    public static async Task<string> MeshHideTriangles(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The mesh, by name or path suffix (scene_find has the names).")] string name,
        [Description("Lower corner of the box, world space [x, y, z].")] float[] boxMin,
        [Description("Upper corner of the box, world space [x, y, z].")] float[] boxMax,
        [Description("Only triangles whose material name contains this. Omit for any material.")] string? material = null,
        [Description("Hide the triangles. Default false: report only.")] bool apply = false,
        [Description("How many of the found triangles to list (0-500). Default 40.")] int sample = 40,
        [Description("What to do when other objects draw the same geometry (the districts reuse it heavily): omit to refuse, 'own' gives this mesh a copy of the geometry of its own first and cuts it alone, 'all' hides the triangles on every object that draws it.")] string? shared = null)
    {
        try
        {
            HiddenTrianglesInfo? result = null;
            string? refused = await ui.RunAsync(() => editor.HideTriangles(name, boxMin, boxMax, material, apply, sample, shared, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, hidden = result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "mesh_materials")]
    [Description("The material slots of a mesh - which material each part of it is drawn with, and how many triangles that part has. With 'slot' and 'material', that slot is first re-pointed at another material (what the Materials tab's 'assign to slot' does): one undoable edit that touches no geometry and no UV, so the new texture is laid out the way the old one was - look at the result. Further levels of detail follow wherever they used the slot's old material. The material is named exactly as search_materials spells it. A district's winter archive is edited separately. The hull's surface type (what footsteps sound like, where grass tufts grow) is the collision's own and does not change with this.")]
    public static async Task<string> MeshMaterials(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The mesh: its name, or its path when the name is not unique (scene_find gives both).")] string name,
        [Description("Slot to re-point. Omit, with 'material', to only list the slots.")] int? slot = null,
        [Description("Material to point the slot at, by exact name.")] string? material = null)
    {
        try
        {
            IReadOnlyList<MeshSlotInfo> slots = [];
            string? refused = await ui.RunAsync(() => editor.MeshMaterials(name, slot, material, out slots));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, changed = slots.Any(s => s.Changed), slots });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "collision_unused_hulls")]
    [Description("Count the collision hulls no placement references — dead weight a delete, a re-cook or a resize leaves behind in the .col (deleting a placement never removes its hull, so undo can put the file back). For every collision file of the open scene: its placements, its hulls and how many of those are unused. By default it only REPORTS; pass apply=true to remove them, all files as one undoable edit. Placements are never touched, so nothing changes in the game except the archive's size. Saved by editor_save, packed by editor_build; run editor_mirror_winter afterwards so the winter district loses them too.")]
    public static async Task<string> CollisionUnusedHulls(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Remove the unused hulls. Default false: count only.")] bool apply = false)
    {
        try
        {
            IReadOnlyList<UnusedHullsInfo> layers = [];
            string? refused = await ui.RunAsync(() => editor.UnusedHulls(apply, out layers));
            return refused != null
                ? ToolResult.Invalid(refused)
                : ToolResult.Json(new { success = true, unused = layers.Sum(l => l.Unused), removed = apply, layers });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "crash_placements")]
    [Description("The city_crash props — trees, bushes, lamps, bins, mailboxes — standing inside a world-space box: each one's prop name, placement id, position and whether the other season holds the same placement. scene_find only shows the prop TYPES of that layer ('lampLuxus — 31'); this is how to see where the copies stand. The layer must be in the scene (view_set crash=true). By default it only LISTS; pass delete=true to remove every placement the box (and the name filter) takes, as one undoable edit — a placement linked to the other season is removed there too. city_crash is ONE archive for the whole city: keep the box tight. Saved by editor_save, packed by editor_build.")]
    public static async Task<string> CrashPlacements(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Lower corner of the box, world space [x, y, z].")] float[] boxMin,
        [Description("Upper corner of the box, world space [x, y, z].")] float[] boxMax,
        [Description("Only props whose name contains this. Omit for every prop.")] string? nameContains = null,
        [Description("Delete the placements found. Default false: list only.")] bool delete = false,
        [Description("How many placements to list (the count is always complete; a delete lists everything it removed). Default 200.")] int limit = 200,
        [Description("The most placements one delete may remove. With more than this in the box the call is refused and nothing is deleted. Default 100.")] int maxDelete = 100)
    {
        try
        {
            IReadOnlyList<CrashPlacementInfo> placements = [];
            int total = 0;
            string? refused = await ui.RunAsync(() =>
                editor.CrashPlacements(boxMin, boxMax, nameContains, delete, limit, maxDelete, out placements, out total));
            return refused != null
                ? ToolResult.Invalid(refused)
                : ToolResult.Json(new
                {
                    success = true,
                    count = total,
                    deleted = delete ? total : 0,
                    // Gone from the OTHER season as well: only the linked ones. An unlinked placement that
                    // has a twin keeps it.
                    deletedInOtherSeasonToo = delete ? placements.Count(p => p.BothSeasons && p.SeasonLinked) : 0,
                    placements,
                });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_substitute")]
    [Description("Build one car under ANOTHER car's name: pc\\sds\\cars\\<target>.sds is replaced by the source car's model, with the root frame, name table, prefab entry, entity data and buffers keyed by the target's model name. No table is touched — the game lists the target as before and finds the source's shape and tuning in its archive. For trying a car where nothing can be registered (a multiplayer that spawns from a fixed list of names). A timestamped backup of each replaced archive is kept in cars\\backups; the winter _z twin is replaced only where both cars have one. This OVERWRITES game files and the target's working copy: the game must not be running.")]
    public static async Task<string> CarSubstitute(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The car whose model is used, by archive or model name, e.g. 'shubert_38_custom'.")] string source,
        [Description("The car whose archive is replaced, e.g. 'shubert_38_destr'.")] string target)
    {
        try
        {
            CarSubstituteInfo? result = null;
            string? refused = await ui.RunAsync(() => editor.SubstituteCar(source, target, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, substitute = result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_export_m2o")]
    [Description("Export a built car as a Mafia II Online resource folder: package.json and stream/sds/cars/<name>.sds with its winter _z twin — one archive per car, which the server registers by file name (a stock car's name replaces that car). Materials the car uses that the game did not ship with (ones the toolkit created) go into stream/materials/<name>.mtl, a library the multiplayer loads in addition to the game's own the way the game loads a mission pack's (LoadMTL, released when the player leaves); a server refuses a car whose new textures no streamed material uses. Takes pc\\sds\\cars\\<car>.sds as it stands (build first) and refuses an archive that is not filed under its own name throughout, or a material no library has. A second export into the same folder adds a car. Writes only the output folder — nothing of the game or of the multiplayer.")]
    public static async Task<string> CarExportM2o(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The car, by archive or model name, e.g. 'shubert_38_custom'.")] string car,
        [Description("Full path of the folder to write. Default: <game>\\_illusion_export\\m2o\\<resource>. Must be empty or an earlier export in this layout.")] string? output = null,
        [Description("The resource's name (lower-case letters, digits, '-', '_', '.'). Default: 'car-' + the car's name with '-' for '_'.")] string? resource = null)
    {
        try
        {
            M2oExportInfo? result = null;
            string? refused = await ui.RunAsync(() => editor.ExportCarForM2o(car, output, resource, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, export = result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "editor_list_areas")]
    [Description("Names of the areas (districts and interiors) the map editor can load. Empty until the editor is open — editor_open_area opens it.")]
    public static async Task<string> ListAreas(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            IReadOnlyList<string> areas = await ui.RunAsync(editor.Areas);
            return ToolResult.Json(new { success = true, count = areas.Count, areas });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "editor_open_area")]
    [Description("Open the map editor if it is still on the launcher, load an area and wait until it has finished streaming in. Returns the editor status. Refuses while a Blender edit session is open, and refuses to replace a scene that has unsaved edits (editor_save first, or pass discardUnsavedEdits=true); asking for the area and season already shown is always a no-op.")]
    public static async Task<string> OpenArea(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Area name as editor_list_areas spells it, e.g. 'uppertown'.")] string area,
        [Description("Load the winter variant (_z archives). Default false.")] bool winter = false,
        [Description("How long to wait for the area to load, in seconds. Default 180.")] int timeoutSeconds = 180,
        [Description("Load even though the scene has unsaved edits — they and the undo history are lost. Default false.")] bool discardUnsavedEdits = false)
    {
        try
        {
            if (await ui.RunAsync(editor.EnsureEditor) is { } cannotOpen) return ToolResult.Invalid(cannotOpen);

            // The editor fills its area list from catalogs it reads on a background thread.
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while ((await ui.RunAsync(editor.Areas)).Count == 0)
            {
                if (DateTime.UtcNow > deadline) return ToolResult.Invalid("the editor opened but its area list never filled");
                await Task.Delay(Poll);
            }

            if (await ui.RunAsync(() => editor.LoadArea(area, winter, discardUnsavedEdits)) is { } refused)
                return ToolResult.Invalid(refused);

            deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, timeoutSeconds));
            DateTime quietSince = DateTime.UtcNow;
            int lastMeshes = -1;
            while (true)
            {
                await Task.Delay(Poll);
                EditorStatus status = await ui.RunAsync(editor.Status);
                if (status.Loading || status.Meshes != lastMeshes)
                {
                    lastMeshes = status.Meshes;
                    quietSince = DateTime.UtcNow;
                }
                // Loaded means: nothing queued, and the mesh count has stopped moving for a moment.
                if (!status.Loading && status.Meshes > 0 && DateTime.UtcNow - quietSince > TimeSpan.FromSeconds(1.5))
                    return ToolResult.Json(new { success = true, status });
                if (DateTime.UtcNow > deadline)
                    return ToolResult.Json(new { success = false, error = "timed out waiting for the area to load", status });
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "scene_find")]
    [Description("Find objects in the loaded scene by a fragment of their name, by kind, and/or by a world-space box. Returns name, kind, tree path, position and bounds. With a box, a mesh is returned only when its TRIANGLES reach into the box (not merely its bounds), with TrianglesInBox and the extent of those triangles clipped to the box — the way to check that a volume is free before building in it. Objects without a mesh match a box by their position. Crash-layer copies (kind CrashInstance: trees, lamps, bins) are searched from the placement table whether or not their rows are expanded in the tree; a copy matches a name by its own label or by its prop's name, and a box by its prop's triangles at the copy's placement. Use it also to learn exact names before scene_select. A collision placement (kind 'CollisionInstance', named 'instance N') is found by its HULL: its bounds, vertex and triangle counts are the hull's, and a box is tested against the hull's triangles.")]
    public static async Task<string> Find(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Case-insensitive fragment of the object name. Omit to match any name.")] string? nameContains = null,
        [Description("Node kind to keep, e.g. 'Mesh', 'Frame', 'Light', 'Collision'. Omit for any.")] string? kind = null,
        [Description("Minimum corner [x, y, z] of a world-space box. Give boxMin and boxMax together.")] float[]? boxMin = null,
        [Description("Maximum corner [x, y, z] of that box.")] float[]? boxMax = null,
        [Description("Most rows to return. Default 50.")] int limit = 50)
    {
        try
        {
            if ((boxMin == null) != (boxMax == null) || (boxMin != null && (boxMin.Length != 3 || boxMax!.Length != 3)))
                return ToolResult.Invalid("boxMin and boxMax go together, three numbers each");
            IReadOnlyList<SceneObjectInfo> found = await ui.RunAsync(
                () => editor.Find(nameContains, kind, boxMin, boxMax, limit <= 0 ? 50 : limit));
            return ToolResult.Json(new { success = true, count = found.Count, objects = found });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "scene_select")]
    [Description("Replace the editor's selection with the named objects; an empty list clears it (a selection is outlined through walls, so clear it before a screenshot that should show what the player sees). A name is the object's name; when two objects share it, give enough of the tree path (as scene_find reports it) to tell them apart. While a Blender session is open only the objects in that session can be selected.")]
    public static async Task<string> Select(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Object names (or path suffixes) to select together. Empty clears the selection.")] string[] names)
    {
        try
        {
            if (await ui.RunAsync(() => editor.Select(names)) is { } refused) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, status = await StatusOf(editor, ui) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "blender_open")]
    [Description("Send the current selection to Blender for editing (the editor's Tab) and wait until Blender has loaded it. Blender is launched if none is running with the bridge. Returns what the editor reported.")]
    public static async Task<string> BlenderOpen(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("How long to wait for Blender, in seconds. Default 120.")] int timeoutSeconds = 120)
    {
        try
        {
            DateTime started = DateTime.Now;
            if (await ui.RunAsync(editor.OpenInBlender) is { } refused) return ToolResult.Invalid(refused);
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, timeoutSeconds));
            while (true)
            {
                await Task.Delay(Poll);
                // Of the editor the session was opened in: with the resource editor as the target, the map's
                // count stays at zero for good and this used to run out its whole timeout on a session that
                // had opened in the first second.
                ResourceStatus resource = await ui.RunAsync(editor.ResourceStatus);
                int inBlender = resource.Target == "resource"
                    ? resource.BlenderObjects
                    : (await ui.RunAsync(editor.Status)).BlenderObjects;
                var said = (await ui.RunAsync(() => editor.Notices(8))).Where(n => n.Time >= started).ToList();
                if (inBlender > 0)
                    return ToolResult.Json(new { success = true, blenderObjects = inBlender, notices = said });
                if (said.Any(n => n.Error))
                    return ToolResult.Json(new { success = false, error = "the editor refused", notices = said });
                if (DateTime.UtcNow > deadline)
                    return ToolResult.Json(new { success = false, error = "timed out waiting for Blender", notices = said });
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "blender_push")]
    [Description("Ask Blender to push the edit session's objects back as they are now (the addon's Push button), wait for the push to land and return what the editor reported: objects applied, what was rebuilt or re-cooked, what was refused and why. After a push that rebuilt topology, blender_end and blender_open again before editing further.")]
    public static async Task<string> BlenderPush(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("How long to wait for the push to land, in seconds. Default 120.")] int timeoutSeconds = 120)
    {
        try
        {
            DateTime started = DateTime.Now;
            if (await ui.RunAsync(editor.RequestBlenderPush) is { } refused) return ToolResult.Invalid(refused);
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, timeoutSeconds));
            while (true)
            {
                await Task.Delay(Poll);
                var said = (await ui.RunAsync(() => editor.Notices(8))).Where(n => n.Time >= started).ToList();
                // Either line ends a push: the summary every applied push prints, or the failure to apply it.
                if (said.FirstOrDefault(n => n.Text.Contains("Blender push", StringComparison.Ordinal)) is { } landed)
                    return ToolResult.Json(new { success = !landed.Error, notices = said });
                if (DateTime.UtcNow > deadline)
                {
                    return ToolResult.Json(new
                    {
                        success = false,
                        error = "no push arrived — Blender sends nothing when nothing changed since the last push",
                        notices = said,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "blender_end")]
    [Description("Leave the Blender edit session (the editor's Esc). Everything already pushed stays in the scene; the bridge objects disappear from Blender.")]
    public static async Task<string> BlenderEnd(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            if (await ui.RunAsync(editor.EndBlenderSession) is { } refused) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, status = await StatusOf(editor, ui) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "editor_notices")]
    [Description("What the editor has said recently, oldest first — most importantly the result of each Blender push: how many objects were applied, which were skipped and why, what was re-cooked or rebuilt. Read this after every push; it is the only place a refusal is explained.")]
    public static async Task<string> Notices(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("How many of the most recent notices to return. Default 10.")] int last = 10)
    {
        try
        {
            IReadOnlyList<EditorNotice> notices = await ui.RunAsync(() => editor.Notices(last <= 0 ? 10 : last));
            return ToolResult.Json(new { success = true, now = DateTime.Now, count = notices.Count, notices });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "editor_save")]
    [Description("Save every unsaved edit into the working copies (the editor's Ctrl+S): scene documents, collisions and material libraries. Does not touch the game's archives — editor_build does that. success is false, with notSaved naming each one, when something could not be written (a material library, a refused working copy); what could be written still was.")]
    public static async Task<string> Save(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            int files = 0;
            IReadOnlyList<string> notSaved = [];
            string? failed = await ui.RunAsync(() => editor.Save(out files, out notSaved));
            if (failed != null) return ToolResult.Invalid(failed);
            object status = await StatusOf(editor, ui);
            return notSaved.Count == 0
                ? ToolResult.Json(new { success = true, filesWritten = files, status })
                : ToolResult.Json(new
                {
                    success = false,
                    error = "the save did not complete — see notSaved",
                    filesWritten = files,
                    notSaved,
                    status,
                });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "editor_build")]
    [Description("Save, then pack every archive that has edits back into the game's .sds files, keeping a timestamped backup of each archive it replaces. This OVERWRITES game files — the game must not be running. Returns each packed archive with its backup, and each failure.")]
    public static async Task<string> Build(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            BuildOutcome outcome = await ui.RunAsync(editor.Build);
            if (outcome.NotSaved.Count > 0)
            {
                return ToolResult.Json(new
                {
                    success = false,
                    error = "the save a build starts with did not complete, so no archive was packed — see notSaved",
                    notSaved = outcome.NotSaved,
                });
            }
            if (outcome.Packed.Count == 0 && outcome.Failed.Count == 0)
                return ToolResult.Invalid("no edits to build — nothing has been edited in this editor session");
            return ToolResult.Json(new
            {
                success = outcome.Failed.Count == 0,
                packed = outcome.Packed.Select(p => new { archive = p.Archive, backup = p.Backup }),
                failed = outcome.Failed.Select(f => new { archive = f.Archive, error = f.Error }),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "editor_mirror_winter")]
    [Description("Carry the loaded district's edits into its winter archive (<name>_z.sds). The two archives are one scene shipped twice, differing only in which materials are swapped for their snow-covered counterparts and in their textures - so this saves, writes the summer scene over the winter one with the materials the seasons differ in put back (a slot re-pointed at another material in summer keeps the new one: slotsReassigned), copies the buffers, collisions, actors and name table across, and adds the textures winter lacks or refreshes the ones an earlier mirror brought. objectsAmbiguous counts objects that could not be told from a namesake and kept summer's materials - look at those. Refused for a district whose winter archive is a scene of its own. It writes the winter WORKING COPY and queues the archive; editor_build then packs it. Load the summer variant first.")]
    public static async Task<string> MirrorWinter(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            SeasonMirrorOutcome? outcome = null;
            string? failed = await ui.RunAsync(() => editor.MirrorToWinter(out outcome));
            if (failed != null || outcome == null) return ToolResult.Invalid(failed ?? "nothing was mirrored");
            return ToolResult.Json(new
            {
                success = true,
                winterArchive = outcome.WinterArchive,
                objectsMatched = outcome.Matched,
                objectsAdded = outcome.Added,
                objectsDropped = outcome.Dropped,
                slotsReassigned = outcome.Reassigned,
                objectsAmbiguous = outcome.Ambiguous,
                filesWritten = outcome.Files,
                texturesWritten = outcome.Textures,
                status = await ui.RunAsync(editor.Status),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "camera_get")]
    [Description("Where the viewport camera is: position, yaw and pitch (radians) and orbit distance.")]
    public static async Task<string> CameraGet(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            return ToolResult.Json(new { success = true, camera = await ui.RunAsync(editor.Camera) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "camera_look_at")]
    [Description("Point the viewport camera at a world position, framing a sphere of the given radius. fromAxis is the direction from the target towards the camera, e.g. [0, -1, 0.3] to stand south of it and a little above; omit it to keep the current viewing direction.")]
    public static async Task<string> CameraLookAt(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("World position [x, y, z] to look at.")] float[] target,
        [Description("Radius of the sphere to frame, in metres. Default 10.")] float radius = 10f,
        [Description("Direction [x, y, z] from the target towards the camera. Omit to keep the current direction.")] float[]? fromAxis = null)
    {
        try
        {
            if (target.Length != 3 || (fromAxis != null && fromAxis.Length != 3))
                return ToolResult.Invalid("target and fromAxis take three numbers each");
            if (await ui.RunAsync(() => editor.LookAt(target, radius, fromAxis)) is { } refused) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, camera = await ui.RunAsync(editor.Camera) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "camera_set")]
    [Description("Stand the viewport camera at an exact world position, looking at a world target. Use this to look around inside a room; camera_look_at is for framing something from outside.")]
    public static async Task<string> CameraSet(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("World position [x, y, z] of the camera.")] float[] position,
        [Description("World position [x, y, z] the camera looks at.")] float[] target)
    {
        try
        {
            if (position.Length != 3 || target.Length != 3)
                return ToolResult.Invalid("position and target take three numbers each");
            if (await ui.RunAsync(() => editor.SetCamera(position, target)) is { } refused) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, camera = await ui.RunAsync(editor.Camera) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "camera_frame_selection")]
    [Description("Move the viewport camera so the current selection fills the view.")]
    public static async Task<string> FrameSelection(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            if (!await ui.RunAsync(editor.FrameSelection)) return ToolResult.Invalid("nothing selected to frame");
            await Task.Delay(TimeSpan.FromSeconds(0.5)); // the camera glides there; report where it came to rest
            return ToolResult.Json(new { success = true, camera = await ui.RunAsync(editor.Camera) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "view_set")]
    [Description("Change how the viewport draws: the shading mode ('Render' = full lighting with normal and specular maps, 'MaterialPreview' = diffuse only, 'Solid', 'Wireframe') and the overlay layers. Anything omitted is left as it is.")]
    public static async Task<string> SetView(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Shading mode: Render, MaterialPreview, Solid or Wireframe.")] string? renderMode = null,
        [Description("Show collision hulls.")] bool? collision = null,
        [Description("Show the city_crash prop layer.")] bool? crash = null,
        [Description("Show district load zones.")] bool? zones = null,
        [Description("Show AI navigation overlays.")] bool? navigation = null,
        [Description("Switch the crash layer off even though the scene has unsaved edits — those made in it are lost with their undo entries. Default false.")] bool discardUnsavedEdits = false)
    {
        try
        {
            if (await ui.RunAsync(() => editor.SetView(renderMode, collision, crash, zones, navigation, discardUnsavedEdits)) is { } refused)
                return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, status = await StatusOf(editor, ui) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "viewport_screenshot")]
    [Description("Render what the viewport camera sees into a PNG file and return its path. The picture is drawn at the size asked for, independent of the window. Use it to look at a result rather than assume it.")]
    public static async Task<string> Screenshot(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Full path of the .png to write.")] string path,
        [Description("Width in pixels. Default 1280.")] int width = 1280,
        [Description("Height in pixels. Default 800.")] int height = 800)
    {
        try
        {
            if (width is < 64 or > 4096 || height is < 64 or > 4096) return ToolResult.Invalid("width and height must be 64…4096");
            if (await ui.RunAsync(() => editor.Screenshot(path, width, height)) is { } refused) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, path, width, height });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "object_move")]
    [Description("Move one object (undoable): to a world position, by a world offset, or both (position first). Works for frames and collision placements. A transform edit never rebuilds a mesh, so it is safe on stock objects.")]
    public static async Task<string> Move(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Object name, or a path suffix when the name is ambiguous.")] string name,
        [Description("New world position [x, y, z]. Omit to keep the position.")] float[]? position = null,
        [Description("World offset [dx, dy, dz] to add. Omit for none.")] float[]? offset = null)
    {
        try
        {
            if (position == null && offset == null) return ToolResult.Invalid("give a position, an offset, or both");
            if ((position != null && position.Length != 3) || (offset != null && offset.Length != 3))
                return ToolResult.Invalid("position and offset take three numbers each");
            if (await ui.RunAsync(() => editor.Move(name, position, offset)) is { } refused) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, status = await StatusOf(editor, ui) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "scene_delete_selected")]
    [Description("Delete the selected objects (undoable with editor_undo).")]
    public static async Task<string> DeleteSelected(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            int deleted = 0;
            string? refused = await ui.RunAsync(() => editor.DeleteSelected(out deleted));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, deleted });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "scene_duplicate_selected")]
    [Description("Duplicate the selected objects in place (undoable) and leave the copies selected: a static mesh gets its own deep copy, a collision placement another placement, an actor another record that shares its behaviour row. Returns the copies' names — move them with object_move.")]
    public static async Task<string> DuplicateSelected(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            IReadOnlyList<string> copies = [];
            string? refused = await ui.RunAsync(() => editor.DuplicateSelected(out copies));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, copies });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "actor_import")]
    [Description("Copy an actor out of ANOTHER archive's actor pack into the loaded area, with its own copy of its behaviour row — how a district with no lights is given one from a stock interior (a LightEntity). With the resource editor as the target (editor_target) the copy goes into the archive open there instead — how an interior under shops\\ gets its lights. Only actors that place no object of their own scene travel this way: lights, sounds — for one that places an object (a door, a prop) use object_import. Undoable. Find candidates with decode_actors on the source .act file.")]
    public static async Task<string> ImportActor(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Full path of the source Actors_*.act file, in an extracted archive.")] string sourceActFile,
        [Description("Entity name of the actor to copy, as decode_actors lists it.")] string actorName,
        [Description("Name for the copy; must be new in the loaded area's pack.")] string newName,
        [Description("World position [x, y, z] to put it at.")] float[] position)
    {
        try
        {
            if (position.Length != 3) return ToolResult.Invalid("position takes three numbers");
            if (await ui.RunAsync(() => editor.ImportActor(sourceActFile, actorName, newName, position)) is { } refused)
                return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, name = newName, status = await ui.RunAsync(editor.Status) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "object_import")]
    [Description("Copy an object out of ANOTHER archive into the loaded area — or, with the resource editor as the target (editor_target), into the archive open there, which is how an interior under shops\\ is furnished: a door from a shop, a prop or a piece of furniture from an interior. 'name' is looked up first among the source archive's actors (entity name, as decode_actors lists it) — then the actor comes too, with the object it places, its behaviour row, its prefab entry and the item descriptions its collision hulls name — and otherwise among its scene's frame objects (as decode_frame_resource lists them), which arrive as plain scenery anchored to the district's scene, with the source's collision hulls that stand inside their footprint — or, when they had none, a hull cooked from their triangles. Geometry is copied into the area's own buffer pools and the textures its materials name into its working copy, so the object does not depend on the source archive being loaded. Undoable: undone and saved, the textures it brought are set aside (and come back if it is redone), while the item descriptions and the prefab entry stay in the working copy, unused. An import that is refused or fails leaves nothing — neither in the scene nor in the working copy. Skinned models cannot travel yet.")]
    public static async Task<string> ImportObject(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The source archive: a full path to the .sds, or one relative to the game's sds folder, e.g. 'shops/harry.sds'.")] string sourceArchive,
        [Description("Entity name of an actor in the source archive, or the name of a frame object in its scene.")] string name,
        [Description("Name for the copy; must be new in the loaded area (it names both the object and, for an actor, the actor).")] string newName,
        [Description("World position [x, y, z] to put it at: for an actor the point it places its object at (stock props stand on it); for scenery the point the middle of its base lands on.")] float[] position,
        [Description("Heading in degrees about the vertical axis. It replaces the heading the original has in its source archive and keeps its tilt, so what stands upright there stands upright here whichever way its mesh lies in its own space (a chair modelled on its back and stood up by its frame's matrix stays on its legs). 0 is the object as it stands there with its own turn about the vertical taken out — for one that is upright in its own space, no rotation at all. For scenery the middle of the bottom of the object as it stands lands on 'position'. Omit to keep the heading the original has as well.")] float? yawDegrees = null,
        [Description("Collision for scenery: 'auto' (default — the hulls that stand inside its box in the source archive, taken as its own, else its convex hull; for a shelf or a room that includes the hulls of what stood on or in it), 'convex' (a few dozen triangles shrink-wrapping it), 'box', 'mesh' (every render triangle) or 'none'. An actor's object always brings its own.")] string? collision = null,
        [Description("Which of the things named so in the source, counting from 1 — names repeat (87 bottles called 'lahev' in one bar). Actors of that name come first, then frame objects, the ones that draw before helpers. The result says how many there are (NamedSo). Default 1.")] int occurrence = 1,
        [Description("Scenery only: a frame of the receiving archive to hang the copy under (name or path suffix) instead of standing it in the scene by itself. An interior's pieces are children of its holder — the '…_translocator_00' frame, which carries them to every place the interior stands at — so furniture for an interior takes the holder here. The copy still lands on 'position' in the archive's own world, and it is not put on the spawn list. Omit for a district.")] string? parent = null)
    {
        try
        {
            if (position is not { Length: 3 }) return ToolResult.Invalid("position takes three numbers");
            ObjectImportOutcome? outcome = null;
            string? refused = await ui.RunAsync(
                () => editor.ImportObject(sourceArchive, name, newName, position, yawDegrees, collision, occurrence, parent, out outcome));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, imported = outcome, status = await StatusOf(editor, ui) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "object_properties")]
    [Description("The property panel of one object as a flat list: group, id, label, kind, whether it is read-only, and the value as text. For an actor this includes its behaviour fields — a light's colour, range and intensity.")]
    public static async Task<string> Properties(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Object name, or a path suffix when the name is ambiguous.")] string name)
    {
        try
        {
            IReadOnlyList<ObjectProperty> properties = await ui.RunAsync(() => editor.Properties(name));
            return ToolResult.Json(new { success = true, count = properties.Count, properties });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "object_set_property")]
    [Description("Set one property of an object (undoable). Name the property by its id from object_properties (or its label). The value is text: a number, true/false, 'x, y, z' for a vector, 0x… for a hash. A behaviour row can be shared by several actors — the group title says how many — and then the change is theirs too.")]
    public static async Task<string> SetProperty(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("Object name, or a path suffix when the name is ambiguous.")] string name,
        [Description("Property id (e.g. 'Behaviour.24') or label.")] string property,
        [Description("New value, as text.")] string value)
    {
        try
        {
            if (await ui.RunAsync(() => editor.SetProperty(name, property, value)) is { } refused) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "editor_undo")]
    [Description("Undo the last edit in the editor's history. A Blender push is one history entry.")]
    public static async Task<string> Undo(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            if (await ui.RunAsync(editor.Undo) is { } refused) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, status = await StatusOf(editor, ui) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "editor_redo")]
    [Description("Redo the edit that editor_undo took back.")]
    public static async Task<string> Redo(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            if (await ui.RunAsync(editor.Redo) is { } refused) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, status = await StatusOf(editor, ui) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "zones_at")]
    [Description("Which LOAD ZONES hold a world point, and which districts the game is asked to keep loaded there. The city is tiled with AREA volumes (in city_univers.sds); while the camera is inside one, the districts its entry in cityareas.bin names stay loaded. A point that lies in no volume asks for nothing - a player teleported there stands in a district that does not stream in. Each zone is tested by its real volume (its planes), not only its box. 'near' also lists the zones that miss the point by up to that many metres, with the distance - which shows the edges of a gap.")]
    public static async Task<string> ZonesAt(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("World position [x, y, z].")] float[] point,
        [Description("Also list zones that miss the point by up to this many metres. Default 0: only the zones that hold it.")] float near = 0,
        [Description("Which copy of city_univers.sds to read: 'base' (default) or a DLC's folder name, e.g. 'cnt_joes_adventures'. A DLC can ship a copy of its own - a different scene with fewer zones; free ride (a multiplayer client that mounts Joe's Adventures included) was measured to use the BASE copy. The result lists the copies there are.")] string? copy = null)
    {
        try
        {
            IReadOnlyList<LoadZoneInfo> zones = [];
            IReadOnlyList<string> districts = [];
            IReadOnlyList<string> copies = [];
            string? refused = await ui.RunAsync(() => editor.ZonesAt(point, near, copy, out zones, out districts, out copies));
            return refused != null
                ? ToolResult.Invalid(refused)
                : ToolResult.Json(new
                {
                    success = true,
                    copy = string.IsNullOrWhiteSpace(copy) ? "base" : copy,
                    copies,
                    districtsAskedFor = districts,
                    inNoZone = !zones.Any(z => z.Inside),
                    zones,
                });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "zones_map")]
    [Description("A plan of where a district is asked for by the load zones, as rows of text, north up and x running left to right: '#' a zone holding the point names the district, '+' zones hold the point but none names it, '.' no zone holds the point. Use it to see a gap in the zones before sending players somewhere the stock game never let them stand, and to check a zone_move_face.")]
    public static async Task<string> ZonesMap(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("District archive name, e.g. 'greenfield'.")] string district,
        [Description("One corner of the plan [x, y].")] float[] from,
        [Description("The opposite corner [x, y].")] float[] to,
        [Description("Step between samples, metres. Default 20.")] float step = 20,
        [Description("Height the samples are taken at. Default 0.")] float z = 0,
        [Description("Which copy of city_univers.sds: 'base' (default) or a DLC's folder name. See zones_at.")] string? copy = null)
    {
        try
        {
            IReadOnlyList<string> rows = [];
            string? refused = await ui.RunAsync(() => editor.ZonesMap(district, from, to, step, z, copy, out rows));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, district, step, z, rows });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "zone_move_face")]
    [Description("Move one face of a load zone to a world coordinate, to close a gap in the zones or pull a zone back: the plane that bounds the volume on that side and its box are changed together. Only for a volume that stands square to the map and a face square to the axis. By default it only REPORTS what would change; apply=true writes the scene of city_univers.sds to its working copy - then editor_build or archive_build packs that archive (with a backup) and the game sees it. With the map editor open a move in the base copy is one step of its history (editor_undo takes it back); a move in a DLC's copy, or in every copy at once, is not. city_univers.sds is shared by the whole city and by both seasons, so every client of a multiplayer server needs the same file. What loads a district where a player appears is a zone naming TWO districts (zones_at lists them); a zone naming one did not load it by itself when measured in the game. The base copy is the one edited unless 'copy' says otherwise.")]
    public static async Task<string> ZoneMoveFace(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The zone's name, as zones_at lists it, e.g. 'AREA0019_GREENFIELD'.")] string zone,
        [Description("Which face: '+x', '-x', '+y', '-y', '+z' or '-z' (the face on that side of the volume).")] string face,
        [Description("Where that face is to stand, on its axis, in world coordinates.")] float to,
        [Description("Write the change to the working copy. Default false: report only.")] bool apply = false,
        [Description("Which copy of city_univers.sds: 'base' (default), a DLC's folder name, or 'all' for every copy that has the zone.")] string? copy = null)
    {
        try
        {
            IReadOnlyList<LoadZoneMoveInfo> results = [];
            string? refused = await ui.RunAsync(() => editor.ZoneMoveFace(zone, face, to, apply, copy, out results));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, moved = results });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "zone_create")]
    [Description("Add a NEW load zone to city_univers.sds (the base copy): a box standing square to the map between two world corners that keeps one or two districts loaded while the player is inside it. It is made as a copy of an existing zone ('like' - its flags, parent and name-table membership) with a name, a place and a shape of its own, and gets a line in cityareas.bin. By default it only REPORTS; apply=true writes the scene, the frame name table and cityareas.bin to the working copy - then archive_build packs city_univers.sds (with a backup). With the map editor open the zone is one step of its history (editor_undo takes it out); zone_delete takes it out later. Refused while an editor holds city_univers (Whole map). Measured in the game: the NAME decides whether the zone loads its districts for a player who appears inside it - two words after the number ('AREA900_DOCK_SOUTH') and it does, one word ('AREA900_DOCK') and it does not, whatever districts it names; the result says which it is ('loadsOnArrival').")]
    public static async Task<string> ZoneCreate(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The new zone's name, e.g. 'AREA900_SANDISLAND_TUNEL' - two words after the number, or the game will not load the districts for a player who appears inside. No object of the scene may have it.")] string name,
        [Description("An existing zone to make it like, as zones_at lists it - one that stands square to the map.")] string like,
        [Description("One corner of the box, world [x, y, z].")] float[] boxMin,
        [Description("The opposite corner, world [x, y, z].")] float[] boxMax,
        [Description("The districts it keeps loaded: one or two archive names, e.g. ['sandisland', 'tunel'].")] string[] districts,
        [Description("Write the change to the working copy. Default false: report only.")] bool apply = false)
    {
        try
        {
            LoadZoneInfo? made = null;
            string? refused = await ui.RunAsync(() => editor.ZoneCreate(name, like, boxMin, boxMax, districts, apply, out made));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, applied = apply, zone = made, loadsOnArrival = made?.LoadsOnArrival });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "zone_delete")]
    [Description("Take a load zone that was ADDED (zone_create, or the editor's New loading zone button - zones_at marks such a zone 'Added') out of city_univers.sds again: its volume, its place in the frame name table and its line in cityareas.bin. A zone the game ships with is refused. By default it only REPORTS; apply=true writes the working copy - then archive_build packs city_univers.sds. With the map editor open it is one step of its history (editor_undo puts the zone back). Refused while an editor holds city_univers (Whole map).")]
    public static async Task<string> ZoneDelete(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The zone's name, as zones_at lists it.")] string name,
        [Description("Write the change to the working copy. Default false: report only.")] bool apply = false)
    {
        try
        {
            LoadZoneInfo? gone = null;
            string? refused = await ui.RunAsync(() => editor.ZoneDelete(name, apply, out gone));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, applied = apply, zone = gone });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "shop_places")]
    [Description("The INTERIORS the game stands in the open city - gun shops, clothes shops, diners, bars, garages, flats - and the places they stand at. Such an interior is an archive under shops\\ that the game loads when the player comes near; one archive can stand at several places (the gun shop at eleven). Without a name: every interior of cityshops.bin with its archive, its actor file and its places as the table has them (marker name, map position). With 'shop': that interior's archive is read too, so each place comes with where its marker stands, how it is turned, the pair of volumes of city_univers that load it there ('LoadZone') and let it go ('UnloadZone'), and whether it was added here ('Added'). A place is added with shop_place_add.")]
    public static async Task<string> ShopPlaces(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("An interior's name as the table has it ('Gunshop') or its archive's ('gunshop'). Omit for the list of all.")] string? shop = null)
    {
        try
        {
            IReadOnlyList<ShopInfo> shops = [];
            string? refused = await ui.RunAsync(() => editor.ShopPlaces(shop, out shops));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, count = shops.Count, shops });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "shop_place_add")]
    [Description("Stand an interior of shops\\ at ONE MORE PLACE of the city - the way the game itself stands one gun shop at eleven. Three things are written together: a marker frame in the interior's archive at 'point', a pair of box volumes round it in city_univers.sds (inside the smaller the interior is loaded, outside the wider it is let go), and the rows of cityshops.bin. 'point' is where the interior's OWN ORIGIN goes - see with shop_places how an existing marker stands against its room (the gun shop's is 2.08 m above its floor, in the middle of the room). By default it only REPORTS; apply=true writes the working copies - then archive_build BOTH archives named in the answer. Refused while either archive is open in an editor. Seen in the game (a multiplayer client): a twelfth gun shop added this way loads and its shop menu opens; an interior has no outside, so it is see-through from the street unless it stands inside a building. 'turn' follows the game's own markers, which are turned; a turned place added here has not been looked at in the game yet. Take a place out again with shop_place_delete.")]
    public static async Task<string> ShopPlaceAdd(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The interior, as shop_places lists it: 'Gunshop', 'Odevy', 'vitohouseb12'...")] string shop,
        [Description("Where the interior's own origin goes, world [x, y, z].")] float[] point,
        [Description("Turn about the vertical, degrees, counter-clockwise seen from above. Default 0: as the interior was built.")] float turn = 0,
        [Description("Half the side of the box inside which the interior is loaded, metres. Default 35.")] float loadHalf = 35,
        [Description("Half the side of the wider box outside which it is let go. Default 60.")] float unloadHalf = 60,
        [Description("Half the height of both boxes. Default 23.")] float halfHeight = 23,
        [Description("Write the change to the working copies. Default false: report only.")] bool apply = false)
    {
        try
        {
            ShopPlaceInfo? place = null;
            IReadOnlyList<string> archives = [];
            string? refused = await ui.RunAsync(() => editor.ShopPlaceAdd(shop, point, turn, loadHalf, unloadHalf, halfHeight, apply, out place, out archives));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, applied = apply, place, archivesToBuild = archives });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "shop_place_delete")]
    [Description("Take a place of an interior that was ADDED (shop_place_add - shop_places marks it 'Added') out again: its marker in the interior's archive, its pair of volumes in city_univers.sds and its rows in cityshops.bin. A place the game ships with is refused. By default it only REPORTS; apply=true writes the working copies - then archive_build BOTH archives named in the answer. Refused while either archive is open in an editor.")]
    public static async Task<string> ShopPlaceDelete(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The place's marker, as shop_places lists it, e.g. 'GUNSHOP_translocator_14'.")] string marker,
        [Description("Write the change to the working copies. Default false: report only.")] bool apply = false)
    {
        try
        {
            ShopPlaceInfo? place = null;
            IReadOnlyList<string> archives = [];
            string? refused = await ui.RunAsync(() => editor.ShopPlaceDelete(marker, apply, out place, out archives));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, applied = apply, place, archivesToBuild = archives });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "shop_create")]
    [Description("Make an INTERIOR OF YOUR OWN the way the game keeps its shops and flats: a copy of one of the table's interiors under a new name, standing at 'point'. The copy is an archive of its own - shops\\<name>.sds, made from the working copy of 'like', with its marker frames named after the new interior - and gets a row of its own in cityshops.bin, a marker for its first place and a pair of box volumes round it in city_univers.sds. What stands inside is then changed like any other archive (open it, delete, import, push from Blender); more places are added with shop_place_add. Pick a small interior to copy - 'elgreco' is one room with one sector. By default it only REPORTS; apply=true writes the working copies and packs the NEW archive (no file of the game is overwritten) - then archive_build city_univers.sds, named in the answer. Refused while city_univers is open in an editor. Take it out of the table again with shop_delete.")]
    public static async Task<string> ShopCreate(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The new interior's name: 3 to 31 lower-case letters, digits and '_', starting with a letter. It is its archive's name too.")] string name,
        [Description("The interior to copy, as shop_places lists it, e.g. 'elgreco'.")] string like,
        [Description("Where the interior's own origin goes, world [x, y, z] - where the copied interior's marker stood against its room.")] float[] point,
        [Description("Turn about the vertical, degrees, counter-clockwise seen from above. Default 0.")] float turn = 0,
        [Description("Half the side of the box inside which the interior is loaded, metres. Default 35.")] float loadHalf = 35,
        [Description("Half the side of the wider box outside which it is let go. Default 60.")] float unloadHalf = 60,
        [Description("Half the height of both boxes. Default 23.")] float halfHeight = 23,
        [Description("Write the change. Default false: report only.")] bool apply = false)
    {
        try
        {
            ShopPlaceInfo? place = null;
            IReadOnlyList<string> archives = [];
            IReadOnlyList<string> notes = [];
            string? refused = await ui.RunAsync(() => editor.ShopCreate(name, like, point, turn, loadHalf, unloadHalf, halfHeight, apply, out place, out archives, out notes));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, applied = apply, place, archivesToBuild = archives, notes });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "shop_delete")]
    [Description("Take an interior that was ADDED (shop_create) out of cityshops.bin again: its row, its area rows and their volumes in city_univers.sds - after which the game never asks for it. Its archive under shops\\ and its working copy are left where they are. An interior the game ships with is refused. By default it only REPORTS; apply=true writes the working copy - then archive_build city_univers.sds. Refused while city_univers is open in an editor.")]
    public static async Task<string> ShopDelete(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The interior, as shop_places lists it.")] string name,
        [Description("Write the change to the working copy. Default false: report only.")] bool apply = false)
    {
        try
        {
            ShopInfo? shop = null;
            IReadOnlyList<string> archives = [];
            string? refused = await ui.RunAsync(() => editor.ShopDelete(name, apply, out shop, out archives));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, applied = apply, shop, archivesToBuild = archives });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "archive_materials")]
    [Description("What an archive needs REGISTERED to draw as it was made: the materials its meshes are drawn with, each in full, and which of them the game does not have. A mesh names its materials by hash; the definitions are not in the archive but in the game's material libraries (edit\\materials\\*.mtl) - so an archive handed to someone else (a multiplayer server, another modder) is missing every material that was added or changed here. The archive FILE is read as it stands in pc\\sds (build it first) and the libraries from disk. Each material comes with its 'origin' against the game as it ships - 'added', 'changed' (the game's own, defined differently here), 'shipped', or 'unknown' when there is no list for this edition (the list covers Mafia II, library version 57; not the Definitive Edition) - and with name, hash, flags, shader id and hash, the library's nameless fields, every sampler (texture, whether that texture is INSIDE the archive, sampler states) and every parameter. By default the document holds only the materials that have to travel with the archive; all=true lists every one. 'missing' are hashes the archive names and no library has - those parts draw with no material. 64-bit hashes are hex text, safe for a JavaScript reader. saveTo also writes the document to a file to send along. Nothing of the game is written.")]
    public static async Task<string> ArchiveMaterials(
        IEditorSession editor,
        IUiThreadMarshal ui,
        [Description("The archive: a full path to an .sds, or a path under pc\\sds such as 'cars/shubert_38_custom.sds' or 'city/southport'.")] string archive,
        [Description("List every material the archive uses, the game's own too. Default false: only added and changed ones.")] bool all = false,
        [Description("Full path of a .json file to also write the document to. Default: none.")] string? saveTo = null,
        [Description("Full path of an .mtl file to also write the added and changed materials to, as a material library of their own - the file a multiplayer host loads beside the game's libraries (Mafia II Online: stream/materials/<name>.mtl, names of a-z 0-9 _ only and not default*). An archive that adds nothing writes no file (LibraryMaterials 0). Never inside the game's edit\\materials. Default: none.")] string? libraryTo = null)
    {
        try
        {
            ArchiveMaterialsInfo? result = null;
            string? refused = await ui.RunAsync(() => editor.ArchiveMaterials(archive, all, saveTo, libraryTo, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }
}
