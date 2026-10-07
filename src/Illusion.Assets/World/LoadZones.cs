using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.CityAreas;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;

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
    /// Measured the same way: what makes a district stream in at the point a player appears at is a zone that
    /// names TWO districts (the seams, "AREA341_GREENFIELD_KINGSTONE"). A zone that names one (the district's
    /// own box, "AREA0019_GREENFIELD") did not load it by itself, and a point in no two-district zone keeps
    /// whatever was loaded before - nothing, right after a login.
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
    public static LoadZones Open(Func<FileInfo, string> ensureExtracted, IReadOnlyCollection<string> districts, FileInfo? copy = null)
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
        var zones = new LoadZones(archive, new FrameResource(scenes[0]), scenes[0]);
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
            var known = new HashSet<string>(districts, StringComparer.OrdinalIgnoreCase);
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
            || (theirs.LocalTransform == mine.LocalTransform && theirs.Bounds.Min == mine.Bounds.Min && theirs.Bounds.Max == mine.Bounds.Max
                && (theirs.Planes ?? []).SequenceEqual(mine.Planes ?? []));
    }

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
    public string Save() => SdsWriter.SaveFrameResource(Frame, Archive);
}

/// <summary>One face of a load zone moved: which plane, from where to where on its axis (world), and the
/// volume's world box afterwards.</summary>
public sealed record LoadZoneFaceMove(string Zone, string Face, int Plane, float From, float To, Vector3 BoxMin, Vector3 BoxMax);
