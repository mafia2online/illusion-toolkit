namespace Illusion.Mcp;

/// <summary>
/// The running map editor, as a client may drive it: what is open, what is selected, and the handful of
/// actions that otherwise take a hand on the mouse — load an area, select, send the selection to Blender,
/// save, build, look around.
/// <para>
/// A seam for the same reason <see cref="IGameEnvironment"/> is one: the editor window, the viewport and
/// the scene tree live in the executable, which this project may not reference. The application registers
/// an implementation through <see cref="McpHostOptions.ConfigureServices"/>.
/// </para>
/// <b>Every member must be called on the UI thread</b> — the tools route through
/// <see cref="IUiThreadMarshal"/>. Members that can refuse return the reason as a string and null on
/// success, so a tool can hand the sentence straight to the client.
/// </summary>
public interface IEditorSession
{
    EditorStatus Status();

    /// <summary>Names of the areas the editor can load. Empty until the editor is open and its catalogs
    /// are ready.</summary>
    IReadOnlyList<string> Areas();

    /// <summary>Opens the map editor from the launcher when it is not open yet. Null on success.</summary>
    string? EnsureEditor();

    /// <summary>Starts loading an area (a no-op when it is the one already shown). Null on success. A load
    /// that would replace a scene with unsaved edits is refused unless <paramref name="discardUnsavedEdits"/>
    /// says they may be lost.</summary>
    string? LoadArea(string area, bool winter, bool discardUnsavedEdits);

    /// <summary>Scene-tree rows by name fragment, kind and/or a world-space box their bounds or position
    /// must touch.</summary>
    IReadOnlyList<SceneObjectInfo> Find(string? nameContains, string? kind, float[]? boxMin, float[]? boxMax, int limit);

    /// <summary>Replaces the selection; no names clears it. A name is an object name, or a path suffix
    /// when the name alone is ambiguous. Null on success.</summary>
    string? Select(IReadOnlyList<string> names);

    /// <summary>Sends the selection to Blender (what Tab does). Null when the request was started.</summary>
    string? OpenInBlender();

    /// <summary>Asks Blender to push what it holds now. Null when the request was sent; the outcome
    /// arrives as a notice.</summary>
    string? RequestBlenderPush();

    /// <summary>Leaves the Blender edit session; everything pushed so far stays in the scene.</summary>
    string? EndBlenderSession();

    /// <summary>The most recent notices, oldest first.</summary>
    IReadOnlyList<EditorNotice> Notices(int last);

    /// <summary>Writes every unsaved edit to the working copies. Null when the save ran — which is not the
    /// same as everything being written: <paramref name="notSaved"/> names each thing the save had to leave
    /// unsaved (a material library that would not write, a working copy that was refused), and is empty only
    /// when the save is complete.</summary>
    string? Save(out int filesWritten, out IReadOnlyList<string> notSaved);

    /// <summary>Saves, then packs every archive with edits, keeping a backup of each. When the save does not
    /// complete nothing is packed, and <see cref="BuildOutcome.NotSaved"/> says what stood in the way.</summary>
    BuildOutcome Build();

    /// <summary>Saves, then carries the loaded district's edits into its winter archive's working copy and
    /// queues that archive for a Build. Null on success.</summary>
    string? MirrorToWinter(out SeasonMirrorOutcome? outcome);

    CameraInfo Camera();

    /// <summary>Looks at <paramref name="target"/> from the direction <paramref name="fromAxis"/> points
    /// along (camera sits on that side), framing a sphere of <paramref name="radius"/>.</summary>
    string? LookAt(float[] target, float radius, float[]? fromAxis);

    /// <summary>Stands the camera at <paramref name="position"/> looking at <paramref name="target"/> —
    /// the way to look around INSIDE a room, where framing a sphere would back out through the wall.</summary>
    string? SetCamera(float[] position, float[] target);

    /// <summary>Frames the selection. False when there is nothing to frame.</summary>
    bool FrameSelection();

    /// <summary>Switches the shading mode and the overlay layers; a null argument leaves that one alone.</summary>
    string? SetView(string? renderMode, bool? collision, bool? crash, bool? zones, bool? navigation,
        bool discardUnsavedEdits = false);

    /// <summary>Renders the viewport's current view into a PNG. Null on success.</summary>
    string? Screenshot(string path, int width, int height);

    /// <summary>Moves one object (undoable): to a world position, and/or by a world offset.</summary>
    string? Move(string name, float[]? position, float[]? offset);

    /// <summary>Copies an actor out of another archive's pack (<paramref name="sourceActFile"/>) into the
    /// loaded area, under <paramref name="newName"/>, at a world position. Undoable. Null on success.</summary>
    string? ImportActor(string sourceActFile, string actorName, string newName, float[] position);

