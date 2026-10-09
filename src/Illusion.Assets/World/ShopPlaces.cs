using System.Globalization;
using System.Numerics;
using Illusion.Assets.Frames;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.City;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.World;

/// <summary>
/// The places the game's interiors stand at, and one more of them.
/// <para>
/// An interior - a shop, a bar, a flat - is an archive under <c>shops\</c> that the game loads when the player
/// comes near and stands where a MARKER says. One archive can stand at several places (the gun shop at eleven):
/// the archive holds a marker frame per place, the scene of <c>city_univers</c> a pair of volumes per place -
/// inside the first the interior is loaded, outside the wider second it is let go - and
/// <c>cityshops.bin</c> names them all (<see cref="CityShopsTable"/>).
/// </para>
/// <para>
/// So a new place is three things written together: a marker in the interior's archive, two volumes in
/// <c>city_univers</c>, and two rows of the table. Seen in the game (a multiplayer client, a twelfth gun shop
/// where the game has none): the interior loads there and its own shop menu opens.
/// </para>
/// </summary>
public sealed class ShopPlaces
{
    /// <summary>One place of an interior. <paramref name="At"/> and <paramref name="Turn"/> (degrees about the
    /// vertical) are the marker's, known only when the interior's archive was read; the zones are the pair of
    /// volumes that holds the marker; <paramref name="Added"/> is false for a place the game ships with.</summary>
    public sealed record PlaceInfo(string Shop, string Archive, string Marker, Vector3? At, float? Turn, float MapX, float MapY,
        string? LoadZone, string? UnloadZone, bool Added);

    /// <summary>An interior of the table. <paramref name="Archive"/> is its file under <c>shops\</c> without the
    /// extension, or null when there is no such file.</summary>
    public sealed record ShopInfo(string Name, string? Archive, string ActorFile, int Entities, IReadOnlyList<PlaceInfo> Places);

    private readonly Func<FileInfo, string> _ensureExtracted;
    private readonly string _tableFile;
    private readonly CityShopsTable _table;
    private readonly FrameResource _cityScene;
    private FileInfo? _shopArchive;
    private FrameResource? _shopScene;
    private string? _copyFrom;       // the working copy a new interior's own is made from, on Save
    private bool _tableOnly;         // an interior was taken out: only city_univers changed
    private FileInfo? _copySource;   // the archive that working copy belongs to

    /// <summary>The archive the volumes and the table are in.</summary>
    public FileInfo CityArchive { get; }

    /// <summary>The interior's archive a place was added to or taken out of, once one was.</summary>
    public FileInfo? ShopArchive => _shopArchive;

    /// <summary>True when <see cref="ShopArchive"/> is an interior made here that has no archive file yet:
    /// after <see cref="Save"/> its working copy is there to be packed for the first time.</summary>
    public bool ShopArchiveIsNew => _copyFrom != null;

    public CityShopsTable Table => _table;

    /// <summary>The scenes as they stand in memory - for the probe that checks what a change did to them.</summary>
    internal FrameResource CityScene => _cityScene;

    internal FrameResource? ShopScene => _shopScene;

    private ShopPlaces(Func<FileInfo, string> ensureExtracted, FileInfo city, string tableFile, CityShopsTable table, FrameResource cityScene)
    {
        _ensureExtracted = ensureExtracted;
        CityArchive = city;
        _tableFile = tableFile;
        _table = table;
        _cityScene = cityScene;
    }

