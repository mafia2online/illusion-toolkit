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
    string RenderMode,
    // Which editor the scene tools drive right now: "map" or "resource". A tool that only works on one of
    // them switches to it, and this is where that shows.
    string Target = "map");

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
/// <paramref name="Dropped"/> are manifest entries that named a file missing from the working copy: they are
/// not in the archive, and no longer in the manifest.
public sealed record PackedArchive(string Archive, string? Backup, IReadOnlyList<string>? Dropped = null);

/// <summary>One triangle of a mesh, in world space, with the material of its slot and its level of detail.</summary>
public sealed record TriangleInfo(int Lod, string Material, float[] A, float[] B, float[] C);

/// <summary>What a box takes out of a mesh: how many triangles on each level of detail, whether they were
/// hidden or only found, and the first of them.</summary>
public sealed record HiddenTrianglesInfo(int Triangles, IReadOnlyList<int> PerLod, bool Applied, IReadOnlyList<TriangleInfo> Sample);

/// <summary>One material slot of a mesh: its index, the material it draws with (null when no loaded library
/// knows the hash), how many triangles it covers, and whether this call re-pointed it.</summary>
public sealed record MeshSlotInfo(int Slot, string? Material, int Triangles, bool Changed);

/// <summary>One collision file of the open scene: its placements, the hulls it carried, how many of those no
/// placement referenced, and whether they were removed or only counted.</summary>
public sealed record UnusedHullsInfo(string Layer, int Placements, int Hulls, int Unused, bool Removed);

/// <summary>One placement of a city_crash prop (a tree, a lamp, a bin): the prop's name, the placement's id in the
/// table, where it stands, whether the other season holds the same placement, and whether the two are kept in
/// step — an edit of a linked placement is made to its twin, and deleting it deletes both; one the user
/// unlinked ("In all seasons" off) loses only the season on screen.</summary>
public sealed record CrashPlacementInfo(string Prop, int Id, float[] Position, bool BothSeasons, bool SeasonLinked);

/// <summary>One car built under another's name: the model name the replaced archives are keyed by, each archive
/// with the backup of what it held, and what was left as it was.</summary>
public sealed record CarSubstituteInfo(string Model, IReadOnlyList<PackedArchive> Packed, IReadOnlyList<string> Notes);

/// <summary>A car exported as a multiplayer resource: the folder and the resource's name, the model, its title
/// and the car it was cloned from, how many cars the folder holds now, the materials written into its library,
/// the files written and what the one shipping it should know.</summary>
public sealed record M2oExportInfo(string Folder, string Resource, string Model, string? Title, string? BasedOn,
    int Vehicles, IReadOnlyList<string> Materials, IReadOnlyList<string> Files, IReadOnlyList<string> Notes);

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
/// archive that failed with the reason. <paramref name="NotSaved"/> is what the save a build starts with
/// could not write; when it is not empty the build stopped there and packed nothing.</summary>
/// <paramref name="Dropped"/> are manifest entries ("archive: file") that named a file missing from the
/// working copy and were left out of the archive packed.
public sealed record BuildOutcome(
    IReadOnlyList<(string Archive, string? Backup)> Packed,
    IReadOnlyList<(string Archive, string Error)> Failed,
    IReadOnlyList<string> NotSaved,
    IReadOnlyList<string>? Dropped = null);

/// <summary>What mirroring a district into its winter archive did: how many objects had every material
/// settled (winter's own where the season changes it), how many objects winter gained and lost, how many
/// material slots were re-pointed in summer and carried over as they are, how many objects could not be told
/// from a namesake and kept summer's materials, and the files and textures written into the winter working
/// copy.</summary>
public sealed record SeasonMirrorOutcome(
    string WinterArchive, int Matched, int Added, int Dropped, int Reassigned, int Ambiguous,
    IReadOnlyList<string> Files, IReadOnlyList<string> Textures);

/// <summary>What bringing an object in from another archive did: what it came as ("actor" — an actor and the
/// object it places, "scenery" — a plain object anchored to the scene), how many frames and meshes were
/// copied, and what was carried into the working copy beside the scene — textures, item descriptions, a
/// prefab entry. <paramref name="TexturesElsewhere"/> are textures neither archive carries: they live in an
/// archive the game loads beside the source, and may not be loaded where the object now stands.</summary>
/// <paramref name="NamedSo"/> is how many things in the source answer to the name asked for, and
/// <paramref name="Occurrence"/> which of them this was.
public sealed record ObjectImportOutcome(
    string Kind, string Name, int Frames, int Meshes, IReadOnlyList<string> Textures, int ItemDescriptions,
    bool Prefab, IReadOnlyList<string> TexturesElsewhere, int UnresolvedCollisions, string Collision,
    int NamedSo = 1, int Occurrence = 1);

/// <summary>Where the viewport camera is. Yaw and pitch are in radians, as the camera keeps them.</summary>
public sealed record CameraInfo(float[] Position, float Yaw, float Pitch, float OrbitDistance);
