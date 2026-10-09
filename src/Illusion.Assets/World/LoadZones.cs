using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.CityAreas;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.World;

/// <summary>
/// The city's load zones as the game states them, open for editing.
/// <para>
/// A zone is an AREA volume in the scene of <c>city_univers.sds</c> - a world matrix, a box in the volume's own
/// space and the planes that bound it - and <c>missions\CITY\cityareas.bin</c> names, for each volume, the one
/// or two districts the game keeps loaded while the camera is inside it. A place that lies in no volume asks
/// for nothing: put there by a teleport, the player stands in a district that never streams in, until he
/// walks into a zone that asks for it.
/// </para>
/// <para>
/// The planes are what decides: a point is inside when <c>n.p + d &gt;= 0</c> for every plane, in the volume's
/// space. The box is the planes' own extent and is kept in step with them here - a face is moved in both.
/// </para>
/// </summary>
public sealed class LoadZones
{
    private readonly Dictionary<string, FrameObjectArea> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _districts = new(StringComparer.OrdinalIgnoreCase);

    // Asked about a point: the volumes in name order, and each one's way from the world into its own space.
    // Worked out once - a plan of a district asks about tens of thousands of points - and dropped when a
    // volume is moved.
    private (string Name, FrameObjectArea Zone)[]? _ordered;
    private readonly Dictionary<FrameObjectArea, Matrix4x4?> _toLocal = [];

    private LoadZones(FileInfo archive, FrameResource frame, string sceneFile)
    {
        Archive = archive;
        Frame = frame;
        SceneFile = sceneFile;
    }

    // For a zone that is ADDED: the working copy's folder, the scene read with its name table (a new volume
    // has to be listed there), and the table of districts once it has been read to be changed.
    private string _extracted = "";
    private HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private bool _withNameTable;
    private bool _volumeAdded;
    private CityAreasTable? _table;

    private string TableFile => Path.Combine(_extracted, "missions", "CITY", "cityareas.bin");

    /// <summary>The frame name table of the working copy - the file a new volume is listed in - or null when
    /// the archive has none.</summary>
    public string? NameTableFile => SdsManifest.Load(_extracted).GetFiles("FrameNameTable").FirstOrDefault();

    /// <summary>The file of the working copy the scene was read from, and <see cref="Save"/> writes.</summary>
    public string SceneFile { get; }

    /// <summary>The archive the zones live in: a copy of <c>city_univers.sds</c> (see <see cref="Copies"/>).</summary>
    public FileInfo Archive { get; }

    /// <summary>
    /// Every copy of <c>city_univers.sds</c> the game has: the base one first, then one for each DLC that ships
    /// its own (<c>pc\dlcs\&lt;dlc&gt;\sds\city_univers\</c> - Joe's Adventures does). A DLC's copy is a scene of
    /// its own, with fewer zones. Measured in the game (free ride of a multiplayer client that mounts Joe's
    /// Adventures): the zones in force were the BASE copy's - a face moved in the DLC's copy changed nothing.
    /// <para>
    /// Measured the same way: what makes a district stream in at the point a player appears at is a zone of
    /// the seam kind ("AREA341_GREENFIELD_KINGSTONE") - see <see cref="LoadsOnArrival"/>. A district's own box
    /// ("AREA0019_GREENFIELD") did not load it by itself, and a point in no seam zone keeps whatever was loaded
    /// before - nothing, right after a login.
    /// </para>
    /// </summary>
    public static IReadOnlyList<FileInfo> Copies()
    {
        var copies = new List<FileInfo> { new(MafiaEnvironment.CityUniversSds) };
        string dlcs = Path.Combine(MafiaEnvironment.PcFolder, "dlcs");
        if (Directory.Exists(dlcs))
        {
            foreach (string dlc in Directory.GetDirectories(dlcs).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var copy = new FileInfo(Path.Combine(dlc, "sds", "city_univers", "city_univers.sds"));
                if (copy.Exists) copies.Add(copy);
            }
        }
        return copies;
    }