    /// <summary>Reads the table and the scene of the base <c>city_univers.sds</c> from its working copy.</summary>
    /// <exception cref="FileNotFoundException">The working copy has no table or no scene.</exception>
    public static ShopPlaces Open(Func<FileInfo, string> ensureExtracted)
    {
        ArgumentNullException.ThrowIfNull(ensureExtracted);
        var city = new FileInfo(MafiaEnvironment.CityUniversSds);
        string extracted = ensureExtracted(city);
        string tableFile = Directory.EnumerateFiles(extracted, "cityshops.bin", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException("the working copy of city_univers has no cityshops.bin", Path.Combine(extracted, "cityshops.bin"));
        FrameResource scene = SdsMeshLoader.OpenScene(extracted).FrameResource
            ?? throw new FileNotFoundException("the working copy of city_univers has no scene", extracted);
        return new ShopPlaces(ensureExtracted, city, tableFile, CityShopsTable.Parse(File.ReadAllBytes(tableFile)), scene);
    }

    /// <summary>
    /// The interiors of the table. For the one named by <paramref name="withMarkers"/> its archive is read too,
    /// so that its places come with where their markers stand and which volumes hold them; the others list
    /// their places as the table has them.
    /// </summary>
    public IReadOnlyList<ShopInfo> Shops(string? withMarkers = null)
    {
        var list = new List<ShopInfo>(_table.Shops.Count);
        foreach (CityShopsTable.Shop shop in _table.Shops)
        {
            string archive = ArchiveOf(shop);
            bool exists = ArchiveFile(archive).Exists;
            FrameResource? scene = exists && withMarkers != null && Is(shop, withMarkers) ? SceneOf(archive) : null;
            var places = new List<PlaceInfo>(shop.Places.Count);
            foreach (CityShopsTable.Place place in shop.Places) places.Add(Describe(shop, archive, place, scene));
            list.Add(new ShopInfo(shop.Name, exists ? archive : null, shop.ActorFile, shop.Entities.Count, places));
        }
        return list;
    }

    /// <summary>
    /// Adds a place for an interior, IN MEMORY: a marker in its archive at <paramref name="at"/> - the point the
    /// interior's own origin goes to - turned by <paramref name="turnDegrees"/> about the vertical, a pair of
    /// box volumes round it in <c>city_univers</c>, and the rows of the table. <see cref="Save"/> writes it.
    /// </summary>
    /// <param name="loadHalf">Half the side of the box inside which the interior is loaded, metres.</param>
    /// <param name="unloadHalf">Half the side of the wider box outside which it is let go.</param>
    /// <param name="halfHeight">Half the height of both boxes.</param>
    /// <returns>Null on success, otherwise why not; nothing is changed on a refusal.</returns>
    public string? Add(string shop, Vector3 at, float turnDegrees, float loadHalf, float unloadHalf, float halfHeight, out PlaceInfo? made)
    {
        made = null;
        if (Sizes(at, turnDegrees, loadHalf, unloadHalf, halfHeight) is { } bad) return bad;

        CityShopsTable.Shop? row = _table.Shops.FirstOrDefault(s => Is(s, shop));
        if (row == null) return $"the table has no interior named '{shop}'; it has: {string.Join(", ", _table.Shops.Select(s => s.Name))}";
        if (row.Places.Count == 0) return $"{row.Name} has no place yet to make the new one like";
        return AddPlace(row, row.Places[0], at, turnDegrees, loadHalf, unloadHalf, halfHeight, out made);
    }

    private static string? Sizes(Vector3 at, float turnDegrees, float loadHalf, float unloadHalf, float halfHeight)
    {
        foreach (float v in new[] { at.X, at.Y, at.Z, turnDegrees, loadHalf, unloadHalf, halfHeight })
        {
            if (!float.IsFinite(v)) return "the place, the turn and the sizes must be finite numbers";
        }
        if (loadHalf < 5f || halfHeight < 3f) return "the load box must be at least 10 m wide and 6 m high";
        return unloadHalf <= loadHalf
            ? "the unload box must be wider than the load box - it is what the player has to leave for the interior to be let go"
            : null;
    }

    // The place itself. `first` is the place whose map icon, text and per-entity marks the new one takes.
    private string? AddPlace(CityShopsTable.Shop row, CityShopsTable.Place first, Vector3 at, float turnDegrees, float loadHalf, float unloadHalf,
        float halfHeight, out PlaceInfo? made)
    {
        made = null;
        string archive = ArchiveOf(row);
        if (Hold(archive) is { } notHeld) return notHeld;
        FrameResource scene = _shopScene!;

        // the marker to copy: one of the interior's own places - an empty frame of the scene - or, when the
        // interior has only the marker that carries it, that one (a copy takes none of what hangs on it)
        FrameObjectBase? holder = Find(scene, row.Holder);
        List<FrameObjectBase> markers = [.. row.Places.Select(p => Find(scene, p.Marker)).OfType<FrameObjectBase>()];
        FrameObjectBase? pattern = markers.FirstOrDefault(m => m.Parent == null && m.Children.Count == 0) ?? (holder is { Parent: null } ? holder : null);
        if (pattern is not FrameObjectDummy like) return $"the archive of {row.Name} has no marker of its own to copy (a frame of the scene named as the table names its places)";
        string marker = NextMarker(scene, row, like.Name.String ?? row.Holder);

        // the pair of volumes to copy: this interior's own when they stand square to the map, else any pair that does
        CityShopsTable.Area? likeRow = _table.Areas.Where(a => Same(a.Archive, archive)).FirstOrDefault(Square)
            ?? _table.Areas.FirstOrDefault(Square);
        if (likeRow == null) return "city_univers has no pair of volumes standing square to the map to copy";
        var likeLoad = (FrameObjectArea)Find(_cityScene, likeRow.LoadZone)!;
        var likeUnload = (FrameObjectArea)Find(_cityScene, likeRow.UnloadZone)!;
        // named after this interior as its own volumes are, where it has some; else after the interior itself
        (string loadName, string unloadName) = NextZones(Same(likeRow.Archive, archive) ? likeRow.LoadZone : "", row.Name);
        foreach (FrameObjectArea source in new[] { likeLoad, likeUnload })
        {
            Matrix4x4 parent = (source.Parent ?? source.Root)?.WorldTransform ?? Matrix4x4.Identity;
            parent.Translation = Vector3.Zero;
            if (!Matrix4x4.Invert(Whole(parent), out _)) return $"{source.Name} hangs on a frame whose transform cannot be inverted";
        }

        // ---- everything can be done: the marker
        var clone = new FrameObjectDummy(like) { Name = new HashName(marker) };
        scene.FrameObjects.Add(clone.RefID, clone);
        FrameDuplicator.LinkParents(clone, FrameDuplicator.ResolveRef(scene, like, FrameEntryRefTypes.Parent1), FrameDuplicator.ResolveRef(scene, like, FrameEntryRefTypes.Parent2));
        // it stands as the marker carrying the interior stands, turned about the vertical, at the new place
        Matrix4x4 stand = Whole((holder ?? like).WorldTransform);
        stand.Translation = Vector3.Zero;
        Matrix4x4 turned = stand * Matrix4x4.CreateRotationZ(turnDegrees * MathF.PI / 180f);
        Matrix4x4 local = like.LocalTransform;      // the fourth column as this scene stores it
        local.M11 = turned.M11; local.M12 = turned.M12; local.M13 = turned.M13;
        local.M21 = turned.M21; local.M22 = turned.M22; local.M23 = turned.M23;
        local.M31 = turned.M31; local.M32 = turned.M32; local.M33 = turned.M33;
        local.Translation = at;
        clone.LocalTransform = local;

        // ---- the two volumes
        AddVolume(likeLoad, loadName, at, new Vector3(loadHalf, loadHalf, halfHeight));
        AddVolume(likeUnload, unloadName, at, new Vector3(unloadHalf, unloadHalf, halfHeight));

        // ---- the rows
        var place = new CityShopsTable.Place
        {
            Marker = marker, MapX = at.X, MapY = at.Y, Icon = first.Icon, TextId = first.TextId, Marks = [.. first.Marks], Pad = first.Pad,
        };
        row.Places.Add(place);
        _table.Areas.Add(new CityShopsTable.Area { LoadZone = loadName, UnloadZone = unloadName, Archive = archive, Pad = likeRow.Pad });
        made = new PlaceInfo(row.Name, archive, marker, at, turnDegrees, at.X, at.Y, loadName, unloadName, true);
        return null;
    }

    /// <summary>
    /// Takes a place that was ADDED out again, IN MEMORY: its marker, its pair of volumes and its rows.
    /// A place the game ships with is refused. <see cref="Save"/> writes it.
    /// </summary>
    /// <returns>Null on success, otherwise why not; nothing is changed on a refusal.</returns>
    public string? Remove(string marker, out PlaceInfo? removed)
    {
        removed = null;
        CityShopsTable.Shop? row = _table.Shops.FirstOrDefault(s => s.Places.Any(p => Same(p.Marker, marker)));
        if (row == null) return $"no place of the table has the marker '{marker}'";
        CityShopsTable.Place place = row.Places.First(p => Same(p.Marker, marker));
        if (row.Places.Count == 1) return $"it is the only place of {row.Name}";
        string archive = ArchiveOf(row);
        if (Hold(archive) is { } notHeld) return notHeld;
        FrameResource scene = _shopScene!;

        if (Find(scene, place.Marker) is not { } frame) return $"the archive of {row.Name} has no frame named '{place.Marker}'";
        if (frame.Children.Count > 0 || scene.FrameObjects.Values.OfType<FrameObjectBase>().Any(o => ReferenceEquals(o.Parent, frame) || ReferenceEquals(o.Root, frame)))
            return $"other frames hang on {place.Marker} - it carries the interior itself";
        PlaceInfo info = Describe(row, archive, place, scene);
        if (info.LoadZone == null || info.UnloadZone == null) return $"no pair of volumes of the table holds {place.Marker} - it is not a place that was added here";
        if (!info.Added) return $"{place.Marker} came with the game ({info.LoadZone}) - only a place that was added is taken out";
        CityShopsTable.Area area = _table.Areas.First(a => Same(a.LoadZone, info.LoadZone) && Same(a.UnloadZone, info.UnloadZone));

        Unlink(scene, frame);
        foreach (string zone in new[] { area.LoadZone, area.UnloadZone })
        {
            // a volume another row still names stays (two of the game's own load boxes share one unload box)
            if (_table.Areas.Any(a => !ReferenceEquals(a, area) && (Same(a.LoadZone, zone) || Same(a.UnloadZone, zone)))) continue;
            if (Find(_cityScene, zone) is { } volume) Unlink(_cityScene, volume);
        }
        _table.Areas.Remove(area);
        row.Places.Remove(place);
        removed = info;
        return null;
    }

    /// <summary>
    /// Makes an interior OF ONE'S OWN, IN MEMORY: a copy of one of the table's interiors under a new name, with
    /// its first place at <paramref name="at"/>. The copy is an archive of its own - <c>shops\&lt;name&gt;.sds</c>,
    /// made from the working copy of <paramref name="like"/> - whose marker frames bear the new name, and a row
    /// of its own in the table with the entities of the one it was copied from. What stands in it is then
    /// changed like any archive. <see cref="Save"/> writes the working copies; the new archive is packed after.
    /// </summary>
    /// <returns>Null on success, otherwise why not; nothing is changed on a refusal.</returns>
    public string? Create(string name, string like, Vector3 at, float turnDegrees, float loadHalf, float unloadHalf, float halfHeight, out PlaceInfo? made)
    {
        made = null;
        if (Sizes(at, turnDegrees, loadHalf, unloadHalf, halfHeight) is { } bad) return bad;
        if (string.IsNullOrWhiteSpace(name) || name.Length is < 3 or > 31 || !char.IsAsciiLetterLower(name[0])
            || name.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')))
        {
            return "an interior's name is 3 to 31 lower-case letters, digits and '_', starting with a letter - it is its archive's name too";
        }
        if (_shopArchive != null) return $"a place of {Path.GetFileNameWithoutExtension(_shopArchive.Name)} is being changed - save it first";
        if (_table.Shops.Any(s => Is(s, name)) || _table.Areas.Any(a => Same(a.Archive, name))) return $"the table already has an interior named '{name}'";
        FileInfo archive = ArchiveFile(name);
        if (archive.Exists) return $"there is already a shops\\{name}.sds - pick another name";
        string folder = MafiaEnvironment.ExtractedDir(archive);
        if (Directory.Exists(folder)) return $"there is already a working copy at {folder}, though shops\\{name}.sds is not there - it may hold work that was never built; remove or rename it, or pick another name";
        CityShopsTable.Shop? source = _table.Shops.FirstOrDefault(s => Is(s, like));
        if (source == null) return $"the table has no interior named '{like}' to copy";
        if (source.Places.Count == 0) return $"{source.Name} has no place to make the new one like";
        FileInfo sourceArchive = ArchiveFile(ArchiveOf(source));
        if (!sourceArchive.Exists) return $"there is no shops\\{ArchiveOf(source)}.sds to copy";

        string from = _ensureExtracted(sourceArchive);
        if (SdsMeshLoader.OpenScene(from).FrameResource is not { } scene) return $"{sourceArchive.Name} has no scene";
        if (Find(scene, source.Holder) is not { } holder) return $"{sourceArchive.Name} has no frame named '{source.Holder}', which the table says carries the interior";
        string stem = name.ToUpperInvariant() + "_translocator_";
        if (scene.FrameObjects.Values.OfType<FrameObjectBase>().Any(f => (f.Name.String ?? "").StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
            return $"{sourceArchive.Name} already has frames named {stem}...";

        // ---- everything can be done; from here the scene in memory is the new interior's
        holder.Name = new HashName(stem + "00");
        // the markers of the places the copied interior stands at are not this one's places
        foreach (CityShopsTable.Place place in source.Places)
        {
            if (Find(scene, place.Marker) is { Parent: null, Children.Count: 0 } marker && !ReferenceEquals(marker, holder)) Unlink(scene, marker);
        }
        var row = new CityShopsTable.Shop
        {
            Name = name, Holder = stem + "00", ActorFile = source.ActorFile, Description = source.Description,
            Unk1 = source.Unk1, Unk2 = source.Unk2, Unk3 = source.Unk3,
        };
        row.Entities.AddRange(source.Entities);
        _table.Shops.Add(row);
        _shopArchive = archive;
        _shopScene = scene;
        _copyFrom = from;
        _copySource = sourceArchive;
        if (AddPlace(row, source.Places[0], at, turnDegrees, loadHalf, unloadHalf, halfHeight, out made) is { } refused)
        {
            // nothing was written; this object is not to be saved
            _table.Shops.Remove(row);
            _shopArchive = null;
            _shopScene = null;
            _copyFrom = null;
            _copySource = null;
            return refused;
        }
        return null;
    }

    /// <summary>
    /// Takes an interior that was ADDED out of the table again, IN MEMORY: its row, its area rows and their
    /// volumes. Its archive and working copy are left where they are - without the rows the game never asks for
    /// them. An interior the game ships with is refused. <see cref="Save"/> writes it.
    /// </summary>
    /// <returns>Null on success, otherwise why not; nothing is changed on a refusal.</returns>
    public string? Delete(string name, out ShopInfo? removed)
    {
        removed = null;
        CityShopsTable.Shop? row = _table.Shops.FirstOrDefault(s => Is(s, name));
        if (row == null) return $"the table has no interior named '{name}'";
        if (IsShipped(row.Name)) return $"{row.Name} came with the game - only an interior that was added is taken out";
        if (_shopArchive != null) return $"a place of {Path.GetFileNameWithoutExtension(_shopArchive.Name)} is being changed - save it first";
        string archive = ArchiveOf(row);
        List<CityShopsTable.Area> areas = [.. _table.Areas.Where(a => Same(a.Archive, archive))];
        if (areas.Any(a => LoadZones.IsShipped(a.LoadZone) || LoadZones.IsShipped(a.UnloadZone))) return $"{row.Name} is loaded by volumes the game ships with";
        removed = new ShopInfo(row.Name, ArchiveFile(archive).Exists ? archive : null, row.ActorFile, row.Entities.Count,
            [.. row.Places.Select(p => Describe(row, archive, p, null))]);
        foreach (CityShopsTable.Area area in areas)
        {
            _table.Areas.Remove(area);
            foreach (string zone in new[] { area.LoadZone, area.UnloadZone })
            {
                if (_table.Areas.Any(a => Same(a.LoadZone, zone) || Same(a.UnloadZone, zone))) continue;
                if (Find(_cityScene, zone) is { } volume) Unlink(_cityScene, volume);
            }
        }
        _table.Shops.Remove(row);
        _tableOnly = true;
        return null;
    }

    /// <summary>Whether an interior of this name is one the game ships with (the main game's and Joe's Adventures').</summary>
    public static bool IsShipped(string? name) =>
        // without the list nothing can be told to be added - and what cannot be told is not taken out
        name != null && (Shipped.Value.Count == 0 || Shipped.Value.Contains(name));

    private static readonly Lazy<HashSet<string>> Shipped = new(() =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        System.Reflection.Assembly assembly = typeof(ShopPlaces).Assembly;
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("ShippedInteriors.txt", StringComparison.Ordinal));
        if (resource == null) return names;
        using Stream? stream = assembly.GetManifestResourceStream(resource);
        if (stream == null) return names;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim() is { Length: > 0 } name && name[0] != '#') names.Add(name);
        }
        return names;
    });

