using System.Globalization;
using System.Text;

namespace Illusion.Assets.Text;

/// <summary>
/// The game's text table as an archive's working copy holds it (<c>tables\TextDatabase.dat</c> of
/// <c>sds_&lt;language&gt;\text\text_default.sds</c>): UTF-8 with a byte-order mark, one CRLF-terminated
/// <c>KEY:TEXT</c> line per string. A string's numeric id is its key without the separators — id 60000017 is the
/// line <c>00_60_00_0017:Shubert 38</c>, which is how <c>vehicles.tbl</c> names a car.
/// </summary>
public static class GameText
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>The key a numeric id is filed under: ten digits in groups of 2, 2, 2 and 4.</summary>
    public static string Key(int id)
    {
        string digits = id.ToString("D10", CultureInfo.InvariantCulture);
        return $"{digits[..2]}_{digits[2..4]}_{digits[4..6]}_{digits[6..]}";
    }

    /// <summary>The text filed under <paramref name="id"/>, or null when the table has no such line.</summary>
    public static string? Find(string path, int id)
    {
        string prefix = Key(id) + ":";
        foreach (string line in Lines(path, out _))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..];
        }
        return null;
    }

    /// <summary>
    /// Adds one string. The line goes after the last line of its own block (the keys sharing everything but
    /// the final group) that sorts before it, so the table keeps the order it ships in; with no such block it
    /// goes at the end.
    /// </summary>
    /// <returns>False when the id is already taken — the table is left as it was.</returns>
    public static bool Add(string path, int id, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\r') || text.Contains('\n')) throw new ArgumentException("a string is one line", nameof(text));

        List<string> lines = Lines(path, out bool hadBom);
        string key = Key(id);
        if (lines.Any(l => l.StartsWith(key + ":", StringComparison.Ordinal))) return false;

        string block = key[..9];
        int at = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length > key.Length && lines[i].StartsWith(block, StringComparison.Ordinal)
                && string.CompareOrdinal(lines[i][..key.Length], key) < 0)
            {
                at = i;
            }
        }
        lines.Insert(at < 0 ? lines.Count : at + 1, $"{key}:{text}");

        var bytes = new List<byte>(hadBom ? Bom : []);
        foreach (string line in lines) bytes.AddRange(Utf8.GetBytes(line + "\r\n"));
        AtomicFile.WriteAllBytes(path, [.. bytes]);
        return true;
    }

    // The table's lines without their terminators. A final CRLF does not make an empty last line.
    private static List<string> Lines(string path, out bool hadBom)
    {
        byte[] bytes = File.ReadAllBytes(path);
        hadBom = bytes.AsSpan().StartsWith(Bom);
        string text = Utf8.GetString(bytes, hadBom ? Bom.Length : 0, bytes.Length - (hadBom ? Bom.Length : 0));
        var lines = new List<string>(text.Split("\r\n"));
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
