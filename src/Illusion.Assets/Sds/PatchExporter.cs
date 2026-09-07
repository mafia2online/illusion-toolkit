using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Assets.Sds;

/// <summary>One archive's export: where the patch went, and what it carries.</summary>
/// <param name="Archive">The game archive the patch applies to.</param>
/// <param name="PatchPath">The file written.</param>
/// <param name="Result">What the diff found.</param>
public readonly record struct PatchExportResult(string Archive, string PatchPath, PatchDiffResult Result);

/// <summary>Diffs saved working copies against read-only game archives and exports native patches.</summary>
public static class PatchExporter
{
    /// <summary>The extension every exported patch carries, matching the game's own convention.</summary>
    public const string PatchExtension = ".sds.patch";

    /// <summary>
    /// The other season's copy of a district, or null when there is none.
    /// </summary>
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
        => TryExport(sds, outputPath) ?? throw new InvalidOperationException($"{sds.Name} is unchanged; no patch is needed.");

    /// <summary>Exports a changed archive, or returns null without writing a file when unchanged.</summary>
    public static PatchExportResult? TryExport(FileInfo sds, string outputPath)
        => TryExport(sds, outputPath, out _, out _);

    private static PatchExportResult? TryExport(FileInfo sds, string outputPath, out SdsArchive original, out SdsArchive edited)
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

        edited = SdsArchive.Pack(extracted, GameProfile.MafiaII);
        original = SdsArchive.Open(sds.FullName);

        if (SdsPatchDiff.TryBetween(original, edited) is not { } diff) return null;
        (SdsPatchFile patch, PatchDiffResult result) = diff;

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
    /// Only matching frame removals carry across; resource ordinals and other edits are season-specific.
    /// </remarks>
    public static IReadOnlyList<PatchExportResult> ExportWithSeasonVariant(FileInfo sds, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(sds);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        FileInfo? twin = SeasonVariantOf(sds);
        string? twinPath = twin is null ? null : Path.Combine(Path.GetDirectoryName(outputPath) ?? string.Empty, SuggestFileName(twin));
        if (twinPath is not null && string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(twinPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The chosen filename conflicts with the seasonal patch filename.");
        PatchExportResult result = TryExport(sds, outputPath, out SdsArchive original, out SdsArchive edited)
            ?? throw new InvalidOperationException($"{sds.Name} is unchanged; no patch is needed.");
        var exported = new List<PatchExportResult> { result };
        if (twin is null)
        {
            return exported;
        }

        var removedNames = ScenePatchAuthor
            .FrameNamesOf(FrameResourceOf(original))
            .Except(ScenePatchAuthor.FrameNamesOf(FrameResourceOf(edited)), StringComparer.Ordinal)
            .ToList();

        if (removedNames.Count == 0)
        {
            return exported;
        }

        var author = new ScenePatchAuthor(SdsArchive.Open(twin.FullName));
        RemovalResult removal = author.RemoveFrames(removedNames);
        if (removal.DeletedFrames == 0 && removal.DeletedCollisionInstances == 0) return exported;

        SdsPatchFile twinPatch = author.Build();
        using (FileStream output = File.Create(twinPath!))
        {
            twinPatch.Save(output);
        }

        exported.Add(new PatchExportResult(
            twin.FullName,
            twinPath!,
            new PatchDiffResult(Changed: twinPatch.Entries.Count, Removed: 0, Added: 0)));

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

        FileInfo[] sources = archives.ToArray();
        if (sources.Select(SuggestFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new IOException("Several archives would export to the same filename. Export them separately.");
        var exported = new List<PatchExportResult>();
        foreach (FileInfo sds in sources)
        {
            if (TryExport(sds, Path.Combine(targetFolder, SuggestFileName(sds))) is { } result)
            {
                exported.Add(result);
            }
        }

        return exported;
    }
}