    /// <summary>
    /// Writes what <see cref="Add"/> and <see cref="Remove"/> changed into the working copies: the interior's
    /// scene and name table, the scene and name table of <c>city_univers</c>, and the table - all of them, or,
    /// when one cannot be written, none (the ones already swapped in are put back). A Build of both archives
    /// takes it into the game. Returns the files written.
    /// </summary>
    /// <exception cref="IOException">A file could not be written; the message says what was left changed, if anything.</exception>
    public IReadOnlyList<string> Save()
    {
        if ((_shopArchive == null || _shopScene == null) && !_tableOnly) return [];
        string cityDir = MafiaEnvironment.ExtractedDir(CityArchive);
        string? shopDir = _shopArchive == null ? null : MafiaEnvironment.ExtractedDir(_shopArchive);
        bool madeFolder = false;
        if (_copyFrom != null && shopDir != null)
        {
            // a new interior: its working copy is the copied one's, made now - and taken away again if the rest fails
            if (Directory.Exists(shopDir)) throw new IOException($"there is already a folder at {shopDir}");
            // the copy asks the engine for the memory its source asks: written beside the source, copied with it
            if (_copySource != null) SdsWriter.EnsureMemoryRequirements(_copySource);
            CopyFolder(_copyFrom, shopDir);
            madeFolder = true;
        }
        List<string> files =
        [
            .. shopDir == null || madeFolder ? [] : SdsManifest.Load(shopDir).GetFiles("FrameResource").Take(1),
            .. shopDir == null || madeFolder ? [] : SdsManifest.Load(shopDir).GetFiles("FrameNameTable").Take(1),
            .. SdsManifest.Load(cityDir).GetFiles("FrameResource").Take(1), .. SdsManifest.Load(cityDir).GetFiles("FrameNameTable").Take(1),
            _tableFile,
        ];
        Dictionary<string, byte[]?> before = files.ToDictionary(f => f, f => File.Exists(f) ? File.ReadAllBytes(f) : null, StringComparer.OrdinalIgnoreCase);
        var written = new List<string>();
        try
        {
            // the table first: it is the one that can refuse (names past the reach of their offsets)
            byte[] table = _table.ToBytes();
            if (_shopArchive != null && _shopScene != null)
            {
                written.Add(SdsWriter.SaveFrameResource(_shopScene, _shopArchive));
                if (SdsWriter.SaveFrameNameTable(_shopScene, _shopArchive) is { } shopNames) written.Add(shopNames);
            }
            written.Add(SdsWriter.SaveFrameResource(_cityScene, CityArchive));
            if (SdsWriter.SaveFrameNameTable(_cityScene, CityArchive) is { } cityNames) written.Add(cityNames);
            AtomicFile.WriteAllBytes(_tableFile, table);
            written.Add(_tableFile);
            return written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            var left = new List<string>();
            if (madeFolder)
            {
                // the new interior's folder was made by this save and holds nothing else
                try
                {
                    Directory.Delete(shopDir!, recursive: true);
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                    left.Add(shopDir!);
                }
            }
            foreach (string file in written.Where(f => !madeFolder || !f.StartsWith(shopDir!, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    if (before.GetValueOrDefault(file) is { } bytes) AtomicFile.WriteAllBytes(file, bytes);
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                    left.Add(Path.GetFileName(file));
                }
            }
            if (left.Count == 0) throw;
            throw new IOException($"{ex.Message} - and the working copies could not be put back as they were ({string.Join(", ", left)}): unpack the archives again", ex);
        }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }
    }

