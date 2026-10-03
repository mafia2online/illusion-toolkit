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

/// <summary>What the resource editor is doing: whether it is open, which editor the scene tools drive
/// (<c>map</c> or <c>resource</c>), and the archive on its stage.</summary>
public sealed record ResourceStatus(
    bool Open,
    string Target,
    string? Archive,
    string? ArchivePath,
    bool Loading,
    int Meshes,
    IReadOnlyList<string> Selection,
    bool UnsavedEdits,
    IReadOnlyList<string> PendingBuild,
    int BlenderObjects,
    string RenderMode);

/// <summary>One archive of the game's library: its name, its path under <c>pc\sds</c>, what kind of thing it
/// holds (Car, Character, CityCrash, …), its size, and whether it already has a working copy.</summary>
public sealed record LibraryItem(string Name, string Path, string Kind, long Size, bool Extracted);

/// <summary>A car cloned under a new name: the vehicle id the tables gave it, how many traffic rows pick it,
/// the id of the text holding its own title (null when it shares the source car's), each archive written with
/// the backup taken of it (null for a new one), and what was left out.</summary>
public sealed record CarCloneInfo(string Name, int VehicleId, int TrafficRows, int? TextId,
    IReadOnlyList<PackedArchive> Packed, IReadOnlyList<string> Notes);

/// <summary>One archive a car clone wrote, and the backup of what it replaced.</summary>
public sealed record PackedArchive(string Archive, string? Backup);

/// <summary>One entity-data table of a car — a car ships several (the stock one and its tuned variants); the
/// label names its mass and power, which is what tells them apart.</summary>
public sealed record TuningTableInfo(int Table, string Label, string Type, int Fields);

/// <summary>One named value of a car's tuning table: where it sits (band, element — a wheel, a gear), what it is
/// called, its kind (Number, Integer, Flag, Vector, Text) and its value as text.</summary>
public sealed record TuningFieldInfo(int Table, string Band, string? Element, string Label, string Name, string Kind, string Value);

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
    bool Prefab, IReadOnlyList<string> TexturesElsewhere, int UnresolvedCollisions, string Collision);

/// <summary>Where the viewport camera is. Yaw and pitch are in radians, as the camera keeps them.</summary>
public sealed record CameraInfo(float[] Position, float Yaw, float Pitch, float OrbitDistance);
