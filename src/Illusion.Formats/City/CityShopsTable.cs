using System.Text;

namespace Illusion.Formats.City;

/// <summary>
/// <c>cityshops.bin</c> read into its rows and written back as it stands: the table of the interiors the game
/// keeps under <c>shops\</c> and stands in the open city - shops, bars, flats, garages.
/// <para>
/// One such archive can stand at several PLACES. Each place is a marker frame in the archive itself (the gun
/// shop's <c>GUNSHOP_translocator_01</c>, <c>_02</c>, ...) and a pair of volumes in the scene of
/// <c>city_univers</c>: inside the first the interior is loaded, outside the wider second it is let go. This
/// table names them: an AREA row per pair (the two volumes and the archive), and per interior a row with its
/// actor file, the entities of that file and its places.
/// </para>
/// <para>
/// Layout: <c>"hstc"</c>, version (8; 9 in Joe's Adventures), area count, byte length of the names, shop count,
/// three more numbers; the names, each ended by a zero and addressed by its offset; the area rows - two
/// zero-ended strings, (version 9: a byte,) a name offset; then the shop rows - a zero-ended name, a name
/// offset (the marker that carries the interior), two zero-ended strings, a 16-bit and two 32-bit numbers, the
/// entity names, and the places: a name offset, two floats (where the map draws it), an icon, a text, (version
/// 9: a byte,) and one 16-bit mark per entity. Of the header's three numbers the first is the number of places
/// in the whole table and the third the number of marks; the second is kept as read.
/// </para>
/// </summary>
public sealed class CityShopsTable
{
    private const uint Magic = 0x63747368;          // "hstc"
    private static readonly Encoding Text = Encoding.Latin1;

    private readonly int _version;
    private readonly int _kept;                      // the header's middle number, whose meaning is not known
    private readonly List<string> _names = [];       // the name buffer, in the order it was read

    /// <summary>A pair of volumes of <c>city_univers</c> and the archive under <c>shops\</c> they load.</summary>
    public sealed class Area
    {
        public required string LoadZone { get; set; }
        public required string UnloadZone { get; set; }
        public required string Archive { get; set; }
        /// <summary>Version 9 only: a byte of the row, kept as read.</summary>
        public byte Pad { get; set; }
    }

    /// <summary>One place an interior stands at.</summary>
    public sealed class Place
    {
        /// <summary>The marker frame of the interior's archive that stands at the place.</summary>
        public required string Marker { get; set; }
        /// <summary>Where the map draws the place.</summary>
        public float MapX { get; set; }
        public float MapY { get; set; }
        public int Icon { get; set; }
        public int TextId { get; set; }
        /// <summary>One number per entity of the interior, in the order of <see cref="Shop.Entities"/>.</summary>
        public short[] Marks { get; set; } = [];
        /// <summary>Version 9 only: a byte of the row, kept as read.</summary>
        public byte Pad { get; set; }
    }

    /// <summary>An interior: its actor file, the entities of that file, and the places it stands at.</summary>
    public sealed class Shop
    {
        public required string Name { get; set; }
        /// <summary>The marker frame the interior itself hangs on.</summary>
        public required string Holder { get; set; }
        public string ActorFile { get; set; } = "";
        public string Description { get; set; } = "";
        public short Unk1 { get; set; }
        public int Unk2 { get; set; }
        public int Unk3 { get; set; }
        public List<string> Entities { get; } = [];
        public List<Place> Places { get; } = [];
    }

    public int Version => _version;
    public List<Area> Areas { get; } = [];
    public List<Shop> Shops { get; } = [];

    private CityShopsTable(int version, int kept)
    {
        _version = version;
        _kept = kept;
    }