    // ---- the table's words

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private bool Is(CityShopsTable.Shop shop, string asked) => Same(shop.Name, asked) || Same(ArchiveOf(shop), asked);

    // An interior's archive is named by the area rows, in their own (lower case) spelling of its name; an
    // interior no area row names (one the story loads another way) has the file of its own name, if any.
    private string ArchiveOf(CityShopsTable.Shop shop) =>
        _table.Areas.FirstOrDefault(a => Same(a.Archive, shop.Name))?.Archive ?? shop.Name.ToLowerInvariant();

    private static FileInfo ArchiveFile(string archive) => new(Path.Combine(MafiaEnvironment.PcFolder, "sds", "shops", archive + ".sds"));

    private FrameResource? SceneOf(string archive)
    {
        if (_shopArchive != null && _shopScene != null && Same(Path.GetFileNameWithoutExtension(_shopArchive.Name), archive)) return _shopScene;
        return SdsMeshLoader.OpenScene(_ensureExtracted(ArchiveFile(archive))).FrameResource;
    }

    // One interior's archive is changed at a time: its scene is read once and every change is made in that copy.
    private string? Hold(string archive)
    {
        FileInfo file = ArchiveFile(archive);
        if (_shopArchive != null)
        {
            return Same(_shopArchive.FullName, file.FullName) ? null : $"a place of {Path.GetFileNameWithoutExtension(_shopArchive.Name)} is being changed - save it first";
        }
        if (!file.Exists) return $"there is no shops\\{archive}.sds";
        if (SdsMeshLoader.OpenScene(_ensureExtracted(file)).FrameResource is not { } scene) return $"shops\\{archive}.sds has no scene";
        _shopArchive = file;
        _shopScene = scene;
        return null;
    }

