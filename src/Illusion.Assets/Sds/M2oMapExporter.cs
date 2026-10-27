using Illusion.Assets.Materials;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Materials;

namespace Illusion.Assets.Sds;

/// <summary>How an archive travels in an M2O export.</summary>
public enum M2oExportKind
{
    /// <summary>The game ships the archive: a native <c>.sds.patch</c> against its shipped bytes.</summary>
    Patch,
    /// <summary>The game has no archive of that name: the archive itself.</summary>
    NewArchive,
}

/// <summary>One archive of an M2O export.</summary>
/// <param name="Archive">The game archive exported.</param>
/// <param name="Target">Its game path, <c>/sds/city/uppertown.sds</c>.</param>
/// <param name="OutputPath">The file written for it, in the published export.</param>
/// <param name="Kind">Whether that file is a patch or the archive itself.</param>
/// <param name="Diff">What the patch carries; null for a new archive.</param>
/// <param name="LibraryPath">The material library written for it, or null when it adds no material.</param>
/// <param name="Materials">How many materials that library holds.</param>
public readonly record struct M2oExportResult(string Archive, string Target, string OutputPath, M2oExportKind Kind, PatchDiffResult? Diff, string? LibraryPath, int Materials);

/// <summary>
/// Writes edited archives as an M2O resource: a folder whose <c>stream\</c> drops into <c>resources\&lt;name&gt;\</c>
/// as it is. Every file sits at the game path it stands for, so the path says what it is and no manifest is needed:
/// <list type="bullet">
/// <item><c>stream/sds/&lt;path&gt;.sds.patch</c> for an archive the game ships, diffed against its shipped bytes;</item>
/// <item><c>stream/sds/&lt;path&gt;.sds</c> for an archive the game does not have;</item>
/// <item><c>stream/materials/&lt;archive&gt;.mtl</c> with the materials an archive adds, one library per archive.</item>
/// </list>
/// </summary>
public static class M2oMapExporter
{
    /// <summary>The folder of a resource M2O streams to its players.</summary>
    public const string StreamFolder = "stream";

