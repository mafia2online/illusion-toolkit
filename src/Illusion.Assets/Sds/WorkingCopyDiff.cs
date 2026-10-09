using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Illusion.Formats.Archive;

namespace Illusion.Assets.Sds;

/// <summary>How a file of a working copy stands against the archive in the game.</summary>
public enum WorkingCopyChangeKind
{
    /// <summary>In both, with different contents.</summary>
    Changed,
    /// <summary>In the working copy only: a Build adds it to the archive.</summary>
    Added,
    /// <summary>In the archive only: a Build drops it from the archive.</summary>
    Removed,
}

/// <summary>One file a Build would make different in the game.</summary>
/// <param name="Path">Relative to the working copy, with forward slashes.</param>
/// <param name="Size">The working copy's file; for a removed one, the archive's.</param>
/// <param name="Modified">When the working copy's file was last written (for a removed one, the default).</param>
public sealed record WorkingCopyChange(string Path, WorkingCopyChangeKind Kind, long Size, DateTime Modified);

/// <summary>What packing a working copy would change in the game.</summary>
/// <param name="Archive">The archive in the game.</param>
/// <param name="InGame">False when the game has no such archive yet - then every file is an addition.</param>
/// <param name="Changes">The files that differ, by path.</param>
/// <param name="MissingEntries">Files the working copy's manifest names that are not there: a Build leaves them
/// out of the archive and strikes them from the manifest.</param>
public sealed record WorkingCopyComparison(
    FileInfo Archive, bool InGame, IReadOnlyList<WorkingCopyChange> Changes, IReadOnlyList<string> MissingEntries);

/// <summary>
/// Compares an archive's working copy with the archive as it stands in the game, file by file.
/// <para>
/// A Build packs the WHOLE working copy, not what was edited since it was last opened: a file changed months
/// ago and forgotten goes into the game with today's edit, and nothing said so (it happened with
/// city_univers - an old collision file and name table rode in beside an eight-byte change). This is what a
/// Build window shows before it packs.
/// </para>
/// <para>
/// The archive is unpacked afresh into a scratch folder and the two folders are compared by what the files
/// hold, not by what they are called (see <see cref="Match"/>). Unpacking is deterministic, so a working copy
/// nobody touched compares equal; the game's archive is only read.
/// </para>
/// </summary>
public static partial class WorkingCopyDiff
{
    /// <summary>Compares the working copy the toolkit keeps for <paramref name="sds"/>.</summary>
    /// <exception cref="FileNotFoundException">The archive has no working copy.</exception>
    public static WorkingCopyComparison Compare(FileInfo sds)
    {
        ArgumentNullException.ThrowIfNull(sds);
        return Compare(sds, MafiaEnvironment.ExtractedDir(sds));
    }

    /// <summary>Compares the working copy in <paramref name="workingDir"/> with <paramref name="sds"/>.</summary>
    /// <exception cref="FileNotFoundException">The folder is not a working copy (it has no manifest).</exception>
    public static WorkingCopyComparison Compare(FileInfo sds, string workingDir)
    {
        ArgumentNullException.ThrowIfNull(sds);
        ArgumentNullException.ThrowIfNull(workingDir);
        if (!File.Exists(Path.Combine(workingDir, "SDSContent.xml")))
        {
            throw new FileNotFoundException($"{sds.Name} has no working copy - there is nothing to pack.", Path.Combine(workingDir, "SDSContent.xml"));
        }

        IReadOnlyList<string> missing = SdsWriter.MissingEntries(workingDir);
        Dictionary<string, FileInfo> mine = Files(workingDir);
        // A file the manifest does not name is not packed, whatever lies in the folder (a note, a copy kept
        // "just in case"): it is not something a Build puts into the game, and is not listed as one.
        // unescaped: the list writes "&" as "&amp;", and a file name is compared as it stands on disk
        string manifest = System.Net.WebUtility.HtmlDecode(File.ReadAllText(Path.Combine(workingDir, "SDSContent.xml")));
        // A name that several files of the working copy carry, each in a folder of its own, is told apart by the
        // folder: with "/a/foo" unsaid and "/b/foo" still listed, the name alone found it listed.
        HashSet<string> shared = [.. mine.Keys.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key!)];
        // As a whole name - the text of an element, or the end of a path in one - and not as a piece of a longer
        // one: with "a.dds" unsaid and "ba.dds" still listed, a search for the letters found it listed.
        bool Says(string name)
        {
            for (int at = manifest.IndexOf(name, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = manifest.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                char before = at == 0 ? '>' : manifest[at - 1];
                int end = at + name.Length;
                if (before is '>' or '/' or '\\' && (end >= manifest.Length || manifest[end] == '<')) return true;
            }
            return false;
        }
        bool Named(string path)
        {
            // By its place in the working copy when the name is not its alone, by the name otherwise. A texture's
            // top level ("MIP_<name>") is a resource with an entry of its own and is looked up as any other: it
            // used to pass as named whenever its texture was, which hid the deletion of the Mipmap alone.
            string name = shared.Contains(Path.GetFileName(path)) ? path : Path.GetFileName(path);
            return path.Equals("SDSContent.xml", StringComparison.OrdinalIgnoreCase)
                || Says(name) || Says(name.Replace('/', '\\'))
                // an XML resource is named without the extension it is unpacked with
                || (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && (Says(name[..^4]) || Says(name[..^4].Replace('/', '\\'))));
        }
        sds.Refresh();
        if (!sds.Exists)
        {
            return new WorkingCopyComparison(sds, false,
                [.. mine.Where(f => Named(f.Key)).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new WorkingCopyChange(f.Key, WorkingCopyChangeKind.Added, f.Value.Length, f.Value.LastWriteTime))],
                missing);
        }