    private PlaceInfo Describe(CityShopsTable.Shop shop, string archive, CityShopsTable.Place place, FrameResource? scene)
    {
        Vector3? at = null;
        float? turn = null;
        CityShopsTable.Area? area = null;
        if (scene != null && Find(scene, place.Marker) is { } frame)
        {
            Matrix4x4 world = frame.WorldTransform;
            at = world.Translation;
            turn = MathF.Atan2(world.M12, world.M11) * 180f / MathF.PI;
            area = _table.Areas.FirstOrDefault(a => Same(a.Archive, archive) && Find(_cityScene, a.LoadZone) is FrameObjectArea volume
                && LoadZones.Contains(volume, world.Translation, out _));
        }
        bool added = area != null && !LoadZones.IsShipped(area.LoadZone);
        return new PlaceInfo(shop.Name, archive, place.Marker, at, turn, place.MapX, place.MapY, area?.LoadZone, area?.UnloadZone, added);
    }

    // ---- the scenes

    private static FrameObjectBase? Find(FrameResource scene, string name) =>
        scene.FrameObjects.Values.OfType<FrameObjectBase>().FirstOrDefault(f => Same(f.Name.String, name));

    // A frame matrix is kept as three columns; for arithmetic it is a whole matrix.
    private static Matrix4x4 Whole(Matrix4x4 m)
    {
        m.M14 = 0f;
        m.M24 = 0f;
        m.M34 = 0f;
        m.M44 = 1f;
        return m;
    }

