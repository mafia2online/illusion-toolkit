using Illusion.Formats.Archive;

namespace Illusion.Assets.Sds;

/// <summary>
/// What one resource inside an archive IS, as far as showing it goes. The engine's own type names
/// (<c>FrameResource</c>, <c>NAV_OBJ_DATA</c>, <c>PREFAB</c>) are what the manifest carries; this is the
/// smaller set the browser draws an icon for, since several types are one thing wearing two names.
/// </summary>
public enum SdsResourceKind
{
    /// <summary>A type this build has not been taught — drawn as a plain payload.</summary>
    Unknown,

    /// <summary>The scene graph: <c>FrameResource</c>.</summary>
    Mesh,

    /// <summary>Which frame objects the game may see: <c>FrameNameTable</c>.</summary>
    NameTable,

    /// <summary>Geometry pools: <c>IndexBufferPool</c>, <c>VertexBufferPool</c>.</summary>
    Buffer,

    /// <summary>A texture: <c>Texture</c>.</summary>
    Texture,

    /// <summary>The high-resolution tail of a texture, a resource of its own: <c>Mipmap</c>.</summary>
    Mipmap,

    /// <summary>A texture that plays: <c>Animated Texture</c>.</summary>
    AnimatedTexture,

    /// <summary>Effects and their actors: <c>Effects</c>, <c>FxActor</c>, <c>FxAnimSet</c>.</summary>
    Effect,

    /// <summary>Cooked world collision: <c>Collisions</c>.</summary>
    Collision,

    /// <summary>Physics-shape templates: <c>ItemDesc</c>.</summary>
    Shape,

    /// <summary>Gameplay entities: <c>Actors</c>.</summary>
    Actor,

    /// <summary>Per-entity-type tables: <c>EntityDataStorage</c>.</summary>
    EntityData,

    /// <summary>Assembly descriptions: <c>PREFAB</c>.</summary>
    Prefab,

    /// <summary>Mass placement: <c>Translokator</c>.</summary>
    Instances,

    /// <summary>Animation banks: <c>Animation2</c>.</summary>
    Animation,

    /// <summary>Scripted camera work: <c>Cutscene</c>.</summary>
    Cutscene,

    /// <summary>Sound banks and their tables: <c>Sound</c>, <c>SoundTable</c>.</summary>
    Sound,

    /// <summary>Spoken lines: <c>Speech</c>.</summary>
    Speech,

    /// <summary>Where sound is audible: <c>AudioSectors</c>.</summary>
    AudioSector,

    /// <summary>AI graphs: the <c>NAV_*</c> family.</summary>
    Navigation,

    /// <summary>Wildlife routes: <c>AnimalTrafficPaths</c>.</summary>
    TrafficPath,

    /// <summary>Gameplay scripts: <c>Script</c>.</summary>
    Script,

    /// <summary>Data tables: <c>Table</c>.</summary>
    Table,

    /// <summary>Loose XML: <c>XML</c>.</summary>
    Xml,

    /// <summary>An opaque payload: <c>MemFile</c>.</summary>
    Binary,
}

/// <summary>
/// The band of the contents pane a resource is filed under. An archive announces up to thirty types and a
/// header per type would be a list of headers; these are the questions someone actually opens an archive
/// asking — what does it look like, what does it hit, what lives in it, what does it sound like.
/// <para>The order of the members is the order the sections appear in, and is deliberate: what you see
/// first, then what the game feels, then what it runs on.</para>
/// </summary>
public enum SdsResourceSection
{
    Geometry,
    Textures,
    Effects,
    Collision,
    Entities,
    Animation,
    Audio,
    Navigation,
    Data,

    /// <summary>Anything unclassified — never empty on purpose, and a sign this table needs a line.</summary>
    Other,
}

/// <summary>One entry of an archive's manifest: what it is, what it is called, and how big it is on disk.</summary>
public sealed class SdsResource
{
    /// <summary>File name as the manifest spells it — what the tile shows.</summary>
    public required string Name { get; init; }

    /// <summary>The engine's own type name, verbatim (<c>NAV_OBJ_DATA</c>). Kept because it is the thing the
    /// game and every other tool call it; the kind below is only our grouping of it.</summary>
    public required string Type { get; init; }

    public required SdsResourceKind Kind { get; init; }

    public required SdsResourceSection Section { get; init; }

    /// <summary>Size of the extracted payload, or 0 when the manifest names a file that is not on disk —
    /// which is a broken archive rather than an empty resource, and worth being able to see.</summary>
    public required long Size { get; init; }

    /// <summary>
    /// Whether this entry's manifest name IS a payload on disk. Not every type's is: a <c>Script</c> entry
    /// names the package it stands for and lists its actual files under elements of its own, so there is
    /// nothing at that path and calling it missing would be a lie about a resource that extracted fine.
    /// </summary>
    public required bool NamesFile { get; init; }

