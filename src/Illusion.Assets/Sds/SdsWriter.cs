using System.Globalization;
using System.Text.RegularExpressions;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;

namespace Illusion.Assets.Sds;

/// <summary>
/// Writes in-memory frame edits back to disk and repacks the archive, the inverse of <see cref="SdsMeshLoader"/>.
/// Two independent steps mirror MafiaToolkit's own flow (edit the extracted files → build the SDS):
/// <list type="bullet">
/// <item><see cref="SaveFrameResource"/> — re-serialize the edited <c>FrameResource</c> into its extracted
/// folder (the working copy under <c>&lt;root&gt;\resources\…</c>). Cheap; only the FrameResource file changes.</item>
/// <item><see cref="PackSds(FileInfo, bool)"/> — pack that whole extracted folder back into the original <c>.sds</c> archive.
/// Heavier (zlib of every resource); overwrites the game file, keeping a timestamped backup for versioning.</item>
/// </list>
/// Both rely on the MafiaToolkit globals having been initialised by <see cref="MafiaEnvironment.TryInitialize"/>
/// (selected game = Mafia II, ToolkitSettings pack-path values) — which the app always does before a scene loads.
/// </summary>
public static class SdsWriter
{
    /// <summary>Timestamp format for backup file names — sortable (chronological = alphabetical) and second-precise.</summary>
    private const string BackupStamp = "yyyyMMdd_HHmmss";

    /// <summary>Name of the folder (beside each archive) that versioned backups live in. Backups are full
    /// <c>.sds</c> copies, so the bulk unpacker must skip this folder — see
    /// <see cref="ResourceUnpacker.IsUnpackableGameSds"/>, which excludes it by this name.</summary>
    public const string BackupFolderName = "backups";

    /// <summary>Outcome of a single <see cref="PackSds(FileInfo, bool)"/>: the archive that was (re)written and the backup made of
    /// its previous contents, or <c>null</c> if no backup was created (disabled, or a brand-new archive).</summary>
    public readonly record struct PackResult(string Archive, string? Backup);

    /// <summary>
    /// Drops manifest entries whose file is not on disk. Such an entry can only fail packing — the archive
    /// would name a resource nothing carries — and it is unrecoverable from the folder's side, so removing it
    /// is the only thing that lets a Build succeed at all.
    /// </summary>
    /// <returns>The file names that were unsaid.</returns>
    internal static List<string> PruneMissingEntries(string extracted)
    {
        var dropped = new List<string>();
        SdsManifest manifest;
        try { manifest = SdsManifest.Load(extracted); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return dropped; }

        foreach ((string type, string file) in manifest.Entries.ToArray())
        {
            // Containers list their pieces under elements of their own; their File is not a file.
            if (type is "Script" or "Table") continue;
            // Names out of the archive are often rooted ("/missions/city11_Little_Italy/SoundSectors_city11.bin"),
            // and Path.Combine treats a rooted second argument as absolute: the file was looked for at the
            // drive root, never found, and a perfectly present resource was unsaid on every Build — a
            // district lost its AudioSectors that way. Joined the way the handlers join it.
            string onDisk = Path.Combine(extracted, file.TrimStart('/', '\\'));
            if (File.Exists(onDisk)) continue;
            // An XML resource is named without its extension and extracts to that name plus ".xml" — the
            // handler adds it on both sides. Missing that unsaid every XML of ingame.sds on its first Build.
            if (type == "XML" && File.Exists(onDisk + ".xml")) continue;
            if (manifest.RemoveEntry(file)) dropped.Add(file);
        }
        return dropped;
    }

