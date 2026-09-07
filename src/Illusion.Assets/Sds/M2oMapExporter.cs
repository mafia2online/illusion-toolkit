using System.Text.Json;

namespace Illusion.Assets.Sds;

/// <summary>Publishes native patches and their manifest together in a new M2O map folder.</summary>
public static class M2oMapExporter
{
    /// <summary>The native target path relative to the game's pc directory.</summary>
    public static string TargetOf(FileInfo archive)
    {
        string relative = Path.GetRelativePath(MafiaEnvironment.PcFolder, archive.FullName).Replace('\\', '/');
        if (!relative.StartsWith("sds/", StringComparison.OrdinalIgnoreCase) || relative.Split('/').Any(part => part is ".." or ".") || !relative.EndsWith(".sds", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{archive.Name} is not an SDS archive inside the selected game's pc/sds folder.");
        return "/" + relative.ToLowerInvariant();
    }

    /// <summary>Exports only the supplied edited archives; season variants must be edited separately.</summary>
    public static IReadOnlyList<PatchExportResult> Export(IReadOnlyList<FileInfo> archives, string destination, IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (archives.Count == 0) throw new InvalidOperationException("No edited archives to export.");
        string fullDestination = Path.GetFullPath(destination);
        if (Directory.Exists(fullDestination) || File.Exists(fullDestination)) throw new IOException("That export folder already exists. Choose a new name to keep the previous export intact.");
        string[] targets = archives.Select(TargetOf).ToArray();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length) throw new InvalidOperationException("The export contains duplicate SDS targets.");
        string stage = fullDestination + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            var results = new List<PatchExportResult>();
            var entries = new List<object>();
            for (int i = 0; i < archives.Count; i++)
            {
                progress?.Report($"Exporting {i + 1} of {archives.Count}: {archives[i].Name}");
                string patch = targets[i].TrimStart('/') + ".patch";
                PatchExportResult? exported;
                try { exported = PatchExporter.TryExport(archives[i], Path.Combine(stage, patch)); }
                catch (Exception ex) { throw new InvalidOperationException($"Could not export {archives[i].Name}: {ex.Message}", ex); }
                if (exported is not { } result) continue;
                results.Add(result with { PatchPath = Path.Combine(fullDestination, patch) });
                entries.Add(new { sds = targets[i], patch });
            }
            if (results.Count == 0) return results;
            File.WriteAllText(Path.Combine(stage, "map_patches.json"), JsonSerializer.Serialize(new { patches = entries }, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Move(stage, fullDestination);
            return results;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }
}