    public required FileInfo File { get; init; }
}

/// <summary>
/// An archive's manifest, read for showing rather than for loading: every resource it announces, typed and
/// filed under a section. This is the one place the library layer opens an archive — the catalog is a
/// directory walk and stays one, and stepping INTO an archive is a separate, deliberate act.
/// <para>
/// <see cref="Read"/> extracts if it has to, so it belongs on a background thread. The extraction is the
/// same shared working copy the stage loads from, so opening an archive that is already staged costs a
/// manifest parse and nothing else.
/// </para>
/// </summary>
public sealed class ArchiveContents
{
    private ArchiveContents(FileInfo archive, string folder, IReadOnlyList<SdsResource> resources)
    {
        Archive = archive;
        Folder = folder;
        Resources = resources;
    }

    public FileInfo Archive { get; }

    /// <summary>The extracted working copy the resources were read from.</summary>
    public string Folder { get; }

    /// <summary>Every resource the archive announces, in manifest order.</summary>
    public IReadOnlyList<SdsResource> Resources { get; }

    /// <summary>Reads one archive's manifest. Call on a background thread — it may extract first.</summary>
    public static ArchiveContents Read(FileInfo archive) =>
        ReadFrom(archive, SdsMeshLoader.EnsureExtracted(archive));

    /// <summary>
    /// The same, from a working copy that is not the archive's own. Editing an archive's contents means
    /// writing into the folder <see cref="MafiaEnvironment.ExtractedDir"/> names, which is the player's real
    /// install — so the regression harness points this at a scratch copy instead and leaves the game alone.
    /// </summary>
    public static ArchiveContents ReadFrom(FileInfo archive, string folder)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrEmpty(folder);

        SdsManifest manifest = SdsManifest.Load(folder);

        var resources = new List<SdsResource>(manifest.Entries.Count);
        foreach ((string type, string file) in manifest.Entries)
        {
            SdsResourceKind kind = SdsResourceKinds.Of(type);
            bool v20 = manifest.ArchiveVersion == 20;
            bool namesFile = v20 || SdsResourceKinds.NamesFile(type);
            var info = new FileInfo(Path.Combine(folder, v20 ? file : PayloadPath(file, kind)));
            resources.Add(new SdsResource
            {
                Name = Path.GetFileName(file),
                Type = type,
                Kind = kind,
                Section = SdsResourceKinds.SectionOf(kind),
                Size = namesFile && info.Exists ? info.Length : 0,
                NamesFile = namesFile,
                File = info,
            });
        }
        return new ArchiveContents(archive, folder, resources);
    }

    /// <summary>
    /// Where a manifest entry's payload actually sits under the extracted folder. Three things stand between
    /// the name and the file, and all three come from the extractor rather than from us:
    /// the manifest writes forward slashes, while a few types (sound tables, loose XML) sit in a sub-folder;
    /// names taken out of an archive's ResourceInfo are often ROOTED, and combining a rooted second path
    /// silently throws the first one away, so the leading separator has to come off;
    /// and the XML handler writes its payload with a <c>.xml</c> suffix that it does not record.
    /// </summary>
    /// <remarks>An empty name is legal input: a few community-repacked archives carry a lock entry whose
    /// File element is blank, and the browser has to survive whatever is in the game folder.</remarks>
    public static string PayloadPath(string file, SdsResourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(file);
        string relative = file.Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        return kind == SdsResourceKind.Xml ? relative + ".xml" : relative;
    }
}

