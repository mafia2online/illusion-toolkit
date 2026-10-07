using System.Text;

namespace Illusion.Formats.StreamMap;

/// <summary>Which string fields a <see cref="StreamMapEditor"/> pass is allowed to touch.</summary>
[Flags]
public enum StreamMapFields
{
    None = 0,
    /// <summary>A loader's asset path, e.g. <c>/sds/city/eastside.sds</c>.</summary>
    Path = 1,
    /// <summary>A loader's instance name, e.g. <c>City-1</c>.</summary>
    Entity = 2,
    /// <summary>A line's name — the streaming state the game's Lua switches on.</summary>
    LineName = 4,
    /// <summary>A line-group header name.</summary>
    GroupName = 8,
    All = Path | Entity | LineName | GroupName,
}

/// <summary>One string the editor looked at, and what became of it.</summary>
/// <param name="Field">Which kind of field it was.</param>
/// <param name="PoolOffset">Where the string starts, relative to the string pool.</param>
/// <param name="Before">The value as found.</param>
/// <param name="After">The value the replacement would produce.</param>
/// <param name="Applied">True only when the bytes were actually written to the patched buffer.
/// False both for a refused change and for every change in a dry run, so it cannot be read on its
/// own as "this was rejected" — <paramref name="Refused"/> is what distinguishes the two.</param>
/// <param name="Refused">Why the change could not be made, or null when it could. Null with
/// <paramref name="Applied"/> false means the edit is fine and simply was not written, because the
/// pass was a dry run.</param>
public sealed record StreamMapEdit(
    StreamMapFields Field,
    int PoolOffset,
    string Before,
    string After,
    bool Applied,
    string? Refused);

/// <summary>What a replacement pass produced.</summary>
/// <param name="Edits">Every string that matched, applied or refused.</param>
/// <param name="Patched">The rewritten file, or null for a dry run.</param>
public sealed record StreamMapPatch(IReadOnlyList<StreamMapEdit> Edits, byte[]? Patched);

/// <summary>
/// Find/replace across the string fields of a <c>StreamMap*.bin</c> — how a mod re-points the game
/// at its own archives instead of the stock ones.
/// <para>
/// <b>The edit is made in place, inside the existing string pool, and never relocates anything.</b>
/// That is not a shortcut, it is the only version of this that can be shown to be correct. The file
/// is a header, four arrays and a trailing string pool, and the records reference their strings by
/// offset into that pool — but the reader this library ships models only three of the arrays. Two
/// more sit between the loaders and the pool (113 and 1626 eight-byte entries in the retail file),
/// and nothing here knows whether they hold pool offsets too. Rebuilding the file would have to
/// place them correctly; rewriting a string where it already lies does not have to know at all,
/// because every offset in the file — modelled or not — still points exactly where it did.
/// </para>
/// <para>
/// The cost of that guarantee is the one rule this class enforces: a replacement must fit in the
/// bytes the original occupied. A shorter value is written and the remainder zero-filled, which is
/// what the NUL terminator would have done anyway. A longer one is refused, individually and by
/// name, rather than silently skipped or made to overflow into the next string.
/// </para>
/// </summary>
public static class StreamMapEditor
{
    // Header field offsets, as the native reader indexes them. The two arrays at 48/52 and 56/60 are
    // deliberately not touched — see the type remarks.
    private const int OffsetTotalSize = 8;
    private const int OffsetGroupCount = 24;
    private const int OffsetGroupArray = 28;
    private const int OffsetLineCount = 32;
    private const int OffsetLineArray = 36;
    private const int OffsetLoaderCount = 40;
    private const int OffsetLoaderArray = 44;
    private const int OffsetPoolStart = 68;

    private const int GroupRecordSize = 8;
    private const int LineRecordSize = 56;
    private const int LoaderRecordSize = 32;
    private const int LineNameField = 0;
    private const int LoaderPathField = 24;
    private const int LoaderEntityField = 28;

    /// <summary>
    /// Latin-1, matching the native reader byte for byte. Windows-1252 would agree on everything a
    /// game path actually contains but differs in 0x80–0x9F, and a mismatch there would rewrite a
    /// string the caller never asked to change.
    /// </summary>
    private static readonly Encoding PoolEncoding = Encoding.Latin1;

