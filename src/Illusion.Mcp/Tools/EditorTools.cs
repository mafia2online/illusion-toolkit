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
    [Description("Find objects in the loaded scene by a fragment of their name, by kind, and/or by a world-space box. Returns name, kind, tree path, position and bounds. With a box, a mesh is returned only when its TRIANGLES reach into the box (not merely its bounds), with TrianglesInBox and the extent of those triangles clipped to the box — the way to check that a volume is free before building in it. Objects without a mesh match a box by their position. Crash-layer copies (kind CrashInstance: trees, lamps, bins) are searched from the placement table whether or not their rows are expanded in the tree; a copy matches a name by its own label or by its prop's name, and a box by its prop's triangles at the copy's placement. Use it also to learn exact names before scene_select.")]
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
            return ToolResult.Json(new { success = true, status = await ui.RunAsync(editor.Status) });
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
                EditorStatus status = await ui.RunAsync(editor.Status);
                var said = (await ui.RunAsync(() => editor.Notices(8))).Where(n => n.Time >= started).ToList();
                if (status.BlenderObjects > 0)
                    return ToolResult.Json(new { success = true, blenderObjects = status.BlenderObjects, notices = said });
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
            return ToolResult.Json(new { success = true, status = await ui.RunAsync(editor.Status) });
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
            EditorStatus status = await ui.RunAsync(editor.Status);
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
        [Description("Show AI navigation overlays.")] bool? navigation = null)
    {
        try
        {
            if (await ui.RunAsync(() => editor.SetView(renderMode, collision, crash, zones, navigation)) is { } refused)
                return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, status = await ui.RunAsync(editor.Status) });
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
            return ToolResult.Json(new { success = true, status = await ui.RunAsync(editor.Status) });
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
    [Description("Copy an actor out of ANOTHER archive's actor pack into the loaded area, with its own copy of its behaviour row — how a district with no lights is given one from a stock interior (a LightEntity). Only actors that place no object of their own scene can travel: lights, sounds. Undoable. Find candidates with decode_actors on the source .act file.")]
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
            return ToolResult.Json(new { success = true, status = await ui.RunAsync(editor.Status) });
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
            return ToolResult.Json(new { success = true, status = await ui.RunAsync(editor.Status) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }
}