    /// <exception cref="FileFormatException">The bytes are not a city shops table.</exception>
    public static CityShopsTable Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 32 || BitConverter.ToUInt32(bytes) != Magic) throw new FileFormatException("not a cityshops.bin (no \"hstc\" at its start)");
        int version = BitConverter.ToInt32(bytes[4..]), areas = BitConverter.ToInt32(bytes[8..]), namesLength = BitConverter.ToInt32(bytes[12..]);
        int shops = BitConverter.ToInt32(bytes[16..]);
        if (version is not (8 or 9)) throw new FileFormatException($"cityshops.bin: version {version} is not one this reads (8, 9)");
        if (areas < 0 || shops < 0 || namesLength < 0 || namesLength > ushort.MaxValue || 32 + namesLength > bytes.Length)
        {
            throw new FileFormatException("cityshops.bin: its header does not fit the file");
        }

        var table = new CityShopsTable(version, BitConverter.ToInt32(bytes[24..]));
        var byOffset = new Dictionary<int, string>();
        int at = 32;
        while (at - 32 < namesLength)
        {
            int offset = at - 32;
            string name = ReadText(bytes, ref at);
            byOffset[offset] = name;
            table._names.Add(name);
        }
        if (at - 32 != namesLength) throw new FileFormatException("cityshops.bin: its names run past the length the header gives");

        string Named(ReadOnlySpan<byte> all, ref int position)
        {
            int key = ReadUInt16(all, ref position);
            return byOffset.TryGetValue(key, out string? name) ? name : throw new FileFormatException($"cityshops.bin: no name starts at offset {key}");
        }

        bool v9 = version == 9;
        for (int i = 0; i < areas; i++)
        {
            string load = ReadText(bytes, ref at), unload = ReadText(bytes, ref at);
            byte pad = v9 ? ReadByte(bytes, ref at) : (byte)0;
            table.Areas.Add(new Area { LoadZone = load, UnloadZone = unload, Archive = Named(bytes, ref at), Pad = pad });
        }
        for (int i = 0; i < shops; i++)
        {
            string name = ReadText(bytes, ref at);
            var shop = new Shop { Name = name, Holder = Named(bytes, ref at) };
            shop.ActorFile = ReadText(bytes, ref at);
            shop.Description = ReadText(bytes, ref at);
            shop.Unk1 = (short)ReadUInt16(bytes, ref at);
            shop.Unk2 = ReadInt32(bytes, ref at);
            shop.Unk3 = ReadInt32(bytes, ref at);
            int entities = ReadInt32(bytes, ref at);
            if (entities < 0 || entities > bytes.Length) throw new FileFormatException($"cityshops.bin: '{name}' counts {entities} entities");
            for (int e = 0; e < entities; e++) shop.Entities.Add(ReadText(bytes, ref at));
            int places = ReadInt32(bytes, ref at);
            if (places < 0 || places > bytes.Length) throw new FileFormatException($"cityshops.bin: '{name}' counts {places} places");
            for (int p = 0; p < places; p++)
            {
                var place = new Place { Marker = Named(bytes, ref at) };
                place.MapX = BitConverter.Int32BitsToSingle(ReadInt32(bytes, ref at));
                place.MapY = BitConverter.Int32BitsToSingle(ReadInt32(bytes, ref at));
                place.Icon = ReadInt32(bytes, ref at);
                place.TextId = ReadInt32(bytes, ref at);
                if (v9) place.Pad = ReadByte(bytes, ref at);
                place.Marks = new short[entities];
                for (int e = 0; e < entities; e++) place.Marks[e] = (short)ReadUInt16(bytes, ref at);
                shop.Places.Add(place);
            }
            table.Shops.Add(shop);
        }
        if (at != bytes.Length) throw new FileFormatException($"cityshops.bin: {bytes.Length - at} byte(s) left over after its rows");
        return table;
    }

    public byte[] ToBytes()
    {
        // The names keep the order they were read in, so an untouched table comes back byte for byte. A name no
        // row uses any more is dropped, a new one goes to the end.
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (Area a in Areas) used.Add(a.Archive);
        foreach (Shop s in Shops)
        {
            used.Add(s.Holder);
            foreach (Place p in s.Places) used.Add(p.Marker);
        }
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        using var names = new MemoryStream();
        foreach (string name in _names.Where(used.Contains).Concat(used.Except(_names).OrderBy(n => n, StringComparer.Ordinal)))
        {
            if (offsets.ContainsKey(name)) continue;
            offsets[name] = (int)names.Length;
            names.Write(Text.GetBytes(name));
            names.WriteByte(0);
        }
        if (names.Length > ushort.MaxValue) throw new InvalidOperationException("cityshops.bin: its names no longer fit the 16-bit offsets that address them");

        bool v9 = _version == 9;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Text, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(_version);
        writer.Write(Areas.Count);
        writer.Write((int)names.Length);
        writer.Write(Shops.Count);
        writer.Write(Shops.Sum(s => s.Places.Count));
        writer.Write(_kept);
        writer.Write(Shops.Sum(s => s.Places.Sum(p => p.Marks.Length)));
        writer.Write(names.ToArray());
        void WriteText(string text)
        {
            writer.Write(Text.GetBytes(text));
            writer.Write((byte)0);
        }
        foreach (Area a in Areas)
        {
            WriteText(a.LoadZone);
            WriteText(a.UnloadZone);
            if (v9) writer.Write(a.Pad);
            writer.Write((ushort)offsets[a.Archive]);
        }
        foreach (Shop s in Shops)
        {
            WriteText(s.Name);
            writer.Write((ushort)offsets[s.Holder]);
            WriteText(s.ActorFile);
            WriteText(s.Description);
            writer.Write(s.Unk1);
            writer.Write(s.Unk2);
            writer.Write(s.Unk3);
            writer.Write(s.Entities.Count);
            foreach (string entity in s.Entities) WriteText(entity);
            writer.Write(s.Places.Count);
            foreach (Place p in s.Places)
            {
                if (p.Marks.Length != s.Entities.Count) throw new InvalidOperationException($"cityshops.bin: place '{p.Marker}' of '{s.Name}' has {p.Marks.Length} marks for {s.Entities.Count} entities");
                writer.Write((ushort)offsets[p.Marker]);
                writer.Write(p.MapX);
                writer.Write(p.MapY);
                writer.Write(p.Icon);
                writer.Write(p.TextId);
                if (v9) writer.Write(p.Pad);
                foreach (short mark in p.Marks) writer.Write(mark);
            }
        }
        writer.Flush();
        return output.ToArray();
    }

    private static string ReadText(ReadOnlySpan<byte> bytes, ref int at)
    {
        int length = at <= bytes.Length ? bytes[at..].IndexOf((byte)0) : -1;
        if (length < 0) throw new FileFormatException("cityshops.bin: a name runs past the end of the file");
        string text = Text.GetString(bytes.Slice(at, length));
        at += length + 1;
        return text;
    }

    private static byte ReadByte(ReadOnlySpan<byte> bytes, ref int at)
    {
        if (at + 1 > bytes.Length) throw new FileFormatException("cityshops.bin: a row runs past the end of the file");
        return bytes[at++];
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, ref int at)
    {
        if (at + 2 > bytes.Length) throw new FileFormatException("cityshops.bin: a row runs past the end of the file");
        ushort value = BitConverter.ToUInt16(bytes[at..]);
        at += 2;
        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, ref int at)
    {
        if (at + 4 > bytes.Length) throw new FileFormatException("cityshops.bin: a row runs past the end of the file");
        int value = BitConverter.ToInt32(bytes[at..]);
        at += 4;
        return value;
    }
}