    /// <summary>The native target path relative to the game's pc directory.</summary>
    public static string TargetOf(FileInfo archive)
    {
        string relative = Path.GetRelativePath(MafiaEnvironment.PcFolder, archive.FullName).Replace('\\', '/');
        if (!relative.StartsWith("sds/", StringComparison.OrdinalIgnoreCase) || relative.Split('/').Any(part => part is ".." or ".") || !relative.EndsWith(".sds", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{archive.Name} is not an SDS archive inside the selected game's pc/sds folder.");
        string target = "/" + relative.ToLowerInvariant();
        // M2O takes a streamed name of a-z, 0-9 and _ only
        if (target[1..^4].Split('/').Any(part => part.Length == 0 || part.Any(c => c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))))
            throw new InvalidOperationException($"{target} cannot be streamed by M2O: folder and file names may hold only a-z, 0-9 and _.");
        return target;
    }

    /// <summary>
    /// What an export should offer: the archives with pending edits, every shipped archive the toolkit has built
    /// over (it has a backup), and every archive in <c>pc\sds</c> the game does not ship. Unchanged ones drop out
    /// at export.
    /// </summary>
    public static IReadOnlyList<FileInfo> Candidates(IEnumerable<FileInfo> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var found = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (FileInfo archive in pending) found.TryAdd(archive.FullName, archive);
        string sds = Path.Combine(MafiaEnvironment.PcFolder, "sds");
        if (Directory.Exists(sds))
        {
            foreach (string file in Directory.EnumerateFiles(sds, "*.sds", SearchOption.AllDirectories))
            {
                if (!ResourceUnpacker.IsUnpackableGameSds(file)) continue;
                var archive = new FileInfo(file);
                string target = "/" + Path.GetRelativePath(MafiaEnvironment.PcFolder, file).Replace('\\', '/').ToLowerInvariant();
                if (!StockArchives.IsStock(target) || SdsWriter.ListBackups(archive).Count > 0) found.TryAdd(archive.FullName, archive);
            }
        }
        return found.Values.OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Exports the supplied edited archives; season variants are archives of their own.</summary>
    public static IReadOnlyList<M2oExportResult> Export(IReadOnlyList<FileInfo> archives, string destination, IProgress<string>? progress = null)
        => Export(archives, destination, progress, null, null);

    /// <param name="libraries">The game's material libraries; null reads them from the game folder.</param>
    /// <param name="stockCopy">For a target: null when the game does not ship it, else a file holding its shipped
    /// bytes (null file: none was found). Null uses <see cref="StockArchives"/>.</param>
    internal static IReadOnlyList<M2oExportResult> Export(IReadOnlyList<FileInfo> archives, string destination, IProgress<string>? progress,
        IReadOnlyList<MaterialLibrary>? libraries, Func<FileInfo, string, (bool Stock, FileInfo? Copy)>? stockCopy)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (archives.Count == 0) throw new InvalidOperationException("No edited archives to export.");
        string fullDestination = Path.GetFullPath(destination);
        if (Directory.Exists(fullDestination) || File.Exists(fullDestination)) throw new IOException("That export folder already exists. Choose a new name to keep the previous export intact.");
        string[] targets = archives.Select(TargetOf).ToArray();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length) throw new InvalidOperationException("The export contains duplicate SDS targets.");
        string[] stems = targets.Select(t => Path.GetFileNameWithoutExtension(t)).ToArray();
        if (stems.Distinct(StringComparer.Ordinal).Count() != stems.Length)
            throw new InvalidOperationException("Two archives share a file name, and each archive's materials are named after it. Export them separately.");
        if (stems.FirstOrDefault(s => s.StartsWith("default", StringComparison.Ordinal)) is { } shadowing)
            throw new InvalidOperationException($"{shadowing}.sds cannot be exported: its material library would be named like the game's own (default*).");
        stockCopy ??= (archive, target) => (StockArchives.IsStock(target), StockArchives.ResolveStockCopy(archive, target));
        libraries ??= ArchiveMaterials.LoadLibraries(MafiaEnvironment.GameRoot);

        string stage = fullDestination + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            var results = new List<M2oExportResult>();
            for (int i = 0; i < archives.Count; i++)
            {
                progress?.Report($"Exporting {i + 1} of {archives.Count}: {archives[i].Name}");
                try
                {
                    if (ExportOne(archives[i], targets[i], stems[i], stage, fullDestination, libraries, stockCopy) is { } result) results.Add(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException($"Could not export {archives[i].Name}: {ex.Message}", ex);
                }
            }
            if (results.Count == 0) return results;
            Directory.Delete(Path.Combine(stage, ".edited"), true);
            Directory.Move(stage, fullDestination);
            return results;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    private static M2oExportResult? ExportOne(FileInfo archive, string target, string stem, string stage, string published,
        IReadOnlyList<MaterialLibrary> libraries, Func<FileInfo, string, (bool Stock, FileInfo? Copy)> stockCopy)
    {
        string relative = StreamFolder + target;
        (bool stock, FileInfo? copy) = stockCopy(archive, target);
        M2oExportKind kind;
        PatchDiffResult? diff = null;
        string written;
        // the materials are read off the archive as the players will have it
        string edited = Path.Combine(stage, ".edited", stem + ".sds");
        Directory.CreateDirectory(Path.GetDirectoryName(edited)!);
        if (stock)
        {
            if (copy == null)
            {
                throw new InvalidOperationException($"{target} no longer holds the game's own bytes, and no backup of them was found "
                    + "(backups\\ or BackupSDS\\ beside it). A patch must be made against the shipped archive: restore it, or verify the game files.");
            }
            relative += ".patch";
            if (PatchExporter.TryExport(archive, copy, Path.Combine(stage, relative), out SdsArchive packed) is not { } patch) return null;
            using (FileStream output = File.Create(edited)) packed.Save(output, new SdsWriteOptions());
            kind = M2oExportKind.Patch;
            diff = patch.Result;
            written = Path.Combine(stage, relative);
        }
        else
        {
            string extracted = MafiaEnvironment.ExtractedDir(archive);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml")))
                throw new FileNotFoundException($"Extracted content not found for {archive.Name}; nothing to export.", Path.Combine(extracted, "SDSContent.xml"));
            written = Path.Combine(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(written)!);
            SdsArchive packed = SdsArchive.Pack(extracted, GameProfile.MafiaII, SdsWriter.MemoryFor(archive, extracted));
            using (FileStream output = File.Create(written)) packed.Save(output, new SdsWriteOptions());
            File.Copy(written, edited);
            kind = M2oExportKind.NewArchive;
        }

        ArchiveMaterials.Report report = ArchiveMaterials.Read(new FileInfo(edited), libraries);
        File.Delete(edited);
        if (report.Missing.Count > 0)
        {
            throw new InvalidOperationException($"{report.Missing.Count} material(s) its meshes use are defined nowhere: "
                + string.Join(", ", report.Missing.Take(5).Select(m => m.Name ?? $"0x{m.Hash:x16}")) + ". Add them to a library, or reassign those meshes.");
        }
        // the game keeps the definition it has of a material it knows, so a changed one would draw as shipped
        if (report.Materials.Where(m => m.Origin == MaterialOrigin.Changed).Select(m => m.Name).ToList() is { Count: > 0 } changed)
        {
            throw new InvalidOperationException($"it changes {changed.Count} of the game's own material(s) ({string.Join(", ", changed.Take(5))}), "
                + "which M2O cannot redefine. Make a new material instead.");
        }
        string library = Path.Combine(stage, StreamFolder, "materials", stem + ".mtl");
        int materials = ArchiveMaterials.SaveLibrary(report, libraries, library);
        return new M2oExportResult(archive.FullName, target, Path.Combine(published, Path.GetRelativePath(stage, written)), kind, diff,
            materials > 0 ? Path.Combine(published, StreamFolder, "materials", stem + ".mtl") : null, materials);
    }
}
