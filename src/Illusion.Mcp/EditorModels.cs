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

/// <summary>Where the viewport camera is. Yaw and pitch are in radians, as the camera keeps them.</summary>
public sealed record CameraInfo(float[] Position, float Yaw, float Pitch, float OrbitDistance);