    /// <summary>
    /// Writes a patched StreamMap over <paramref name="filePath"/>, after putting what stood there beside it
    /// as a backup, and returns the backup's path. The backup goes down first.
    /// <para>
    /// It never overwrites an earlier one. The name is derived from the target, so it is the same on every
    /// edit: a second pass over the same file would have replaced the pristine backup with the
    /// already-patched file, leaving two copies of modified data and no way back. The first backup is the
    /// valuable one, so it is kept as <c>&lt;name&gt;_old.bin</c> and later passes get <c>_old.2.bin</c>,
    /// <c>_old.3.bin</c> and so on.
    /// </para>
    /// </summary>
    public static string WriteWithBackup(string filePath, byte[] original, byte[] patched)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(patched);
        string directory = Path.GetDirectoryName(filePath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(filePath) + "_old";
        string extension = Path.GetExtension(filePath);

        string backup = Path.Combine(directory, stem + extension);
        for (int n = 2; File.Exists(backup); n++)
        {
            backup = Path.Combine(directory, stem + "." + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + extension);
        }
        File.WriteAllBytes(backup, original);
        File.WriteAllBytes(filePath, patched);
        return backup;
    }

    /// <summary>
    /// Replaces <paramref name="find"/> with <paramref name="replace"/> in every selected string
    /// field. Returns what it found and, unless <paramref name="dryRun"/>, the patched bytes.
    /// </summary>
    /// <exception cref="FileFormatException">The buffer is not a StreamMap, or its header points
    /// outside itself.</exception>
    public static StreamMapPatch Replace(
        byte[] file,
        string find,
        string replace,
        StreamMapFields fields = StreamMapFields.All,
        bool dryRun = true)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(find);
        ArgumentNullException.ThrowIfNull(replace);
        if (find.Length == 0)
        {
            throw new ArgumentException("find must not be empty", nameof(find));
        }

        ValidateHeader(file);
        int poolStart = ReadI32(file, OffsetPoolStart);

        // Every reference to the pool that this library models, gathered before anything is written:
        // the same string can be referenced from several records, and the overlap check below has to
        // see all of them at once.
        List<(int Offset, StreamMapFields Field)> references = CollectReferences(file, poolStart);

        var byOffset = new SortedDictionary<int, StreamMapFields>();
        foreach ((int offset, StreamMapFields field) in references)
        {
            byOffset[offset] = byOffset.TryGetValue(offset, out StreamMapFields existing)
                ? existing | field
                : field;
        }

        var edits = new List<StreamMapEdit>();
        byte[]? output = dryRun ? null : (byte[])file.Clone();

        foreach ((int relative, StreamMapFields field) in byOffset)
        {
            if ((field & fields) == 0)
            {
                continue;
            }

            int start = poolStart + relative;
            if (start < 0 || start >= file.Length)
            {
                continue;
            }

            int length = TerminatedLength(file, start);
            string before = PoolEncoding.GetString(file, start, length);
            if (!before.Contains(find, StringComparison.Ordinal))
            {
                continue;
            }

            string after = before.Replace(find, replace, StringComparison.Ordinal);
            byte[] encoded = PoolEncoding.GetBytes(after);

            string? refused = null;
            if (encoded.Length > length)
            {
                refused = $"the replacement is {encoded.Length} bytes and the original occupies {length} — "
                    + "a StreamMap string can only be rewritten in place, so it cannot grow; "
                    + "choose a name no longer than the one it replaces";
            }
            else if (OverlapsAnotherString(byOffset, relative, length))
            {
                // Pools can share a suffix between two strings. Rewriting the longer one would
                // zero-fill the shorter one out of existence, so it is left alone instead.
                refused = "another string starts inside this one's bytes (the pool shares a suffix), "
                    + "so rewriting it here would destroy the other";
            }

            if (refused is null && output is not null)
            {
                encoded.CopyTo(output, start);
                // Zero the tail rather than leaving the old suffix: the string is NUL-terminated, so
                // one zero would be enough for a reader, but leaving readable remnants of a path a
                // modder just replaced is a trap for the next person to hex-edit this file.
                Array.Clear(output, start + encoded.Length, length - encoded.Length);
            }

            edits.Add(new StreamMapEdit(field, relative, before, after, refused is null && !dryRun, refused));
        }

