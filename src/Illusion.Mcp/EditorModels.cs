namespace Illusion.Mcp;

/// <summary>What the editor is doing right now — the first thing a client asks before driving it.</summary>
public sealed record EditorStatus(
    bool EditorOpen,
    string? Area,
    bool Winter,
    bool Loading,
    int Meshes,
    IReadOnlyList<string> Selection,
    bool UnsavedEdits,
    IReadOnlyList<string> PendingBuild,
    int BlenderObjects,
    string RenderMode);

/// <summary>One row of the scene tree. <see cref="Path"/> runs from the root, which is what tells two
/// objects of the same name apart. Bounds are there only for a node that draws a mesh. The three
/// "in box" members are filled when the search was given a box: how many of the mesh's triangles reach
/// into it, and the extent of those triangles clipped to the box — a building's bounds contain every room
/// inside it, so only the triangles say whether a volume is really occupied. Vertices and Triangles are
/// the mesh's own size — a mesh over 65535 vertices is one the game cannot draw.</summary>
public sealed record SceneObjectInfo(
    string Name,
    string Kind,
    string Path,
    float[]? Position,
    float[]? BoundsMin,
    float[]? BoundsMax,
    bool Selected,
    int? TrianglesInBox = null,
    float[]? InBoxMin = null,
    float[]? InBoxMax = null,
    int? Vertices = null,
    int? Triangles = null);

/// <summary>One field of an object's property panel. <see cref="Id"/> is what a write names; a group
/// title says where the panel shows it. The value is text in the form a write takes back.</summary>
public sealed record ObjectProperty(string Group, string Id, string Label, string Kind, bool ReadOnly, string Value);

/// <summary>Something the editor said to the user — a Blender push result above all, which is the only
/// place the applied and the refused objects of a push are spelled out.</summary>
public sealed record EditorNotice(DateTime Time, bool Error, string Text);

/// <summary>What a Build wrote: each packed archive with the backup taken of what it replaced, and each
/// archive that failed with the reason.</summary>
public sealed record BuildOutcome(
    IReadOnlyList<(string Archive, string? Backup)> Packed,
    IReadOnlyList<(string Archive, string Error)> Failed);

/// <summary>What mirroring a district into its winter archive did: how many winter meshes kept their own
/// materials on the summer object of the same name, how many objects winter gained and lost, how many no
/// longer lined up slot for slot (they keep summer's materials), and the files and textures written into
/// the winter working copy.</summary>
public sealed record SeasonMirrorOutcome(
    string WinterArchive, int Matched, int Added, int Dropped, int Reshaped,
    IReadOnlyList<string> Files, IReadOnlyList<string> Textures);

/// <summary>What bringing an object in from another archive did: what it came as ("actor" — an actor and the
/// object it places, "scenery" — a plain object anchored to the scene), how many frames and meshes were
/// copied, and what was carried into the working copy beside the scene — textures, item descriptions, a
/// prefab entry. <paramref name="TexturesElsewhere"/> are textures neither archive carries: they live in an
/// archive the game loads beside the source, and may not be loaded where the object now stands.</summary>
public sealed record ObjectImportOutcome(
    string Kind, string Name, int Frames, int Meshes, IReadOnlyList<string> Textures, int ItemDescriptions,
    bool Prefab, IReadOnlyList<string> TexturesElsewhere, int UnresolvedCollisions);

/// <summary>Where the viewport camera is. Yaw and pitch are in radians, as the camera keeps them.</summary>
public sealed record CameraInfo(float[] Position, float Yaw, float Pitch, float OrbitDistance);