        string scratch = Path.Combine(Path.GetTempPath(), "illusion_compare_" + Guid.NewGuid().ToString("N"));
        try
        {
            SdsArchive.Open(sds.FullName).Extract(scratch);
            return new WorkingCopyComparison(sds, true, Match(mine, Files(scratch), Named), missing);
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a scratch folder left in %TEMP% is not worth failing a comparison over
            }
        }
    }

    // "Actors_306.act", "AnimalTrafficPaths317.atp", "ItemDesc_096a92487af8f55f.ids": a file named by the toolkit,
    // after its place in the archive or after a hash, because the archive gives the resource no name.
    [GeneratedRegex(@"^[A-Za-z_]+?_?(\d+|[0-9a-f]{16})\.[^./]+$")]
    private static partial Regex Unnamed();

    // Pairs the working copy's files with the archive's. By name alone that goes wrong: a resource the archive
    // does not name is unpacked as "<Type>_<its index>", and the index is its place in the archive - add one
    // texture and every such file after it comes out of the rebuilt archive under a new number; and a resource
    // the toolkit imported sits in the working copy as "<Type>_<hash>" and comes back as "<Type>_<index>". A
    // working copy that was just packed then "differs" from its own archive in dozens of files. So:
    //   1. same name, same bytes                    - the same resource, nothing to say;
    //   2. same bytes, same kind, another name      - the same resource under another number: nothing to say;
    //   3. same name, other bytes                   - changed;
    //   4. unnamed files of one kind left over on both sides - the same resources changed AND renumbered:
    //      paired in order, as changed;
    //   5. what is left: in the working copy only - added; in the archive only - removed.
    // "Kind" is the extension, for a file at the top of the folder that is not a texture: a texture keeps the
    // name its artist gave it, and so does anything in a folder of its own.
    private static List<WorkingCopyChange> Match(Dictionary<string, FileInfo> mine, Dictionary<string, FileInfo> theirs, Func<string, bool> named)
    {
        // the manifest is the list of the files, not one of them: an entry added or dropped shows as its file
        mine.Remove("SDSContent.xml");
        theirs.Remove("SDSContent.xml");
        // A file the manifest does not name is not in the working copy as far as a Build goes, even when it lies
        // there byte for byte as the archive has it: that is what deleting a resource leaves (the entry is
        // unsaid, the payload stays). Left in, it was paired with the archive's copy below as "the same" and
        // the deletion was not listed - with nothing else changed, the archive came up as "nothing differs".
        foreach (string path in mine.Keys.Where(path => !named(path)).ToList()) mine.Remove(path);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Hash(FileInfo file)
        {
            if (!hashes.TryGetValue(file.FullName, out string? hash))
            {
                using FileStream stream = file.OpenRead();
                hashes[file.FullName] = hash = file.Length + ":" + Convert.ToHexString(SHA256.HashData(stream));
            }
            return hash;
        }
        static string Kind(string path) =>
            !path.Contains('/') && !path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? "*" + Path.GetExtension(path).ToLowerInvariant() : path;

        foreach (string path in mine.Keys.Where(theirs.ContainsKey).ToList())          // 1
        {
            if (mine[path].Length == theirs[path].Length && Hash(mine[path]) == Hash(theirs[path]))
            {
                mine.Remove(path);
                theirs.Remove(path);
            }
        }
        var byBytes = new Dictionary<string, Queue<string>>();                          // 2
        foreach ((string path, FileInfo file) in theirs.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (Kind(path) == path) continue;
            string key = Kind(path) + "|" + Hash(file);
            if (!byBytes.TryGetValue(key, out Queue<string>? queue)) byBytes[key] = queue = new Queue<string>();
            queue.Enqueue(path);
        }
        foreach (string path in mine.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (Kind(path) == path) continue;
            if (byBytes.TryGetValue(Kind(path) + "|" + Hash(mine[path]), out Queue<string>? queue) && queue.Count > 0)
            {
                theirs.Remove(queue.Dequeue());
                mine.Remove(path);
            }
        }

        var changes = new List<WorkingCopyChange>();
        void Changed(string path) => changes.Add(new WorkingCopyChange(path, WorkingCopyChangeKind.Changed, mine[path].Length, mine[path].LastWriteTime));
        foreach (string path in mine.Keys.Where(theirs.ContainsKey).ToList())          // 3
        {
            Changed(path);
            mine.Remove(path);
            theirs.Remove(path);
        }
        foreach (IGrouping<string, string> kind in mine.Keys.Where(k => Kind(k) != k && Unnamed().IsMatch(k)).GroupBy(Kind).ToList())      // 4
        {
            var stock = new Queue<string>(theirs.Keys.Where(k => Kind(k) == kind.Key && Unnamed().IsMatch(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            foreach (string path in kind.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                if (stock.Count == 0) break;
                Changed(path);
                mine.Remove(path);
                theirs.Remove(stock.Dequeue());
            }
        }
        foreach ((string path, FileInfo file) in mine)                                  // 5
        {
            changes.Add(new WorkingCopyChange(path, WorkingCopyChangeKind.Added, file.Length, file.LastWriteTime));
        }
        foreach ((string path, FileInfo file) in theirs)
        {
            changes.Add(new WorkingCopyChange(path, WorkingCopyChangeKind.Removed, file.Length, default));
        }
        changes.Sort((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.Path, y.Path));
        return changes;
    }

    // Every file of a folder by its path inside it. The toolkit's own note of the archive's memory figures is
    // not part of the archive's contents and is left out.
    private static Dictionary<string, FileInfo> Files(string root)
    {
        var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (string.Equals(relative, SdsMemoryRequirements.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            files[relative] = new FileInfo(path);
        }
        return files;
    }
}
