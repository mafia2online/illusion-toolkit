using System.Text;

namespace Illusion.Formats.CityAreas;

/// <summary>
/// <c>cityareas.bin</c> read and written back as it stands: the table that says, for each AREA volume of
/// <c>city_univers</c>, which one or two districts the game keeps loaded while the player is inside it.
/// <para>
/// Layout: <c>"ratc"</c>, version (1), entry count, byte length of the district names; the district names, each
/// ended by a zero; then the entries - the volume's name ended by a zero, two 16-bit offsets into the district
/// names (0xFFFF for "none") and one byte. In the shipped file that byte is 1 on every entry that names two
/// districts but one, and 0 on every entry that names a single district.
/// </para>
/// </summary>
public sealed class CityAreasTable
{
    private const uint Magic = 0x63746172;          // "ratc"
    private const ushort None = 0xFFFF;
    private static readonly Encoding Text = Encoding.Latin1;

    private readonly uint _version;
    private byte[] _districts;
    private readonly List<(string Name, ushort Target1, ushort Target2, byte Flag)> _entries = [];

    /// <summary>One line of the table, its districts resolved to their names.</summary>
    public sealed record Entry(string Name, string? Target1, string? Target2, byte Flag);

    private CityAreasTable(uint version, byte[] districts)
    {
        _version = version;
        _districts = districts;
    }

    /// <exception cref="FileFormatException">The bytes are not a city areas table.</exception>
    public static CityAreasTable Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || BitConverter.ToUInt32(bytes) != Magic) throw new FileFormatException("not a cityareas.bin (no \"ratc\" at its start)");
        uint version = BitConverter.ToUInt32(bytes[4..]);
        int count = BitConverter.ToInt32(bytes[8..]), namesLength = BitConverter.ToInt32(bytes[12..]);
        if (count < 0 || namesLength < 0 || namesLength > ushort.MaxValue || 16 + namesLength > bytes.Length)
        {
            throw new FileFormatException("cityareas.bin: its header does not fit the file");
        }

        var table = new CityAreasTable(version, bytes.Slice(16, namesLength).ToArray());
        int at = 16 + namesLength;
        for (int i = 0; i < count; i++)
        {
            int end = at + bytes[at..].IndexOf((byte)0);
            if (end < at || end + 6 > bytes.Length) throw new FileFormatException($"cityareas.bin: entry {i} runs past the end of the file");
            string name = Text.GetString(bytes[at..end]);
            table._entries.Add((name, BitConverter.ToUInt16(bytes[(end + 1)..]), BitConverter.ToUInt16(bytes[(end + 3)..]), bytes[end + 5]));
            at = end + 6;
        }
        if (at != bytes.Length) throw new FileFormatException($"cityareas.bin: {bytes.Length - at} byte(s) left over after its {count} entries");
        return table;
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Text, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(_version);
            writer.Write(_entries.Count);
            writer.Write(_districts.Length);
            writer.Write(_districts);
            foreach ((string name, ushort target1, ushort target2, byte flag) in _entries)
            {
                writer.Write(Text.GetBytes(name));
                writer.Write((byte)0);
                writer.Write(target1);
                writer.Write(target2);
                writer.Write(flag);
            }
        }
        return stream.ToArray();
    }

    /// <summary>The district names the table holds, in its order.</summary>
    public IReadOnlyList<string> Districts
    {
        get
        {
            var names = new List<string>();
            for (int at = 0; at < _districts.Length;)
            {
                int end = Array.IndexOf(_districts, (byte)0, at);
                if (end < 0) end = _districts.Length;
                names.Add(Text.GetString(_districts, at, end - at));
                at = end + 1;
            }
            return names;
        }
    }

    public IReadOnlyList<Entry> Entries => [.. _entries.Select(e => new Entry(e.Name, NameAt(e.Target1), NameAt(e.Target2), e.Flag))];

    public Entry? Find(string name)
    {
        int at = _entries.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        return at < 0 ? null : Entries[at];
    }

    /// <summary>
    /// Adds a line for a volume: the one or two districts it keeps loaded. A district the table has not named
    /// yet is added to its names. The byte is 1 for two districts and 0 for one - what the shipped table has.
    /// </summary>
    /// <returns>Null on success, otherwise why not; nothing is changed on a refusal.</returns>
    public string? Add(string name, string target1, string? target2)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => c is < ' ' or > '~')) return "the zone's name must be plain Latin text";
        if (Find(name) != null) return $"the table already has a line for '{name}'";
        if (string.IsNullOrWhiteSpace(target1)) return "a zone names at least one district";
        foreach (string district in new[] { target1, target2 ?? "" })
        {
            if (district.Any(c => c is < ' ' or > '~')) return $"'{district}' is not a plain district name";
        }
        if (string.Equals(target1, target2, StringComparison.OrdinalIgnoreCase)) return "the two districts are the same one";

        byte[] grown = _districts;
        ushort Offset(string district)
        {
            for (int at = 0; at < grown.Length;)
            {
                int end = Array.IndexOf(grown, (byte)0, at);
                if (end < 0) end = grown.Length;
                if (string.Equals(Text.GetString(grown, at, end - at), district, StringComparison.OrdinalIgnoreCase)) return (ushort)at;
                at = end + 1;
            }
            int offset = grown.Length;
            grown = [.. grown, .. Text.GetBytes(district), 0];
            return (ushort)offset;
        }

        ushort first = Offset(target1);
        ushort second = string.IsNullOrWhiteSpace(target2) ? None : Offset(target2);
        if (grown.Length >= None) return "the table's district names would not fit its 16-bit offsets";
        _districts = grown;
        _entries.Add((name, first, second, (byte)(second == None ? 0 : 1)));
        return null;
    }

    /// <summary>
    /// Takes a volume's line out. A district only that line named is taken out of the names with it when it
    /// stands at their end - where <see cref="Add"/> puts a new one - so a line added and removed leaves the
    /// table as it was. False when the table had none.
    /// </summary>
    public bool Remove(string name)
    {
        int at = _entries.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return false;
        (_, ushort target1, ushort target2, _) = _entries[at];
        _entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        // from the end backwards: of two districts added together, the second stands after the first
        foreach (ushort offset in new[] { target1, target2 }.Where(o => o != None && o < _districts.Length).Distinct().OrderDescending())
        {
            int end = Array.IndexOf(_districts, (byte)0, offset);
            bool last = end == _districts.Length - 1 && (offset == 0 || _districts[offset - 1] == 0);
            if (last && !_entries.Any(e => e.Target1 == offset || e.Target2 == offset)) _districts = _districts[..offset];
        }
        return true;
    }

    private string? NameAt(ushort offset)
    {
        if (offset == None || offset >= _districts.Length) return null;
        int end = Array.IndexOf(_districts, (byte)0, offset);
        if (end < 0) end = _districts.Length;
        return Text.GetString(_districts, offset, end - offset);
    }
}