/// <summary>Maps the engine's manifest type names onto <see cref="SdsResourceKind"/> and its section.</summary>
public static class SdsResourceKinds
{
    // Every type name SdsArchive knows how to extract, plus "Animated Texture" — which is not in that list
    // but is in the shipped archives, spelled with a space. A name missing from here still shows up, under
    // Other; the browser is not the place to be strict about an archive the game itself loads.
    private static readonly Dictionary<string, SdsResourceKind> ByType =
        new(StringComparer.Ordinal)
        {
            ["FrameResource"] = SdsResourceKind.Mesh,
            ["Generic"] = SdsResourceKind.Mesh,
            ["FrameNameTable"] = SdsResourceKind.NameTable,
            ["IndexBufferPool"] = SdsResourceKind.Buffer,
            ["VertexBufferPool"] = SdsResourceKind.Buffer,
            ["Texture"] = SdsResourceKind.Texture,
            ["Mipmap"] = SdsResourceKind.Mipmap,
            ["Animated Texture"] = SdsResourceKind.AnimatedTexture,
            ["Effects"] = SdsResourceKind.Effect,
            ["FxActor"] = SdsResourceKind.Effect,
            ["FxAnimSet"] = SdsResourceKind.Effect,
            ["Collisions"] = SdsResourceKind.Collision,
            ["ItemDesc"] = SdsResourceKind.Shape,
            ["Actors"] = SdsResourceKind.Actor,
            ["EntityDataStorage"] = SdsResourceKind.EntityData,
            ["PREFAB"] = SdsResourceKind.Prefab,
            ["Translokator"] = SdsResourceKind.Instances,
            ["Animation2"] = SdsResourceKind.Animation,
            ["Cutscene"] = SdsResourceKind.Cutscene,
            ["Sound"] = SdsResourceKind.Sound,
            ["SoundTable"] = SdsResourceKind.Sound,
            ["Speech"] = SdsResourceKind.Speech,
            ["AudioSectors"] = SdsResourceKind.AudioSector,
            ["NAV_OBJ_DATA"] = SdsResourceKind.Navigation,
            ["NAV_AIWORLD_DATA"] = SdsResourceKind.Navigation,
            ["NAV_PATH_DATA"] = SdsResourceKind.Navigation,
            ["NAV_HPD_DATA"] = SdsResourceKind.Navigation,
            ["AnimalTrafficPaths"] = SdsResourceKind.TrafficPath,
            ["Script"] = SdsResourceKind.Script,
            // Listed for completeness and unreachable today: a Table entry's second manifest element is
            // NumTables rather than File, and the manifest reader only surfaces single-payload entries (see
            // SdsManifest). Classifying it costs a line and stops the day it does surface being a surprise.
            ["Table"] = SdsResourceKind.Table,
            ["XML"] = SdsResourceKind.Xml,
            ["MemFile"] = SdsResourceKind.Binary,
        };

    private static readonly Dictionary<SdsResourceKind, SdsResourceSection> Sections = new()
    {
        [SdsResourceKind.Mesh] = SdsResourceSection.Geometry,
        [SdsResourceKind.NameTable] = SdsResourceSection.Geometry,
        [SdsResourceKind.Buffer] = SdsResourceSection.Geometry,
        [SdsResourceKind.Texture] = SdsResourceSection.Textures,
        [SdsResourceKind.Mipmap] = SdsResourceSection.Textures,
        [SdsResourceKind.AnimatedTexture] = SdsResourceSection.Textures,
        [SdsResourceKind.Effect] = SdsResourceSection.Effects,
        [SdsResourceKind.Collision] = SdsResourceSection.Collision,
        [SdsResourceKind.Shape] = SdsResourceSection.Collision,
        [SdsResourceKind.Actor] = SdsResourceSection.Entities,
        [SdsResourceKind.EntityData] = SdsResourceSection.Entities,
        [SdsResourceKind.Prefab] = SdsResourceSection.Entities,
        [SdsResourceKind.Instances] = SdsResourceSection.Entities,
        [SdsResourceKind.Animation] = SdsResourceSection.Animation,
        [SdsResourceKind.Cutscene] = SdsResourceSection.Animation,
        [SdsResourceKind.Sound] = SdsResourceSection.Audio,
        [SdsResourceKind.Speech] = SdsResourceSection.Audio,
        [SdsResourceKind.AudioSector] = SdsResourceSection.Audio,
        [SdsResourceKind.Navigation] = SdsResourceSection.Navigation,
        [SdsResourceKind.TrafficPath] = SdsResourceSection.Navigation,
        [SdsResourceKind.Script] = SdsResourceSection.Data,
        [SdsResourceKind.Table] = SdsResourceSection.Data,
        [SdsResourceKind.Xml] = SdsResourceSection.Data,
        [SdsResourceKind.Binary] = SdsResourceSection.Data,
    };

    /// <summary>The kind for a manifest type name; anything unlisted comes back
    /// <see cref="SdsResourceKind.Unknown"/>.</summary>
    public static SdsResourceKind Of(string type) =>
        ByType.GetValueOrDefault(type, SdsResourceKind.Unknown);

    /// <summary>
    /// Whether a type's manifest name is a payload on disk. Only <c>Script</c> is not: it names the package
    /// it stands for and lists its actual files under elements of its own, which the manifest reader does not
    /// surface — so nothing is at that path and the tile must not claim the resource is missing.
    /// </summary>
    public static bool NamesFile(string type) => !string.Equals(type, "Script", StringComparison.Ordinal);

    /// <summary>The section a kind is filed under.</summary>
    public static SdsResourceSection SectionOf(SdsResourceKind kind) =>
        Sections.GetValueOrDefault(kind, SdsResourceSection.Other);

    /// <summary>The section's name as the header spells it — the enum member, with the two-word ones
    /// spelled out.</summary>
    public static string TitleOf(SdsResourceSection section) => section switch
    {
        SdsResourceSection.Collision => "Collision & physics",
        SdsResourceSection.Data => "Scripts & data",
        SdsResourceSection.Navigation => "AI navigation",
        _ => section.ToString(),
    };
}