    /// <summary>
    /// Re-serializes <paramref name="frame"/> (with the user's transform edits) over the <c>FrameResource</c>
    /// file inside <paramref name="sds"/>'s extracted folder. Returns the file written.
    /// </summary>
    /// <remarks>Side effect: <c>FrameResource.WriteToFile</c> runs <c>UpdateFrameData</c>/<c>SanitizeFrameData</c>,
    /// which recomputes indices and prunes unused blocks on the live resource — so the in-memory frame is mutated
    /// (harmless for transforms; every loaded object still references its geometry, so nothing is pruned).</remarks>
    public static string SaveFrameResource(FrameResource frame, FileInfo sds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(sds);

        string extracted = MafiaEnvironment.ExtractedDir(sds);
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml")))
            throw new FileNotFoundException(
                $"Extracted SDS content not found for {sds.Name} — the archive must be opened before it can be saved.",
                Path.Combine(extracted, "SDSContent.xml"));

        IReadOnlyList<string> files =
            SdsManifest.Load(extracted).GetFiles("FrameResource");
        if (files.Count == 0)
            throw new InvalidOperationException($"{sds.Name} has no FrameResource resource to write.");

        // Serialize to memory first (WriteToStream runs UpdateFrameData, so a failure throws here — before any
        // file is touched), then swap it into place atomically. FrameResource.WriteToFile would open the target
        // with FileMode.Create and truncate the only working copy up-front; a mid-write throw would leave it
        // corrupt and the district would then fail to reload. temp-then-move avoids that entirely.
        byte[] bytes = frame.WriteToStream();
        AtomicFile.WriteAllBytes(files[0], bytes);
        return files[0];
    }

