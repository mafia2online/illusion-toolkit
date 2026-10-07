using System.Numerics;
using Illusion.Assets.Sds;
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

    private LoadZones(FileInfo archive, FrameResource frame)
    {
        Archive = archive;
        Frame = frame;
    }

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
        FrameResource frame = ExtractedSds.Load(extracted).FrameResource
            ?? throw new InvalidDataException("city_univers.sds has no scene");
        var zones = new LoadZones(archive, frame);
        foreach (FrameObjectArea area in frame.FrameObjects.Values.OfType<FrameObjectArea>())
        {
            if (area.Name?.ToString() is { Length: > 0 } name) zones._volumes[name] = area;
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
        if (!Matrix4x4.Invert(zone.WorldTransform, out Matrix4x4 toLocal)) return false;
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
        foreach ((string name, FrameObjectArea zone) in _volumes.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            bool inside = Contains(zone, world, out float outsideBy);
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

    /// <summary>Writes the scene into the working copy of <c>city_univers.sds</c>; a Build of that archive
    /// takes it into the game. Returns the file written.</summary>
    public string Save() => SdsWriter.SaveFrameResource(Frame, Archive);
}

/// <summary>One face of a load zone moved: which plane, from where to where on its axis (world), and the
/// volume's world box afterwards.</summary>
public sealed record LoadZoneFaceMove(string Zone, string Face, int Plane, float From, float To, Vector3 BoxMin, Vector3 BoxMax);
