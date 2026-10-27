using System.Globalization;
using System.Security.Cryptography;

namespace Illusion.Assets.Sds;

/// <summary>
/// The archives Mafia II (Classic, PC) ships in <c>pc\sds</c>, with their size and SHA-256. A patch only means
/// something against the bytes every player has, so an export diffs against a copy that matches this list - the
/// archive itself while it is untouched, else the oldest backup the toolkit or MafiaToolkit kept of it.
/// </summary>
public static class StockArchives
{
    /// <summary>One shipped archive: its size and its SHA-256 (lower-case hex).</summary>
    public readonly record struct Entry(long Size, string Sha256);

    /// <summary>The shipped archive at <paramref name="target"/> (<c>/sds/city/uppertown.sds</c> or
    /// <c>sds/city/uppertown.sds</c>, any case), or null when the game has none of that name.</summary>
    public static Entry? Find(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return List.Value.TryGetValue(Key(target), out Entry entry) ? entry : null;
    }

    /// <summary>Whether the game ships an archive at <paramref name="target"/>.</summary>
    public static bool IsStock(string target) => Find(target) != null;

    /// <summary>
    /// A file holding the shipped bytes of <paramref name="sds"/>: the archive itself, else its oldest backup
    /// (the toolkit's <c>backups\</c>, then MafiaToolkit's <c>BackupSDS\</c>). Null when none matches.
    /// </summary>
    /// <param name="target">The archive's game path, as <see cref="M2oMapExporter.TargetOf"/> gives it.</param>
    public static FileInfo? ResolveStockCopy(FileInfo sds, string target)
    {
        ArgumentNullException.ThrowIfNull(sds);
        if (Find(target) is not { } entry) return null;
        return Candidates(sds).FirstOrDefault(file => Matches(file, entry));
    }

    private static IEnumerable<FileInfo> Candidates(FileInfo sds)
    {
        yield return sds;
        // oldest first: the very first backup is the one taken before any edit
        foreach (SdsWriter.BackupInfo backup in SdsWriter.ListBackups(sds).Reverse()) yield return backup.File;
        string stem = Path.GetFileNameWithoutExtension(sds.Name);
        string mafiaToolkit = Path.Combine(sds.DirectoryName ?? ".", "BackupSDS");
        if (!Directory.Exists(mafiaToolkit)) yield break;
        foreach (string file in Directory.EnumerateFiles(mafiaToolkit, stem + "_*.sds").Order(StringComparer.OrdinalIgnoreCase))
            yield return new FileInfo(file);
    }

    private static bool Matches(FileInfo file, Entry entry)
    {
        file.Refresh();
        if (!file.Exists || file.Length != entry.Size) return false;
        using FileStream stream = file.OpenRead();
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), entry.Sha256, StringComparison.Ordinal);
    }

    private static string Key(string target) => target.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private static readonly Lazy<Dictionary<string, Entry>> List = new(() =>
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        System.Reflection.Assembly assembly = typeof(StockArchives).Assembly;
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("StockArchives.txt", StringComparison.Ordinal));
        if (resource == null) return entries;
        using Stream? stream = assembly.GetManifestResourceStream(resource);
        if (stream == null) return entries;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split(' ');
            if (parts.Length != 3 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long size)) continue;
            entries[Key(parts[0])] = new Entry(size, parts[2]);
        }
        return entries;
    });
}
