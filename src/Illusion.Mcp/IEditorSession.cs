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

    /// <summary>Starts loading an area (a no-op when it is the one already shown). Null on success.</summary>
    string? LoadArea(string area, bool winter);

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

    /// <summary>Writes every unsaved edit to the working copies. Null on success.</summary>
    string? Save(out int filesWritten);

    /// <summary>Saves, then packs every archive with edits, keeping a backup of each.</summary>
    BuildOutcome Build();

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
    string? SetView(string? renderMode, bool? collision, bool? crash, bool? zones, bool? navigation);

    /// <summary>Renders the viewport's current view into a PNG. Null on success.</summary>
    string? Screenshot(string path, int width, int height);

    /// <summary>Moves one object (undoable): to a world position, and/or by a world offset.</summary>
    string? Move(string name, float[]? position, float[]? offset);

    /// <summary>Copies an actor out of another archive's pack (<paramref name="sourceActFile"/>) into the
    /// loaded area, under <paramref name="newName"/>, at a world position. Undoable. Null on success.</summary>
    string? ImportActor(string sourceActFile, string actorName, string newName, float[] position);

    /// <summary>Duplicates the selection (undoable) and leaves the copies selected. Null on success.</summary>
    string? DuplicateSelected(out IReadOnlyList<string> copies);

    /// <summary>The property panel of one object, flattened: every field with its id, kind and value.</summary>
    IReadOnlyList<ObjectProperty> Properties(string name);

    /// <summary>Sets one property by id (undoable). The value is text, parsed by the property's kind.</summary>
    string? SetProperty(string name, string propertyId, string value);

    /// <summary>Deletes the selection (undoable). Null on success.</summary>
    string? DeleteSelected(out int deleted);

    string? Undo();

    string? Redo();
}
