using Illusion.Formats.Hashing;

namespace Illusion.Formats.ResourceFormats;

/// <summary>
/// One data table as an archive's working copy holds it: a <c>.tbl</c> file under <c>tables\</c>, which is the
/// Table entry's version as a dword followed by that one table's body. Cells read back as the .NET types the
/// table reader produces (int, uint, float, ulong, bool, string); a row is written back through the same codec,
/// so a table edited here packs like one that was never touched.
/// </summary>
public sealed class GameTable
{
    private readonly TableData _data;

    private GameTable(ushort version, TableData data)
    {
        Version = version;
        _data = data;
    }

    /// <summary>The Table entry's format version the file carries.</summary>
    public ushort Version { get; }

    public int ColumnCount => _data.Columns.Count;

    public int RowCount => _data.Rows.Count;

    /// <summary>The column's cell type, by its format name (Signed32, Flags32, String32, Hash64AndString32…).</summary>
    public string ColumnType(int column) => _data.Columns[column].Type.ToString();

    public object Cell(int row, int column) => _data.Rows[row].Values[column];

    /// <summary>Sets one cell. The value must already be the type the column reads back as.</summary>
    public void SetCell(int row, int column, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Type expected = TableData.GetValueTypeForColumnType(_data.Columns[column].Type);
        if (value.GetType() != expected)
        {
            throw new ArgumentException(
                $"column {column} holds {expected.Name}, not {value.GetType().Name}", nameof(value));
        }
        _data.Rows[row].Values[column] = value;
    }

    /// <summary>Appends a copy of a row and returns the copy's index.</summary>
    public int CopyRow(int row)
    {
        var copy = new TableData.Row();
        copy.Values.AddRange(_data.Rows[row].Values);
        _data.Rows.Add(copy);
        return _data.Rows.Count - 1;
    }

    /// <summary>The first row whose cell in <paramref name="column"/> is the text <paramref name="text"/>,
    /// ignoring case, or -1.</summary>
    public int FindRow(int column, string text)
    {
        for (int i = 0; i < _data.Rows.Count; i++)
        {
            if (_data.Rows[i].Values[column] is string value
                && string.Equals(value, text, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    public static GameTable Load(string path) => Read(File.ReadAllBytes(path));

    public static GameTable Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < sizeof(uint)) throw new FormatException("a .tbl starts with its version dword");
        ushort version = (ushort)BitConverter.ToUInt32(bytes, 0);
        TableData data = TableResource.DecodeSingleTable(version, bytes[sizeof(uint)..]);
        return new GameTable(version, data);
    }

    public byte[] ToBytes()
    {
        byte[] body = TableResource.EncodeSingleTable(Version, _data);
        var bytes = new byte[sizeof(uint) + body.Length];
        BitConverter.TryWriteBytes(bytes, (uint)Version);
        body.CopyTo(bytes, sizeof(uint));
        return bytes;
    }

    /// <summary>The hash a Hash64AndString32 cell stores beside its text — FNV64 of the text as written.</summary>
    public static ulong NameHash(string text) => Fnv64.Hash(text);
}
