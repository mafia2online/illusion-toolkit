using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Assets.Sds;

/// <summary>One archive's export: where the patch went, and what it carries.</summary>
/// <param name="Archive">The game archive the patch applies to.</param>
/// <param name="PatchPath">The file written.</param>
/// <param name="Result">What the diff found.</param>
public readonly record struct PatchExportResult(string Archive, string PatchPath, PatchDiffResult Result);

/// <summary>
/// Exports this session's edits as <c>.sds.patch</c> files instead of repacking the game's archives.
/// </summary>
/// <remarks>
/// The game's <c>.sds</c> files are opened read-only and never written, moved or backed up — the
/// edited archive is packed in memory from the extracted folder and diffed against the original.
/// This is the difference between exporting and <see cref="SdsWriter.PackSds(System.IO.FileInfo, bool)"/>,
/// which replaces the archive in place.
/// </remarks>
public static class PatchExporter
{
    /// <summary>The extension every exported patch carries, matching the game's own convention.</summary>
    public const string PatchExtension = ".sds.patch";

    /// <summary>
    /// The other season's copy of a district, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Districts ship twice: <c>sandisland.sds</c> for summer and <c>sandisland_z.sds</c> for winter.
    /// The two hold different geometry at different resource ordinals, so a patch built against one
    /// is silently inert in a session running the other — the engine never even asks for it.
    /// </remarks>
    public static FileInfo? SeasonVariantOf(FileInfo sds)
    {
        ArgumentNullException.ThrowIfNull(sds);

        string stem = Path.GetFileNameWithoutExtension(sds.Name);
        string twin = stem.EndsWith("_z", StringComparison.OrdinalIgnoreCase)
            ? stem[..^2]
            : stem + "_z";

        var candidate = new FileInfo(Path.Combine(sds.DirectoryName ?? string.Empty, twin + ".sds"));
        return candidate.Exists ? candidate : null;
    }

    /// <summary>The name a patch for <paramref name="sds"/> should be given.</summary>
    public static string SuggestFileName(FileInfo sds)
    {
        ArgumentNullException.ThrowIfNull(sds);
        return Path.GetFileNameWithoutExtension(sds.Name) + PatchExtension;
    }

    /// <summary>
    /// Writes a patch for <paramref name="sds"/> to <paramref name="outputPath"/>, expressing every
    /// edit made to it this session.
    /// </summary>
    /// <exception cref="FileNotFoundException">The archive was never extracted, so there is nothing to diff.</exception>
    /// <exception cref="InvalidOperationException">Nothing changed, so there is no patch to write.</exception>
    public static PatchExportResult Export(FileInfo sds, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(sds);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        string extracted = MafiaEnvironment.ExtractedDir(sds);
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml")))
        {
            throw new FileNotFoundException(
                $"Extracted content not found for {sds.Name} — nothing to diff.",
                Path.Combine(extracted, "SDSContent.xml"));
        }

        // The edited archive is built in memory. Nothing is written next to the game file, and the
        // original is only ever read.
        SdsArchive edited = SdsArchive.Pack(extracted, GameProfile.MafiaII);
        SdsArchive original = SdsArchive.Open(sds.FullName);

        (SdsPatchFile patch, PatchDiffResult result) = SdsPatchDiff.Between(original, edited);

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (FileStream output = File.Create(outputPath))
        {
            patch.Save(output);
        }

        return new PatchExportResult(sds.FullName, outputPath, result);
    }

    /// <summary>
    /// Exports the patch for <paramref name="sds"/> and, when the district ships a season twin, a
    /// second patch that applies the same removals to it.
    /// </summary>
    /// <remarks>
    /// Only removals carry across: they are expressed by frame name, and names are all but identical
    /// between the two copies of a district — 3,387 of 3,399 on sandisland, the rest being the
    /// seasonal geometry itself, which lives in disjoint name ranges. Ordinals and resource bytes are
    /// not portable, which is exactly why the twin is rebuilt rather than copied.
    /// </remarks>
    public static IReadOnlyList<PatchExportResult> ExportWithSeasonVariant(FileInfo sds, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(sds);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var exported = new List<PatchExportResult> { Export(sds, outputPath) };

        FileInfo? twin = SeasonVariantOf(sds);
        if (twin is null)
        {
            return exported;
        }

        // What the edit removed, by name — the only part of a diff that means anything in the twin.
        SdsArchive original = SdsArchive.Open(sds.FullName);
        SdsArchive edited = SdsArchive.Pack(MafiaEnvironment.ExtractedDir(sds), GameProfile.MafiaII);
        var removedNames = ScenePatchAuthor
            .FrameNamesOf(FrameResourceOf(original))
            .Except(ScenePatchAuthor.FrameNamesOf(FrameResourceOf(edited)), StringComparer.Ordinal)
            .ToList();

        if (removedNames.Count == 0)
        {
            return exported;
        }

        var author = new ScenePatchAuthor(SdsArchive.Open(twin.FullName));
        author.RemoveFrames(removedNames);

        string twinPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? string.Empty,
            SuggestFileName(twin));

        using (FileStream output = File.Create(twinPath))
        {
            author.Build().Save(output);
        }

        exported.Add(new PatchExportResult(
            twin.FullName,
            twinPath,
            new PatchDiffResult(Changed: 1, Removed: 0, Added: 0)));

        return exported;
    }

    private static byte[]? FrameResourceOf(SdsArchive archive)
    {
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            int typeId = archive.Entries[i].TypeId;
            if (typeId >= 0 && typeId < archive.ResourceTypes.Count &&
                string.Equals(archive.ResourceTypes[typeId].Name, "FrameResource", StringComparison.Ordinal))
            {
                return archive.Entries[i].Data;
            }
        }

        return null;
    }

    /// <summary>
    /// Exports one patch per archive into <paramref name="targetFolder"/>, naming each after its
    /// archive. Archives that turn out to be unchanged are skipped rather than failing the export.
    /// </summary>
    public static IReadOnlyList<PatchExportResult> ExportAll(IEnumerable<FileInfo> archives, string targetFolder)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);

        var exported = new List<PatchExportResult>();
        foreach (FileInfo sds in archives)
        {
            try
            {
                exported.Add(Export(sds, Path.Combine(targetFolder, SuggestFileName(sds))));
            }
            catch (InvalidOperationException)
            {
                // Identical to the original — nothing to ship for this one.
            }
        }

        return exported;
    }
}