        return new StreamMapPatch(edits, output);
    }

    private static void ValidateHeader(byte[] file)
    {
        if (file.Length < 72)
        {
            throw new FileFormatException(
                $"not a StreamMap: {file.Length} bytes is shorter than the header");
        }

        uint magic = BitConverter.ToUInt32(file, 0);
        if (magic != StreamMapFile.Magic)
        {
            throw new FileFormatException("not a StreamMap (bad magic, expected \"StrM\")");
        }

        // The file records its own length; a mismatch means the file was truncated or appended to,
        // and patching offsets inside it would land somewhere unintended.
        int declared = ReadI32(file, OffsetTotalSize);
        if (declared != file.Length)
        {
            throw new FileFormatException(
                $"StreamMap declares {declared} bytes but the file is {file.Length}");
        }
    }

    private static List<(int Offset, StreamMapFields Field)> CollectReferences(byte[] file, int poolStart)
    {
        var references = new List<(int, StreamMapFields)>();

        int groupCount = ReadI32(file, OffsetGroupCount);
        int groupArray = ReadI32(file, OffsetGroupArray);
        for (int i = 0; i < groupCount; i++)
        {
            int at = RecordAt(file, groupArray, i, GroupRecordSize, "group header");
            RequireRange(file, at, sizeof(long), "group header");
            // Stored as eight bytes, used as a signed 32-bit pool offset — the reader narrows it the
            // same way, so this reproduces which string the game actually resolves.
            references.Add(((int)BitConverter.ToUInt64(file, at), StreamMapFields.GroupName));
        }

        int lineCount = ReadI32(file, OffsetLineCount);
        int lineArray = ReadI32(file, OffsetLineArray);
        for (int i = 0; i < lineCount; i++)
        {
            int at = RecordAt(file, lineArray, i, LineRecordSize, "line");
            RequireRange(file, at, LineRecordSize, "line");
            references.Add((ReadI32(file, at + LineNameField), StreamMapFields.LineName));
        }

        int loaderCount = ReadI32(file, OffsetLoaderCount);
        int loaderArray = ReadI32(file, OffsetLoaderArray);
        for (int i = 0; i < loaderCount; i++)
        {
            int at = RecordAt(file, loaderArray, i, LoaderRecordSize, "loader");
            RequireRange(file, at, LoaderRecordSize, "loader");
            references.Add((ReadI32(file, at + LoaderPathField), StreamMapFields.Path));
            references.Add((ReadI32(file, at + LoaderEntityField), StreamMapFields.Entity));
        }

        // A wild offset resolves to "" in the reader rather than throwing; the same tolerance here
        // keeps one bad record from making the whole file un-editable.
        references.RemoveAll(r => r.Item1 < 0 || poolStart + r.Item1 >= file.Length);
        return references;
    }

    /// <summary>True when some other referenced string begins inside this one's bytes.</summary>
    private static bool OverlapsAnotherString(
        SortedDictionary<int, StreamMapFields> byOffset, int start, int length)
    {
        foreach (int other in byOffset.Keys)
        {
            if (other > start && other <= start + length)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Length of the NUL-terminated string at <paramref name="start"/>, terminator excluded.</summary>
    private static int TerminatedLength(byte[] file, int start)
    {
        int end = start;
        while (end < file.Length && file[end] != 0)
        {
            end++;
        }
        return end - start;
    }

    private static int ReadI32(byte[] file, int offset)
    {
        RequireRange(file, offset, sizeof(int), "header field");
        return BitConverter.ToInt32(file, offset);
    }

    /// <summary>
    /// Offset of record <paramref name="index"/>, computed in 64-bit arithmetic. The record COUNT is
    /// also a header field, so <c>index * size</c> can overflow on its own before the bounds check
    /// below ever sees the result.
    /// </summary>
    private static int RecordAt(byte[] file, int arrayStart, int index, int size, string what)
    {
        long at = (long)arrayStart + ((long)index * size);
        if (arrayStart < 0 || at < 0 || at + size > file.Length)
        {
            throw new FileFormatException(
                $"StreamMap {what} {index} lies outside the {file.Length}-byte file");
        }
        return (int)at;
    }

    /// <summary>
    /// Bounds check in 64-bit arithmetic. Every offset here is a header field read straight out of
    /// the file, so a corrupt or hostile one can be close to <see cref="int.MaxValue"/> — and in
    /// 32-bit arithmetic <c>offset + length</c> then overflows to a negative number, sails through
    /// the comparison, and lets BitConverter throw ArgumentOutOfRangeException instead of the
    /// FileFormatException this class documents.
    /// </summary>
    private static void RequireRange(byte[] file, int offset, int length, string what)
    {
        if (offset < 0 || length < 0 || (long)offset + length > file.Length)
        {
            throw new FileFormatException(
                $"StreamMap {what} at {offset} runs past the end of the {file.Length}-byte file");
        }
    }
}
