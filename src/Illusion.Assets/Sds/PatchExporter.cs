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