    private bool Square(CityShopsTable.Area row)
    {
        foreach (string name in new[] { row.LoadZone, row.UnloadZone })
        {
            if (Find(_cityScene, name) is not FrameObjectArea volume) return false;
            Matrix4x4 world = volume.WorldTransform;
            if (MathF.Abs(world.M11 - 1f) > 1e-3f || MathF.Abs(world.M22 - 1f) > 1e-3f || MathF.Abs(world.M33 - 1f) > 1e-3f) return false;
        }
        return true;
    }

    // The next marker of an interior: the name its markers share, with the number after the highest in use.
    private string NextMarker(FrameResource scene, CityShopsTable.Shop shop, string like)
    {
        int digits = 0;
        while (digits < like.Length && char.IsAsciiDigit(like[like.Length - 1 - digits])) digits++;
        string stem = digits > 0 ? like[..^digits] : like + "_";
        int width = Math.Max(digits, 2), highest = -1;
        IEnumerable<string> names = scene.FrameObjects.Values.OfType<FrameObjectBase>().Select(f => f.Name.String ?? "")
            .Concat(shop.Places.Select(p => p.Marker)).Append(shop.Holder);
        foreach (string name in names)
        {
            if (name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name.AsSpan(stem.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                highest = Math.Max(highest, number);
            }
        }
        return stem + (highest + 1).ToString("D" + width, CultureInfo.InvariantCulture);
    }

    // The next pair of volume names, written as the game writes them: AREA<n>_1_<Name> and AREA<n+1>_0_<Name>,
    // with numbers no object of the scene has - from 900 up, clear of the game's own.
    private (string Load, string Unload) NextZones(string like, string shop)
    {
        int cut = like.IndexOf("_1_", StringComparison.Ordinal);
        string word = cut >= 0 && cut + 3 < like.Length ? like[(cut + 3)..] : shop;
        HashSet<string> taken = new(_cityScene.FrameObjects.Values.OfType<FrameObjectBase>().Select(f => f.Name.String ?? ""), StringComparer.OrdinalIgnoreCase);
        bool Free(int n) => !taken.Any(name => name.StartsWith("AREA" + n.ToString(CultureInfo.InvariantCulture) + "_", StringComparison.OrdinalIgnoreCase));
        int number = 900;
        while (!Free(number) || !Free(number + 1)) number += 2;
        return ($"AREA{number}_1_{word}", $"AREA{number + 1}_0_{word}");
    }

    // A box volume made like an existing one - its flags, its parent, its place in the name table - with a
    // name, a centre and a size of its own (as LoadZones.Create makes a load zone).
    private void AddVolume(FrameObjectArea source, string name, Vector3 centre, Vector3 half)
    {
        Matrix4x4 world = source.WorldTransform;
        Matrix4x4 parent = (source.Parent ?? source.Root)?.WorldTransform ?? Matrix4x4.Identity;
        parent.Translation = Vector3.Zero;
        Matrix4x4.Invert(Whole(parent), out Matrix4x4 unTurn);

        var volume = new FrameObjectArea(source) { Name = new HashName(name) };
        var box = volume.Bounds;
        box.Min = -half;
        box.Max = half;
        volume.Bounds = box;
        // inside is n.p + d >= 0 in the volume's own space
        volume.Planes =
        [
            new Vector4(-1, 0, 0, half.X), new Vector4(1, 0, 0, half.X),
            new Vector4(0, -1, 0, half.Y), new Vector4(0, 1, 0, half.Y),
            new Vector4(0, 0, -1, half.Z), new Vector4(0, 0, 1, half.Z),
        ];
        volume.PlaneSize = volume.Planes.Length;
        _cityScene.FrameObjects.Add(volume.RefID, volume);
        FrameDuplicator.LinkParents(volume, FrameDuplicator.ResolveRef(_cityScene, source, FrameEntryRefTypes.Parent1), FrameDuplicator.ResolveRef(_cityScene, source, FrameEntryRefTypes.Parent2));
        Matrix4x4 local = source.LocalTransform;
        local.Translation += Vector3.TransformNormal(centre - world.Translation, unTurn);
        volume.LocalTransform = local;
    }

    private static void Unlink(FrameResource scene, FrameObjectBase frame)
    {
        frame.SetParent(ParentInfo.ParentType.ParentIndex1, null);
        frame.SetParent(ParentInfo.ParentType.ParentIndex2, null);
        foreach (FrameHeaderScene folder in scene.FrameScenes.Values) folder.Children.Remove(frame);
        scene.FrameObjects.Remove(frame.RefID);
    }
}