    /// <summary>
    /// Whether a volume of this name loads its districts for a player who APPEARS inside it (a login, a
    /// teleport), as opposed to keeping loaded what a neighbour brought in. Measured in the game, with new zones
    /// over one test spot (free ride of a multiplayer client, 2026-10): the NAME decides. Two words after the
    /// number - "AREA900_SANDISLAND_TUNEL", "AREA901_FOO_BAR", "AREA903_A_B" - and the district was loaded,
    /// with one district in cityareas.bin or two, its byte 0 or 1. One word - "AREA900_TEST",
    /// "AREA902_FOOXBAR" - and it was not, with two districts and the byte 1. The table says WHICH districts;
    /// it does not say whether. The shipped names agree: a district's own box has one word and one district, a
    /// seam two and two. The thirty shipped names written with hyphens ("AREA0223-DIPTON-KINGSTONE") were not
    /// measured and are counted by their words too.
    /// </summary>
    public static bool LoadsOnArrival(string? name) =>
        (name ?? "").Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries).Length >= 3;

    private static readonly Lazy<HashSet<string>> Shipped = new(() =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        System.Reflection.Assembly assembly = typeof(LoadZones).Assembly;
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("ShippedLoadZones.txt", StringComparison.Ordinal));
        if (resource == null || assembly.GetManifestResourceStream(resource) is not { } stream) return names;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim() is { Length: > 0 } name) names.Add(name);
        }
        return names;
    });

    /// <summary>
    /// Whether a volume of this name came with the game: it is on the list of the AREA volumes the base game's
    /// <c>city_univers</c> and the DLC copies ship with. One that is not was ADDED - by <see cref="Create"/>, here
    /// or on the machine the archive came from - and is the kind that is marked in the layer and may be taken out.
    /// </summary>
    public static bool IsShipped(string? name) => name != null && Shipped.Value.Contains(name);

    /// <summary>How a copy is named to the user: "base", or the DLC's folder name.</summary>
    public static string CopyName(FileInfo copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        string full = copy.FullName;
        int at = full.IndexOf(Path.DirectorySeparatorChar + "dlcs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "base";
        string rest = full[(at + 6)..];
        return rest[..rest.IndexOf(Path.DirectorySeparatorChar)];
    }

    /// <summary>Its scene, as read from the working copy.</summary>
    public FrameResource Frame { get; }

    /// <summary>Every volume, by name.</summary>
    public IReadOnlyDictionary<string, FrameObjectArea> Volumes => _volumes;

    private readonly HashSet<string> _ambiguous = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names that more than one volume of the scene carries. Such a volume is listed (the first of
    /// them) but not moved: a name is all a caller has to say which one is meant.</summary>
    public IReadOnlyCollection<string> AmbiguousNames => _ambiguous;

    /// <summary>The districts a volume keeps loaded, as resolved archive names; none for a volume the table does
    /// not list (shop zones are bound elsewhere).</summary>
    public IReadOnlyList<string> DistrictsOf(string zone) =>
        _districts.TryGetValue(zone, out IReadOnlyList<string>? districts) ? districts : [];

    /// <summary>Reads the zones from the working copy of a <c>city_univers.sds</c>.</summary>
    /// <param name="ensureExtracted">Unpacks an archive when it has no working copy yet; returns its folder.</param>
    /// <param name="districts">The district archives' base names ("midtown"), to resolve the table's targets against.</param>
    /// <param name="copy">Which copy (one of <see cref="Copies"/>); the base game's when null.</param>
    /// <param name="forNewZones">Reads the scene together with its frame name table - what <see cref="Create"/>
    /// needs, and what costs a full read of the archive's buffer pools; moving existing zones does not need it.</param>
    public static LoadZones Open(Func<FileInfo, string> ensureExtracted, IReadOnlyCollection<string> districts, FileInfo? copy = null,
        bool forNewZones = false)
    {
        ArgumentNullException.ThrowIfNull(ensureExtracted);
        ArgumentNullException.ThrowIfNull(districts);
        FileInfo archive = copy ?? new FileInfo(MafiaEnvironment.CityUniversSds);
        if (!archive.Exists) throw new FileNotFoundException("city_univers.sds is not in the game folder", archive.FullName);

        string extracted = ensureExtracted(archive);
        // The scene alone. A zone is a matrix, a box and planes; the archive's vertex and index pools have
        // nothing to say about it, and reading them all cost every click on a zone a full load of the archive.
        IReadOnlyList<string> scenes;
        try
        {
            scenes = SdsManifest.Load(extracted).GetFiles("FrameResource");
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException("the working copy of city_univers.sds has a contents list that cannot be read: " + ex.Message, ex);
        }
        if (scenes.Count == 0) throw new InvalidDataException("city_univers.sds has no scene");
        // A volume that is added has to be listed in the frame name table, with the flags the other volumes
        // carry there - and those are only on the frames when the scene is read together with the table.
        FrameResource scene = forNewZones
            ? ExtractedSds.Load(extracted).FrameResource ?? throw new InvalidDataException("city_univers.sds has no scene")
            : new FrameResource(scenes[0]);
        var zones = new LoadZones(archive, scene, scenes[0])
        {
            _extracted = extracted,
            _withNameTable = forNewZones,
            _known = new HashSet<string>(districts, StringComparer.OrdinalIgnoreCase),
        };
        foreach (FrameObjectArea area in zones.Frame.FrameObjects.Values.OfType<FrameObjectArea>())
        {
            if (area.Name?.ToString() is not { Length: > 0 } name) continue;
            // Two volumes under one name could not be told apart by the table that names them, nor by a
            // caller that names one to move: the first is kept, and the name is remembered as ambiguous.
            if (!zones._volumes.TryAdd(name, area)) zones._ambiguous.Add(name);
        }

        string table = Path.Combine(extracted, "missions", "CITY", "cityareas.bin");
        if (File.Exists(table))
        {
            HashSet<string> known = zones._known;
            foreach (CityAreaEntry entry in CityAreasFile.Load(table).Areas)
            {
                var list = new List<string>(2);
                foreach (string? target in new[] { entry.Target1, entry.Target2 })
                {
                    if (DistrictNames.Resolve(target, known) is { } district && !list.Contains(district, StringComparer.OrdinalIgnoreCase))
                        list.Add(district);
                }
                if (list.Count > 0) zones._districts[entry.Name] = list;
            }
        }
        return zones;
    }

    /// <summary>Whether a world point is inside a volume. <paramref name="outsideBy"/> is how far past the
    /// nearest violated plane it lies (0 when inside).</summary>
    public static bool Contains(FrameObjectArea zone, Vector3 world, out float outsideBy)
    {
        ArgumentNullException.ThrowIfNull(zone);
        outsideBy = 0f;
        return Matrix4x4.Invert(zone.WorldTransform, out Matrix4x4 toLocal) && Contains(zone, toLocal, world, out outsideBy);
    }

    private static bool Contains(FrameObjectArea zone, Matrix4x4 toLocal, Vector3 world, out float outsideBy)
    {
        outsideBy = 0f;
        Vector3 local = Vector3.Transform(world, toLocal);
        if (zone.Planes is not { Length: > 0 } planes)
        {
            Vector3 lo = zone.Bounds.Min, hi = zone.Bounds.Max;
            Vector3 beyond = Vector3.Max(Vector3.Max(lo - local, local - hi), Vector3.Zero);
            outsideBy = MathF.Max(beyond.X, MathF.Max(beyond.Y, beyond.Z));
            return outsideBy <= 0f;
        }
        foreach (Vector4 plane in planes)
        {
            float depth = Vector3.Dot(new Vector3(plane.X, plane.Y, plane.Z), local) + plane.W;
            if (-depth > outsideBy) outsideBy = -depth;
        }
        return outsideBy <= 0f;
    }

    /// <summary>A volume's box in world space (the eight corners of its own box through its matrix).</summary>
    public static (Vector3 Min, Vector3 Max) WorldBox(FrameObjectArea zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        Vector3 lo = zone.Bounds.Min, hi = zone.Bounds.Max;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = Vector3.Transform(
                new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z),
                zone.WorldTransform);
            min = Vector3.Min(min, corner);
            max = Vector3.Max(max, corner);
        }
        return (min, max);
    }

    /// <summary>The volumes that hold a point, or come within <paramref name="near"/> metres of holding it.</summary>
    public IEnumerable<(string Name, FrameObjectArea Zone, bool Inside, float OutsideBy)> At(Vector3 world, float near = 0f)
    {
        _ordered ??= [.. _volumes.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => (v.Key, v.Value))];
        foreach ((string name, FrameObjectArea zone) in _ordered)
        {
            if (!_toLocal.TryGetValue(zone, out Matrix4x4? toLocal))
            {
                toLocal = Matrix4x4.Invert(zone.WorldTransform, out Matrix4x4 inverse) ? inverse : null;
                _toLocal[zone] = toLocal;
            }
            if (toLocal is not { } into) continue;
            bool inside = Contains(zone, into, world, out float outsideBy);
            if (inside || outsideBy <= near) yield return (name, zone, inside, outsideBy);
        }
    }

    /// <summary>The districts asked for at a point: those of every volume that holds it.</summary>
    public IReadOnlyList<string> DistrictsAt(Vector3 world) =>
        [.. At(world).SelectMany(z => DistrictsOf(z.Name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.Ordinal)];

    /// <summary>
    /// Moves one face of a volume to a world coordinate, IN MEMORY: the plane that bounds it on that side and
    /// its box together. <see cref="Save"/> writes it. Only for a volume that is neither turned nor scaled and
    /// a face that lies square to the axis - which is how the city's zones are cut, except where one corner is
    /// sliced off by an extra plane, and that plane is left as it is.
    /// </summary>
    /// <param name="zone">The volume's name.</param>
    /// <param name="face">"+x", "-x", "+y", "-y", "+z" or "-z": the face on that side.</param>
    /// <param name="to">Where the face is to stand, on that axis, in world coordinates.</param>
    /// <returns>Null on success, otherwise why not.</returns>
    public string? MoveFace(string zone, string face, float to, out LoadZoneFaceMove? move)
    {
        move = null;
        if (!_volumes.TryGetValue(zone ?? "", out FrameObjectArea? area)) return $"no load zone named '{zone}'";
        if (_ambiguous.Contains(zone!)) return $"more than one volume of the scene is named '{zone}' - which one is meant cannot be told";
        if (face is not { Length: 2 } || (face[0] != '+' && face[0] != '-') || "xyz".IndexOf(char.ToLowerInvariant(face[1])) < 0)
            return "face is one of +x, -x, +y, -y, +z, -z";
        if (!float.IsFinite(to)) return "the coordinate must be a finite number";

        Matrix4x4 world = area.WorldTransform;
        if (MathF.Abs(world.M11 - 1f) > 1e-3f || MathF.Abs(world.M22 - 1f) > 1e-3f || MathF.Abs(world.M33 - 1f) > 1e-3f)
            return $"{zone} is turned or scaled; only a volume standing square to the map can have a face moved";

        int axis = "xyz".IndexOf(char.ToLowerInvariant(face[1]));
        bool upper = face[0] == '+';
        float origin = axis == 0 ? world.M41 : axis == 1 ? world.M42 : world.M43;
        float local = to - origin;

        // Inside is n.p + d >= 0: the face that limits the volume on its upper side has its normal pointing
        // DOWN the axis and d equal to the limit; on the lower side the normal points up and d is minus it.
        int found = -1;
        for (int i = 0; i < area.Planes.Length; i++)
        {
            Vector4 plane = area.Planes[i];
            float[] n = [plane.X, plane.Y, plane.Z];
            bool square = true;
            for (int k = 0; k < 3; k++)
            {
                if (k != axis && MathF.Abs(n[k]) > 1e-3f) square = false;
            }
            if (square && MathF.Abs(n[axis] - (upper ? -1f : 1f)) < 1e-3f) found = i;
        }
        if (found < 0) return $"{zone} has no face square to the axis on {face}";

        Vector3 lo = area.Bounds.Min, hi = area.Bounds.Max;
        float other = upper ? (axis == 0 ? lo.X : axis == 1 ? lo.Y : lo.Z) : (axis == 0 ? hi.X : axis == 1 ? hi.Y : hi.Z);
        if (upper ? local <= other + 0.5f : local >= other - 0.5f)
            return $"that would leave {zone} with no depth on {face[1]}: its opposite face stands at {other + origin:F2}";

        Vector4 before = area.Planes[found];
        float from = (upper ? before.W : -before.W) + origin;
        area.Planes[found] = new Vector4(before.X, before.Y, before.Z, upper ? local : -local);
        if (upper)
        {
            if (axis == 0) hi.X = local; else if (axis == 1) hi.Y = local; else hi.Z = local;
        }
        else
        {
            if (axis == 0) lo.X = local; else if (axis == 1) lo.Y = local; else lo.Z = local;
        }
        var box = area.Bounds;
        box.Min = lo;
        box.Max = hi;
        area.Bounds = box;

        (Vector3 min, Vector3 max) = WorldBox(area);
        move = new LoadZoneFaceMove(area.Name.ToString(), face.ToLowerInvariant(), found, from, to, min, max);
        return null;
    }

    /// <summary>
    /// The faces of a volume that <see cref="MoveFace"/> can move: those with a plane square to their axis, on a
    /// volume that stands square to the map. A corner sliced off by a slanted plane leaves its side without one.
    /// </summary>
    public IReadOnlySet<string> SquareFaces(string zone)
    {
        var faces = new HashSet<string>(StringComparer.Ordinal);
        if (!_volumes.TryGetValue(zone ?? "", out FrameObjectArea? area)) return faces;
        Matrix4x4 world = area.WorldTransform;
        if (MathF.Abs(world.M11 - 1f) > 1e-3f || MathF.Abs(world.M22 - 1f) > 1e-3f || MathF.Abs(world.M33 - 1f) > 1e-3f) return faces;
        foreach (Vector4 plane in area.Planes)
        {
            float[] n = [plane.X, plane.Y, plane.Z];
            for (int axis = 0; axis < 3; axis++)
            {
                bool square = MathF.Abs(MathF.Abs(n[axis]) - 1f) < 1e-3f && Enumerable.Range(0, 3).All(k => k == axis || MathF.Abs(n[k]) <= 1e-3f);
                // the face on the upper side has its normal pointing DOWN the axis (see MoveFace)
                if (square) faces.Add((n[axis] < 0 ? "+" : "-") + "xyz"[axis]);
            }
        }
        return faces;
    }

    /// <summary>
    /// Moves a whole volume by a world offset, IN MEMORY: its place changes, its planes and its box - which are
    /// in its own space - do not, so this works for a volume of any shape. <see cref="Save"/> writes it.
    /// </summary>
    /// <returns>Null on success, otherwise why not.</returns>
    public string? Move(string zone, Vector3 by)
    {
        if (!_volumes.TryGetValue(zone ?? "", out FrameObjectArea? area)) return $"no load zone named '{zone}'";
        if (_ambiguous.Contains(zone!)) return $"more than one volume of the scene is named '{zone}' - which one is meant cannot be told";
        if (!float.IsFinite(by.X) || !float.IsFinite(by.Y) || !float.IsFinite(by.Z)) return "the offset must be finite numbers";
        // A volume's place in the world is its local place put through its parent (FrameObjectBase works the
        // world transform out the same way), so the offset is taken into the parent's space before it is added.
        // Only the parent's turn and scale are used: a frame matrix is not kept as a full 4 x 4, and inverting
        // one as it stands fails.
        Matrix4x4 parent = (area.Parent ?? area.Root)?.WorldTransform ?? Matrix4x4.Identity;
        var turn = new Matrix4x4(
            parent.M11, parent.M12, parent.M13, 0f,
            parent.M21, parent.M22, parent.M23, 0f,
            parent.M31, parent.M32, parent.M33, 0f,
            0f, 0f, 0f, 1f);
        if (!Matrix4x4.Invert(turn, out Matrix4x4 unTurn)) return $"{zone} hangs on a frame whose transform cannot be inverted";
        Matrix4x4 local = area.LocalTransform;
        local.Translation += Vector3.TransformNormal(by, unTurn);
        area.LocalTransform = local;
        _toLocal.Clear();          // its own way into its space, and that of anything hanging on it
        return null;
    }

    /// <summary>
    /// Adds a NEW volume to the scene, IN MEMORY, and a line for it to the table of districts: a box standing
    /// square to the map between two world corners, keeping the one or two districts named loaded while the
    /// player is inside it. It is made as a copy of an existing volume (<paramref name="like"/>) - its type,
    /// flags, parent and place in the name table - given a name, a place and a shape of its own.
    /// <see cref="Save"/> writes the scene, the name table and the districts table.
    /// </summary>
    /// <returns>Null on success, otherwise why not; nothing is changed on a refusal.</returns>
    public string? Create(string name, string like, Vector3 min, Vector3 max, string district1, string? district2)
    {
        if (!_withNameTable) return "these zones were opened for moving, not for adding to";
        if (string.IsNullOrWhiteSpace(name)) return "a zone needs a name";
        if (Frame.FrameObjects.Values.OfType<FrameObjectBase>().Any(o => string.Equals(o.Name?.ToString(), name, StringComparison.OrdinalIgnoreCase)))
            return $"the scene already has an object named '{name}'";
        if (!_volumes.TryGetValue(like ?? "", out FrameObjectArea? source)) return $"no load zone named '{like}' to make it like";
        foreach (float v in new[] { min.X, min.Y, min.Z, max.X, max.Y, max.Z })
        {
            if (!float.IsFinite(v)) return "the corners must be finite numbers";
        }
        Vector3 lo = Vector3.Min(min, max), hi = Vector3.Max(min, max), half = (hi - lo) * 0.5f;
        if (half.X < 0.5f || half.Y < 0.5f || half.Z < 0.5f) return "the box must be at least a metre on every side";
        Matrix4x4 world = source.WorldTransform;
        if (MathF.Abs(world.M11 - 1f) > 1e-3f || MathF.Abs(world.M22 - 1f) > 1e-3f || MathF.Abs(world.M33 - 1f) > 1e-3f)
            return $"{like} is turned or scaled - take a volume that stands square to the map as the pattern";

        // its own origin goes to the box's centre, said in the space of the frame the pattern hangs on
        Matrix4x4 parent = (source.Parent ?? source.Root)?.WorldTransform ?? Matrix4x4.Identity;
        var turn = new Matrix4x4(
            parent.M11, parent.M12, parent.M13, 0f,
            parent.M21, parent.M22, parent.M23, 0f,
            parent.M31, parent.M32, parent.M33, 0f,
            0f, 0f, 0f, 1f);
        if (!Matrix4x4.Invert(turn, out Matrix4x4 unTurn)) return $"{like} hangs on a frame whose transform cannot be inverted";

        if (!File.Exists(TableFile)) return "the working copy of city_univers has no cityareas.bin";
        CityAreasTable table = _table ?? CityAreasTable.Parse(File.ReadAllBytes(TableFile));
        // The table names districts in its own words ("kingston" for the archive kingstone): a district is
        // written as the table says it, and one it has no word for must at least be an archive of the city.
        var resolved = new List<string>(2);
        var written = new List<string>(2);
        foreach (string? asked in new[] { district1, district2 })
        {
            if (string.IsNullOrWhiteSpace(asked))
            {
                if (resolved.Count == 0) return "a zone names at least one district";
                continue;
            }
            if (TableWord(table, asked.Trim()) is not var (word, district)) return $"'{asked.Trim()}' is not a district of the city";
            if (resolved.Contains(district, StringComparer.OrdinalIgnoreCase)) return "the two districts are the same one";
            written.Add(word);
            resolved.Add(district);
        }
        if (table.Add(name, written[0], written.Count > 1 ? written[1] : null) is { } refused) return refused;

        // The copy constructor takes every serialized field of the pattern - flags, name-table membership, the
        // references to its parents - and shares its planes array, which is replaced below.
        var clone = new FrameObjectArea(source) { Name = new HashName(name) };
        var box = clone.Bounds;
        box.Min = -half;
        box.Max = half;
        clone.Bounds = box;
        // inside is n.p + d >= 0 in the volume's own space: an upper face looks down its axis, a lower one up
        clone.Planes =
        [
            new Vector4(-1, 0, 0, half.X), new Vector4(1, 0, 0, half.X),
            new Vector4(0, -1, 0, half.Y), new Vector4(0, 1, 0, half.Y),
            new Vector4(0, 0, -1, half.Z), new Vector4(0, 0, 1, half.Z),
        ];
        clone.PlaneSize = clone.Planes.Length;

        Frame.FrameObjects.Add(clone.RefID, clone);
        Frames.FrameDuplicator.LinkParents(clone,
            Frames.FrameDuplicator.ResolveRef(Frame, source, FrameEntryRefTypes.Parent1),
            Frames.FrameDuplicator.ResolveRef(Frame, source, FrameEntryRefTypes.Parent2));

        // the pattern's place, carried by the difference between the centres
        Matrix4x4 local = source.LocalTransform;
        local.Translation += Vector3.TransformNormal(((lo + hi) * 0.5f) - world.Translation, unTurn);
        clone.LocalTransform = local;

        _table = table;
        _volumeAdded = true;
        _volumes[name] = clone;
        _districts[name] = resolved;
        _ordered = null;
        _toLocal.Clear();
        return null;
    }

    // How the table says a district, and the archive that is: the table's own name for it when it has one
    // (exactly, or one that resolves to the archive asked for), otherwise the archive's name as it stands.
    private (string Word, string District)? TableWord(CityAreasTable table, string asked)
    {
        IReadOnlyList<string> words = table.Districts;
        string? word = words.FirstOrDefault(w => string.Equals(w, asked, StringComparison.OrdinalIgnoreCase))
            ?? words.FirstOrDefault(w => string.Equals(DistrictNames.Resolve(w, _known), asked, StringComparison.OrdinalIgnoreCase));
        if (word != null) return (word, DistrictNames.Resolve(word, _known) ?? word);
        return _known.TryGetValue(asked, out string? archive) ? (archive, archive) : null;
    }

    /// <summary>
    /// Takes a volume out of the scene, IN MEMORY, and its line out of the table of districts - what undoing a
    /// <see cref="Create"/> is. <see cref="Save"/> writes the scene, the name table and the districts table.
    /// </summary>
    /// <returns>Null on success, otherwise why not; nothing is changed on a refusal.</returns>
    public string? Delete(string name)
    {
        if (!_withNameTable) return "these zones were opened for moving, not for taking out";
        if (!_volumes.TryGetValue(name ?? "", out FrameObjectArea? area)) return $"no load zone named '{name}'";
        if (_ambiguous.Contains(name!)) return $"more than one volume of the scene is named '{name}' - which one is meant cannot be told";
        if (Frame.FrameObjects.Values.OfType<FrameObjectBase>().Any(o => ReferenceEquals(o.Parent, area) || ReferenceEquals(o.Root, area)))
            return $"other objects of the scene hang on {name}";
        if (!File.Exists(TableFile)) return "the working copy of city_univers has no cityareas.bin";
        CityAreasTable table = _table ?? CityAreasTable.Parse(File.ReadAllBytes(TableFile));
        table.Remove(name!);

        area.SetParent(ParentInfo.ParentType.ParentIndex1, null);
        area.SetParent(ParentInfo.ParentType.ParentIndex2, null);
        foreach (FrameHeaderScene scene in Frame.FrameScenes.Values) scene.Children.Remove(area);
        Frame.FrameObjects.Remove(area.RefID);

        _table = table;
        _volumeAdded = true;
        _volumes.Remove(name!);
        _districts.Remove(name!);
        _ordered = null;
        _toLocal.Remove(area);
        return null;
    }

    /// <summary>
    /// Whether a scene an open editor holds has this volume exactly as this one has it - its place, its box and
    /// its planes. It has not when the volume was changed in that editor and not saved: a write made from the
    /// disk's copy would then be made from a state the editor is about to replace. A scene without the volume
    /// has nothing to disagree with.
    /// </summary>
    public bool InStepWith(SceneDocumentAdapter document, string zone)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!_volumes.TryGetValue(zone ?? "", out FrameObjectArea? mine) || ReferenceEquals(document.Frame, Frame)) return true;
        FrameObjectArea? theirs = document.Frame.FrameObjects.Values.OfType<FrameObjectArea>()
            .FirstOrDefault(a => string.Equals(a.Name?.ToString(), zone, StringComparison.OrdinalIgnoreCase));
        return theirs == null
            || (SamePlace(theirs.LocalTransform, mine.LocalTransform) && theirs.Bounds.Min == mine.Bounds.Min && theirs.Bounds.Max == mine.Bounds.Max
                && (theirs.Planes ?? []).SequenceEqual(mine.Planes ?? []));
    }

    // A frame's matrix is stored as three columns of four. The fourth is not in the file: read from disk it is
    // all zeros, composed by an editor it ends in a one - so a volume moved in the editor and SAVED compared
    // as "changed and not saved" for good. What counts is what a save writes.
    private static bool SamePlace(Matrix4x4 a, Matrix4x4 b) =>
        a.M11 == b.M11 && a.M12 == b.M12 && a.M13 == b.M13
        && a.M21 == b.M21 && a.M22 == b.M22 && a.M23 == b.M23
        && a.M31 == b.M31 && a.M32 == b.M32 && a.M33 == b.M33
        && a.M41 == b.M41 && a.M42 == b.M42 && a.M43 == b.M43;

    /// <summary>
    /// Brings the same volume in a scene an open editor holds in step with this one: its place, its box and its
    /// planes. An editor that has the archive loaded (the map editor in Whole map mode does) writes its own copy
    /// of the scene whole on its next save, and without this that save would put the volume back where it stood.
    /// </summary>
    /// <returns>False when that scene has no volume of this name.</returns>
    public bool MirrorInto(SceneDocumentAdapter document, string zone)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!_volumes.TryGetValue(zone ?? "", out FrameObjectArea? mine) || ReferenceEquals(document.Frame, Frame)) return false;
        FrameObjectArea? theirs = document.Frame.FrameObjects.Values.OfType<FrameObjectArea>()
            .FirstOrDefault(a => string.Equals(a.Name?.ToString(), zone, StringComparison.OrdinalIgnoreCase));
        if (theirs == null) return false;
        theirs.Planes = [.. mine.Planes];
        var box = theirs.Bounds;
        box.Min = mine.Bounds.Min;
        box.Max = mine.Bounds.Max;
        theirs.Bounds = box;
        theirs.LocalTransform = mine.LocalTransform;
        return true;
    }

    /// <summary>Writes the scene into the working copy of <c>city_univers.sds</c>; a Build of that archive
    /// takes it into the game. Returns the file written.</summary>
    public string Save()
    {
        if (!_volumeAdded) return SdsWriter.SaveFrameResource(Frame, Archive);

        // A new volume is three files - the scene, the name table that lists it and the districts table that
        // names it - and one without the others is a scene the game has no use for. What each held is kept
        // until the last is written, and put back if one fails.
        IReadOnlyDictionary<string, byte[]?> before = StructureFiles();
        var replaced = new List<string>();
        try
        {
            string written = SdsWriter.SaveFrameResource(Frame, Archive);
            replaced.Add(written);
            if (SdsWriter.SaveFrameNameTable(Frame, Archive) is { } names) replaced.Add(names);
            AtomicFile.WriteAllBytes(TableFile, _table!.ToBytes());
            return written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            // a write is one file swapped in whole: the one that failed is as it was, the ones before it are not
            string left = PutBack(replaced.ToDictionary(f => f, f => before.GetValueOrDefault(f)));
            if (left.Length == 0) throw;
            throw new IOException($"{ex.Message} - and the working copy could not be put back as it was ({left}): unpack city_univers again", ex);
        }
    }

    /// <summary>
    /// The files a volume that is added or taken out is written into - the scene, the name table that lists it
    /// and the districts table that names it - each with what it holds now, or null when it is not there.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]?> StructureFiles()
    {
        List<string> files = [SceneFile, .. SdsManifest.Load(_extracted).GetFiles("FrameNameTable"), TableFile];
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(f => f, f => File.Exists(f) ? File.ReadAllBytes(f) : null);
    }

    /// <summary>
    /// Writes those files as they are given: all of them or - when one cannot be written - none, the ones
    /// already swapped in being put back.
    /// </summary>
    /// <exception cref="IOException">A file could not be written; the message says what was left changed, if anything.</exception>
    public static void Restore(IReadOnlyDictionary<string, byte[]?> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var before = files.Keys.ToDictionary(f => f, f => File.Exists(f) ? File.ReadAllBytes(f) : null);
        var replaced = new Dictionary<string, byte[]?>();
        try
        {
            foreach ((string file, byte[]? bytes) in files)
            {
                if (bytes != null) AtomicFile.WriteAllBytes(file, bytes);
                else File.Delete(file);
                replaced[file] = before[file];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string left = PutBack(replaced);
            throw new IOException(left.Length == 0 ? ex.Message : $"{ex.Message} - and the working copy was left half written ({left}): unpack city_univers again", ex);
        }
    }

    // Each file to the bytes given, or away when it had none. Returns what could not be put back ("" when all was).
    private static string PutBack(IReadOnlyDictionary<string, byte[]?> files)
    {
        var left = new List<string>();
        foreach ((string file, byte[]? bytes) in files)
        {
            try
            {
                if (bytes != null) AtomicFile.WriteAllBytes(file, bytes);
                else File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return string.Join("; ", left);
    }
}

/// <summary>One face of a load zone moved: which plane, from where to where on its axis (world), and the
/// volume's world box afterwards.</summary>
public sealed record LoadZoneFaceMove(string Zone, string Face, int Plane, float From, float To, Vector3 BoxMin, Vector3 BoxMax);
