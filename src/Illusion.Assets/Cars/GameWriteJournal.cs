using Illusion.Assets.Sds;

namespace Illusion.Assets.Cars;

/// <summary>
/// What an operation that writes SEVERAL game files has done so far, so that it can be taken back when a
/// later step fails.
/// <para>
/// Cloning a car writes a new archive, rows in three shared tables and a string per language, then packs
/// five or more archives one after another. Any of those packs can fail — the game is running and holds the
/// file, the disk is full, an antivirus has it open — and stopping there leaves a state no single step
/// intended: a vehicle row with no archive behind it, tables written but not packed, a name that is now
/// "taken" so the clone cannot even be tried again. Nothing in the game's files is transactional, so the
/// operation keeps this list and undoes itself: files put back as they were, new ones removed, replaced
/// archives restored from the backup taken of them a moment earlier.
/// </para>
/// </summary>
internal sealed class GameWriteJournal
{
    private readonly List<(string Path, byte[]? Before)> _files = [];
    private readonly List<(string Folder, HashSet<string> Before)> _folders = [];
    private readonly List<string> _createdFolders = [];
    private readonly List<string> _createdFiles = [];
    private readonly List<(string Folder, string Aside)> _movedAside = [];
    private readonly List<(string Archive, string? Backup)> _replaced = [];

    /// <summary>Remembers a file as it is now — its bytes, or that it is not there.</summary>
    public void Remember(string path)
    {
        if (_files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        _files.Add((path, File.Exists(path) ? File.ReadAllBytes(path) : null));
    }

    /// <summary>Remembers which files a folder holds, so that ones added later can be removed.</summary>
    public void RememberContents(string folder)
    {
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(folder)) before.UnionWith(Directory.GetFiles(folder));
        _folders.Add((folder, before));
    }

    /// <summary>A folder this operation is about to create.</summary>
    public void WillCreateFolder(string folder) => _createdFolders.Add(folder);

    /// <summary>A file this operation is about to create (an archive that did not exist).</summary>
    public void WillCreateFile(string path) => _createdFiles.Add(path);

    /// <summary>
    /// Moves a folder out of the way instead of deleting it: it is put back if the operation fails and
    /// removed by <see cref="Commit"/> if it succeeds. Nothing happens when there is no such folder.
    /// </summary>
    public void MoveAside(string folder)
    {
        if (!Directory.Exists(folder)) return;
        string aside = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + ".aside-" + Guid.NewGuid().ToString("N")[..8];
        Directory.Move(folder, aside);
        _movedAside.Add((folder, aside));
    }

    /// <summary>An archive that was just replaced, with the backup taken of what it held.</summary>
    public void Replaced(string archive, string? backup) => _replaced.Add((archive, backup));

    /// <summary>The operation succeeded: what was moved aside is no longer needed.</summary>
    public void Commit()
    {
        foreach ((string _, string aside) in _movedAside)
        {
            try { SdsWriter.DeleteExtracted(aside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a leftover folder beside the working copy; it is named for what it is
            }
        }
        _movedAside.Clear();
    }

    /// <summary>
    /// Takes everything back, newest first. Returns what could NOT be put back, in words — an operation that
    /// failed because a file is locked may well fail to restore that same file.
    /// </summary>
    public List<string> Undo()
    {
        var problems = new List<string>();
        void Try(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{what}: {ex.Message}");
            }
        }

        for (int i = _replaced.Count - 1; i >= 0; i--)
        {
            (string archive, string? backup) = _replaced[i];
            if (backup == null || !File.Exists(backup))
            {
                problems.Add($"{Path.GetFileName(archive)} was replaced and there is no backup of it to restore");
                continue;
            }
            Try($"restoring {Path.GetFileName(archive)}", () =>
            {
                File.Copy(backup, archive, overwrite: true);
                File.Delete(backup); // the backup existed only because of this operation
            });
        }
        foreach (string file in Enumerable.Reverse(_createdFiles))
        {
            Try($"removing {Path.GetFileName(file)}", () =>
            {
                File.Delete(file);
                DeleteTemporary(file); // what a pack leaves when its last move fails
            });
        }
        foreach ((string archive, string? _) in _replaced)
        {
            Try($"removing {Path.GetFileName(archive)}.tmp", () => DeleteTemporary(archive));
        }
        foreach (string folder in Enumerable.Reverse(_createdFolders))
        {
            Try($"removing {folder}", () => SdsWriter.DeleteExtracted(folder));
        }
        for (int i = _movedAside.Count - 1; i >= 0; i--)
        {
            (string folder, string aside) = _movedAside[i];
            Try($"putting {folder} back", () =>
            {
                SdsWriter.DeleteExtracted(folder);
                if (Directory.Exists(aside)) Directory.Move(aside, folder);
            });
        }
        foreach ((string folder, HashSet<string> before) in _folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (string file in Directory.GetFiles(folder))
            {
                if (!before.Contains(file)) Try($"removing {Path.GetFileName(file)}", () => File.Delete(file));
            }
        }
        for (int i = _files.Count - 1; i >= 0; i--)
        {
            (string path, byte[]? bytes) = _files[i];
            Try($"restoring {Path.GetFileName(path)}", () =>
            {
                if (bytes == null) File.Delete(path);
                else AtomicFile.WriteAllBytes(path, bytes);
            });
        }
        return problems;
    }

    // Only a FILE of that name is the pack's leftover. Whatever else stands there — the very thing that made
    // the pack fail, perhaps — is not this operation's to remove, and not being able to is no failure of the undo.
    private static void DeleteTemporary(string archive)
    {
        if (File.Exists(archive + ".tmp")) File.Delete(archive + ".tmp");
    }
}