    /// <summary>
    /// Copies an object out of another archive into the loaded area — its frames, geometry, textures and
    /// collision descriptions — under <paramref name="newName"/>, at a world position. <paramref name="name"/>
    /// is an actor of the source archive (then the actor comes too, with the object it places and its prefab
    /// entry) or, failing that, a frame object of its scene (then it arrives as plain scenery). Undoable.
    /// Null on success.
    /// </summary>
    /// <param name="sourceArchive">The source .sds: a full path, or one relative to the game's sds folder.</param>
    /// <param name="yawDegrees">Heading about the vertical axis, replacing the original's rotation; null keeps
    /// the rotation the original has.</param>
    /// <param name="collision">For scenery: auto (its own hulls, else its convex hull), convex, box, mesh or none.</param>
    string? ImportObject(string sourceArchive, string name, string newName, float[] position, float? yawDegrees,
        string? collision, int occurrence, out ObjectImportOutcome? outcome);

    /// <summary>Duplicates the selection (undoable) and leaves the copies selected. Null on success.</summary>
    string? DuplicateSelected(out IReadOnlyList<string> copies);

    /// <summary>The property panel of one object, flattened: every field with its id, kind and value.</summary>
    IReadOnlyList<ObjectProperty> Properties(string name);

    /// <summary>Sets one property by id (undoable). The value is text, parsed by the property's kind.</summary>
    string? SetProperty(string name, string propertyId, string value);

    /// <summary>Deletes the selection (undoable). Null on success.</summary>
    string? DeleteSelected(out int deleted);

    /// <summary>Points the scene tools at the map editor (<c>map</c>) or the resource editor (<c>resource</c>).
    /// Null on success; a note when the chosen editor is not open yet.</summary>
    string? SetTarget(string target);

    /// <summary>The resource editor's state, and which editor the scene tools drive.</summary>
    ResourceStatus ResourceStatus();

    /// <summary>Opens the resource editor (if it is not open) on an archive — a path, a path under <c>pc\sds</c>
    /// such as <c>cars/shubert_38.sds</c>, or a bare name — and makes it the target. Null when the load started.</summary>
    string? OpenResource(string archive, out string? archivePath);

    /// <summary>Archives of the game's library, by name fragment and/or folder fragment.</summary>
    IReadOnlyList<LibraryItem> Library(string? query, string? folder, int limit);

    /// <summary>The tuning tables of the archive on the resource editor's stage, and the fields of one of them
    /// (1-based) whose name or label contains <paramref name="query"/>.</summary>
    string? Tuning(int table, string? query, int limit, out IReadOnlyList<TuningTableInfo> tables,
        out IReadOnlyList<TuningFieldInfo> fields);

    /// <summary>Sets one tuning field (undoable; written to the working copy at once, packed by Build). The field
    /// is its name; <paramref name="band"/> and <paramref name="element"/> narrow it when the name repeats (every
    /// wheel has the same fields). Null on success.</summary>
    string? SetTuning(int table, string field, string? band, string? element, string value, out TuningFieldInfo? result);

    /// <summary>Clones a car under a new model name and registers it (vehicle table, paint, cover points and,
    /// with <paramref name="traffic"/>, the traffic rows of the source car), then builds the archives. Null on
    /// success.</summary>
    string? CloneCar(string source, string name, bool traffic, string? title, out CarCloneInfo? result);

    /// <summary>Packs one archive's working copy back into its .sds, keeping a backup of what it replaces — for an
    /// edit made in the working copy itself (a script, a table) that no editor session tracks. With
    /// <paramref name="memoryFrom"/>, the working copy first takes the memory requirements that archive states
    /// (a clone made before they were kept takes them from the car it was cloned from). Null on success.</summary>
    string? BuildArchive(string archive, string? memoryFrom, bool dropMissing, out PackedArchive? result);

    /// <summary>Builds one car under another car's name, replacing that car's archive (backup kept) and touching
    /// no table. Null on success.</summary>
    string? SubstituteCar(string source, string target, out CarSubstituteInfo? result);

    /// <summary>Exports a built car as a multiplayer resource folder (package.json, sds/cars/, vehicles.json);
    /// nothing of the game is written. Null on success.</summary>
    string? ExportCarForM2o(string car, string? output, string? resource, out M2oExportInfo? result);

    /// <summary>Finds — and with <paramref name="apply"/> hides, as one undoable edit — the triangles of a mesh
    /// whose corners all lie inside a world-space box, without rebuilding the mesh. Null on success.</summary>
    string? HideTriangles(string name, float[] boxMin, float[] boxMax, string? material, bool apply, int sample,
        out HiddenTrianglesInfo? result);

    /// <summary>Counts — and with <paramref name="apply"/> removes, as one undoable edit — the hulls no placement
    /// references, in every collision file of the open scene. Null on success.</summary>
    string? UnusedHulls(bool apply, out IReadOnlyList<UnusedHullsInfo> result);

    /// <summary>Lists — and with <paramref name="delete"/> removes, as one undoable edit and in both seasons where
    /// the placement is linked — the city_crash props standing inside a world-space box. <paramref name="total"/>
    /// is how many the box holds; the list stops at <paramref name="limit"/> — except for a delete, which lists
    /// every placement it removed. A delete of more than <paramref name="maxDelete"/> placements is refused
    /// whole. Null on success.</summary>
    string? CrashPlacements(float[] boxMin, float[] boxMax, string? nameContains, bool delete, int limit, int maxDelete,
        out IReadOnlyList<CrashPlacementInfo> result, out int total);

    string? Undo();

    string? Redo();
}