    /// <summary>
    /// Rebuilds the <c>FrameNameTable</c> from <paramref name="frame"/> and writes it over the extracted folder's
    /// name-table file. Returns the file written, or null when the archive has no name table. Call AFTER
    /// <see cref="SaveFrameResource"/> — the rebuild reads the object order / scene indices that
    /// <c>UpdateFrameData</c> finalises. The rebuild is a verified semantic fixpoint (see <c>--probe-nametable</c>):
    /// reloading it reproduces the exact per-object name-table membership, flags and names.
    /// </summary>
    public static string? SaveFrameNameTable(FrameResource frame, FileInfo sds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(sds);

        string extracted = MafiaEnvironment.ExtractedDir(sds);
        IReadOnlyList<string> files =
            SdsManifest.Load(extracted).GetFiles("FrameNameTable");
        if (files.Count == 0) return null; // no name table in this archive

        var table = new FrameNameTable();
        table.BuildDataFromResource(frame);

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) table.WriteToFile(bw);
            bytes = ms.ToArray();
        }
        AtomicFile.WriteAllBytes(files[0], bytes);
        return files[0];
    }

    /// <summary>
    /// What the resources of <paramref name="sds"/> ask the engine to budget for them. Kept beside the working
    /// copy once known; read the first time from the archive as it shipped — its oldest backup, or the archive
    /// itself when it was never built. Figures that read like a packer's own (payload sizes, no "other" RAM)
    /// are not kept, so an archive the toolkit made earlier does not pass its under-statement on.
    /// </summary>
    /// <returns>Null when nothing trustworthy is known — the packing handlers' own figures are used then.</returns>
    internal static SdsMemoryRequirements? MemoryFor(FileInfo sds, string extracted)
    {
        if (SdsMemoryRequirements.Load(extracted) is { } known) return known;

        IReadOnlyList<BackupInfo> backups = ListBackups(sds);
        sds.Refresh();
        FileInfo? shipped = backups.Count > 0 ? backups[^1].File : sds.Exists ? sds : null;
        if (shipped == null) return null;
        try
        {
            SdsMemoryRequirements read = SdsMemoryRequirements.FromArchive(shipped.FullName);
            if (!read.LooksShipped) return null;
            read.Save(extracted);
            return read;
        }
        catch (Exception ex) when (ex is IOException or FileFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Makes sure the working copy of <paramref name="sds"/> knows its memory requirements — what a
    /// copy of that folder (a cloned car) then carries with it.</summary>
    public static bool EnsureMemoryRequirements(FileInfo sds)
    {
        ArgumentNullException.ThrowIfNull(sds);
        return MemoryFor(sds, MafiaEnvironment.ExtractedDir(sds)) != null;
    }

    /// <summary>
    /// Gives the working copy of <paramref name="sds"/> the memory requirements <paramref name="reference"/>
    /// states — for an archive that never shipped itself: a clone made before requirements were kept takes
    /// them from the car it was cloned from, whose resources bear the same names.
    /// </summary>
    /// <returns>How many resources the reference stated requirements for.</returns>
    public static int AdoptMemoryRequirements(FileInfo sds, FileInfo reference)
    {
        ArgumentNullException.ThrowIfNull(sds);
        ArgumentNullException.ThrowIfNull(reference);
        SdsMemoryRequirements read = SdsMemoryRequirements.FromArchive(reference.FullName);
        read.Save(MafiaEnvironment.ExtractedDir(sds));
        return read.Count;
    }

    /// <summary>The folder timestamped backups of <paramref name="sds"/> are written to: a <c>backups</c> subfolder
    /// beside the archive (e.g. <c>…\sds\city\backups</c>).</summary>
    public static string BackupDir(FileInfo sds)
    {
        ArgumentNullException.ThrowIfNull(sds);
        return Path.Combine(sds.DirectoryName ?? ".", BackupFolderName);
    }

    /// <summary>
    /// Copies <paramref name="sds"/>'s current contents into its <see cref="BackupDir"/> under a timestamped name
    /// (<c>&lt;name&gt;_yyyyMMdd_HHmmss.sds</c>), preserving that version. All versions are kept — every build adds a
    /// new one, so the very first backup is always the untouched stock archive. Returns the backup path, or
    /// <c>null</c> if <paramref name="sds"/> does not exist yet (nothing to preserve).
    /// </summary>
    /// <param name="when">Timestamp for the backup name; callers pass a single value per build so all archives packed
    /// together share one timestamp (they group in the folder and sort as one build).</param>
    public static string? BackupArchive(FileInfo sds, DateTime when)
    {
        ArgumentNullException.ThrowIfNull(sds);
        sds.Refresh();
        if (!sds.Exists) return null;

        string dir = BackupDir(sds);
        Directory.CreateDirectory(dir);

        string stem = Path.GetFileNameWithoutExtension(sds.Name);
        string ext = sds.Extension; // ".sds"
        string stamp = when.ToString(BackupStamp, CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"{stem}_{stamp}{ext}");

        // Guard against two builds of the same archive within one second colliding — append a counter.
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(dir, $"{stem}_{stamp}_{n}{ext}");

        File.Copy(sds.FullName, path);
        return path;
    }

    /// <summary>One recoverable version of an archive: the backup file and the build timestamp parsed
    /// from its name (the counter suffix of same-second builds is reflected by name order, not here).</summary>
    public readonly record struct BackupInfo(FileInfo File, DateTime Stamp);

    /// <summary>
    /// The versions of <paramref name="sds"/> available in its <see cref="BackupDir"/>, newest first.
    /// Matching is strict (<c>&lt;stem&gt;_&lt;yyyyMMdd_HHmmss&gt;[_n].sds</c> with a parseable stamp):
    /// sibling archives sharing a name prefix (<c>city_crash</c> / <c>city_crash_z</c>) share one backups
    /// folder, so a loose <c>stem_*</c> pattern would cross-match their versions.
    /// </summary>
    public static IReadOnlyList<BackupInfo> ListBackups(FileInfo sds)
    {
        ArgumentNullException.ThrowIfNull(sds);
        var result = new List<BackupInfo>();
        string dir = BackupDir(sds);
        if (!Directory.Exists(dir)) return result;

        string stem = Path.GetFileNameWithoutExtension(sds.Name);
        var rx = new Regex("^" + Regex.Escape(stem) + @"_(\d{8}_\d{6})(?:_\d+)?\.sds$", RegexOptions.IgnoreCase);
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            Match m = rx.Match(Path.GetFileName(file));
            if (!m.Success) continue;
            if (!DateTime.TryParseExact(m.Groups[1].Value, BackupStamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime stamp)) continue; // stamp-shaped but not a date — not ours
            result.Add(new BackupInfo(new FileInfo(file), stamp));
        }

        // Newest first. Name order IS chronological (sortable stamp), and it also breaks same-second
        // ties correctly: the counter suffix ('_' > '.') sorts a later same-second build first.
        result.Sort((a, b) => string.CompareOrdinal(b.File.Name, a.File.Name));
        return result;
    }

    /// <summary>
    /// Replaces the live archive at <paramref name="sds"/>.FullName with <paramref name="backup"/>'s contents —
    /// the rollback counterpart of <see cref="PackSds(FileInfo, bool)"/>, and the same swap discipline: copy to a
    /// temp beside the target, then an atomic same-volume <c>File.Move</c>, so the game .sds is never left
    /// half-written. The backup itself (and every other version) is left untouched. Throws when the backup is
    /// missing/empty or the live archive is locked (game running) — nothing is swapped in those cases.
    /// </summary>
    public static void RestoreArchive(FileInfo sds, FileInfo backup)
    {
        ArgumentNullException.ThrowIfNull(sds);
        ArgumentNullException.ThrowIfNull(backup);
        backup.Refresh();
        if (!backup.Exists)
            throw new FileNotFoundException($"Backup not found: {backup.FullName}", backup.FullName);
        if (backup.Length == 0)
            throw new IOException($"Backup {backup.Name} is empty — refusing to restore it.");

        var tmp = new FileInfo(sds.FullName + ".tmp");
        if (tmp.Exists) tmp.Delete();
        File.Copy(backup.FullName, tmp.FullName);
        File.Move(tmp.FullName, sds.FullName, overwrite: true);
    }

    /// <summary>
    /// Deletes an archive's extracted mirror (the folder <see cref="MafiaEnvironment.ExtractedDir"/> names) so
    /// the next load re-extracts from the archive — the only way to invalidate it, since
    /// <c>SdsMeshLoader.EnsureExtracted</c> checks nothing but the marker's existence. The
    /// <c>SDSContent.xml</c> marker goes FIRST: a partially-deleted tree without it re-extracts cleanly,
    /// while one with it would pass for extracted and load broken. Missing folder = no-op. Retries briefly —
    /// a just-cancelled background load may still be closing file handles.
    /// </summary>
    public static void DeleteExtracted(string extractedDir)
    {
        ArgumentNullException.ThrowIfNull(extractedDir);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(extractedDir)) return;
                string marker = Path.Combine(extractedDir, "SDSContent.xml");
                if (File.Exists(marker)) File.Delete(marker);
                Directory.Delete(extractedDir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// Packs <paramref name="sds"/>'s extracted folder back into the archive at <paramref name="sds"/>.FullName,
    /// overwriting the original game file. When <paramref name="createBackup"/> is set, the archive's previous
    /// contents are first copied into its <see cref="BackupDir"/> under a timestamped name (see
    /// <see cref="BackupArchive"/>) so every build is recoverable and versioned. Returns what was written.
    /// </summary>
    public static PackResult PackSds(FileInfo sds, bool createBackup = true) => PackSds(sds, createBackup, DateTime.Now);

    /// <inheritdoc cref="PackSds(FileInfo, bool)"/>
    /// <param name="when">Timestamp shared across one build so co-packed archives back up under the same stamp.</param>
    public static PackResult PackSds(FileInfo sds, bool createBackup, DateTime when)
    {
        ArgumentNullException.ThrowIfNull(sds);

        string extracted = MafiaEnvironment.ExtractedDir(sds);
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml")))
            throw new FileNotFoundException(
                $"Extracted SDS content not found for {sds.Name} — nothing to pack.",
                Path.Combine(extracted, "SDSContent.xml"));

        // A manifest line naming a file that is not there fails the whole Build, and the toolkit is what puts
        // such lines in — an added collision shape that was then undone used to leave one behind. Nothing on
        // disk can rescue such an entry, so drop it here rather than let it block every future Build; the
        // resource is already gone either way, and this is the only place that sees the pair.
        PruneMissingEntries(extracted);

        // Pack to a temp archive beside the target, so a mid-write failure never touches the live game file.
        // SdsArchive.Pack/Save throw on failure (missing files, bad manifest), which propagates to the
        // caller before the temp is ever moved over the live archive.
        var tmp = new FileInfo(sds.FullName + ".tmp");
        if (tmp.Exists) tmp.Delete();
        SdsArchive archive = SdsArchive.Pack(extracted, GameProfile.MafiaII, MemoryFor(sds, extracted));
        using (FileStream output = File.Create(tmp.FullName))
        {
            archive.Save(output, new SdsWriteOptions());
        }
        tmp.Refresh();
        if (!tmp.Exists || tmp.Length == 0)
            throw new IOException($"Packing {sds.Name} produced no output.");

        // Preserve the archive's current contents (the packer makes no backup of its own — bBackupEnabled is off),
        // then swap the freshly-built archive into place. File.Move on the same volume is atomic, so the game .sds
        // is never left half-written. The backup is taken AFTER the temp built successfully, so a failed build
        // never spawns a spurious version.
        string? backup = createBackup ? BackupArchive(sds, when) : null;
        File.Move(tmp.FullName, sds.FullName, overwrite: true);
        return new PackResult(sds.FullName, backup);
    }
}
