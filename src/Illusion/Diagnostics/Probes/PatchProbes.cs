using System.IO;
using System.Text;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Headless <c>.sds.patch</c> authoring and inspection, so a patch can be produced and checked
/// without the editor: <c>Illusion.exe --build-patch</c> and <c>--dump-patch</c>.
/// </summary>
internal static class PatchProbes
{
    /// <summary>
    /// <c>--build-patch &lt;base.sds&gt; &lt;out.sds.patch&gt; [--delete-type Name]... [--delete &lt;ordinal&gt;]...</c>
    /// </summary>
    public static void RunBuildPatch(string[] args)
    {
        if (args.Length < 3)
        {
            Report("usage: --build-patch <base.sds> <out.sds.patch> [--delete-type <Name>]... [--delete <ordinal>]...");
            return;
        }

        string basePath = args[1];
        string outputPath = args[2];

        if (!File.Exists(basePath))
        {
            Report($"base archive not found: {basePath}");
            return;
        }

        SdsArchive archive = SdsArchive.Open(basePath);
        var builder = new SdsPatchBuilder(archive);
        var log = new StringBuilder();

        log.AppendLine($"base    : {basePath}");
        log.AppendLine($"resources: {archive.Entries.Count}");

        for (int i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--delete-type" when i + 1 < args.Length:
                {
                    string typeName = args[++i];
                    var ordinals = builder.OrdinalsOfType(typeName);
                    if (ordinals.Count == 0)
                    {
                        Report($"no '{typeName}' resources in {Path.GetFileName(basePath)}");
                        return;
                    }

                    builder.DeleteType(typeName);
                    log.AppendLine($"delete  : {ordinals.Count} x {typeName} (ordinals {ordinals[0]}..{ordinals[^1]})");
                    break;
                }

                case "--delete" when i + 1 < args.Length && int.TryParse(args[++i], out int ordinal):
                {
                    builder.Delete(ordinal);
                    log.AppendLine($"delete  : ordinal {ordinal}");
                    break;
                }

                default:
                    Report($"unrecognised argument: {args[i]}");
                    return;
            }
        }

        SdsPatchFile patch;
        try
        {
            patch = builder.Build();
        }
        catch (InvalidOperationException ex)
        {
            Report($"refused: {ex.Message}");
            return;
        }

        using (var output = File.Create(outputPath))
        {
            patch.Save(output);
        }

        // Read the file back so what is reported is what landed on disk, not what was intended.
        using var written = File.OpenRead(outputPath);
        SdsPatchFile reloaded = SdsPatchFile.Load(written);

        log.AppendLine($"output  : {outputPath} ({new FileInfo(outputPath).Length} bytes)");
        log.AppendLine($"verified: version {reloaded.Version}, {reloaded.SkippedEntryIndices.Count} skipped, "
                       + $"{reloaded.DeltaEntryIndices.Count} delta, {reloaded.Entries.Count} carried");

        Report(log.ToString().TrimEnd());
    }

    /// <summary><c>--dump-patch &lt;file.sds.patch&gt;</c> — what a patch does, without applying it.</summary>
    public static void RunDumpPatch(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Report("usage: --dump-patch <file.sds.patch>");
            return;
        }

        using var input = File.OpenRead(args[1]);
        SdsPatchFile patch = SdsPatchFile.Load(input);

        var log = new StringBuilder();
        log.AppendLine($"patch   : {args[1]} ({new FileInfo(args[1]).Length} bytes)");
        log.AppendLine($"version : {patch.Version}");
        log.AppendLine($"types   : {patch.ResourceTypes.Count}");
        log.AppendLine($"skipped : {patch.SkippedEntryIndices.Count} {Format(patch.SkippedEntryIndices)}");
        log.AppendLine($"delta   : {patch.DeltaEntryIndices.Count} {Format(patch.DeltaEntryIndices)}");
        log.AppendLine($"carried : {patch.DeclaredResourceCount} declared, {patch.Payload.Length} payload bytes");
        if (patch.DeltaEntryIndices.Count > 0)
        {
            log.AppendLine("          records not walked: this patch carries binary deltas");
        }

        foreach (var entry in patch.Entries.Take(16))
        {
            log.AppendLine($"          type {entry.TypeId}, version {entry.Version}, {entry.Data?.Length ?? 0} bytes");
        }

        Report(log.ToString().TrimEnd());
    }

    private static string Format(List<int> ordinals) =>
        ordinals.Count == 0 ? "" : "[" + string.Join(", ", ordinals.Take(24)) + (ordinals.Count > 24 ? ", ..." : "") + "]";

    private static void Report(string text)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_patch.txt");
        File.WriteAllText(outFile, text);
    }
}
