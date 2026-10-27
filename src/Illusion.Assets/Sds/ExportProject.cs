using System.Text.Json;
using System.Text.Json.Serialization;

namespace Illusion.Assets.Sds;

/// <summary>Where a Build / Export writes the ticked archives.</summary>
public enum ExportDestination
{
    /// <summary>Packed into the game's own archives, each kept in its <c>backups</c> folder first.</summary>
    Game,
    /// <summary>An M2O server resource: <c>stream\</c> at the root (see <see cref="M2oMapExporter"/>).</summary>
    M2oResource,
    /// <summary>The same files at their game paths, with no <c>stream\</c> folder around them.</summary>
    PatchFiles,
}

/// <summary>
/// A saved export: a name, the archives it ships, where it goes. Re-exporting one writes the same set to the same
/// folder, so an author iterating on a map ships what they shipped last time plus what they ticked since.
/// Kept beside the working copies (<c>&lt;game&gt;\resources\.exports\&lt;name&gt;.json</c>), one file each.
/// </summary>
public sealed record ExportProject(string Name, ExportDestination Destination, string Output, IReadOnlyList<string> Archives)
{
    private const string Extension = ".json";
    private const string LastUsedFile = "last.txt";

    /// <summary>The folder projects are kept in.</summary>
    public static string Folder => Path.Combine(MafiaEnvironment.ResourcesFolder ?? throw new InvalidOperationException("the game folder is not set"), ".exports");

    /// <summary>Why <paramref name="name"/> cannot name a project, or null when it can.</summary>
    public static string? CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the export a name.";
        if (name.Trim() != name) return "A name cannot start or end with a space.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith('.')) return $"'{name}' cannot be used as a file name.";
        return null;
    }

    /// <summary>The archives as files of the game the toolkit has open.</summary>
    public IReadOnlyList<FileInfo> ArchiveFiles() => [.. Archives.Select(a => new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, a.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))))];

    /// <summary>The game path of <paramref name="archive"/>, as projects store it (<c>/sds/city/uppertown.sds</c>).</summary>
    public static string GamePathOf(FileInfo archive) => "/" + Path.GetRelativePath(MafiaEnvironment.PcFolder, archive.FullName).Replace('\\', '/').ToLowerInvariant();

    /// <summary>Every saved project, by name.</summary>
    public static IReadOnlyList<ExportProject> LoadAll()
    {
        if (!Directory.Exists(Folder)) return [];
        var projects = new List<ExportProject>();
        foreach (string file in Directory.EnumerateFiles(Folder, "*" + Extension))
        {
            try
            {
                if (JsonSerializer.Deserialize<ExportProject>(File.ReadAllText(file), Json) is { } project && CheckName(project.Name) == null
                    && string.Equals(Path.GetFileNameWithoutExtension(file), project.Name, StringComparison.OrdinalIgnoreCase))
                {
                    projects.Add(project with { Archives = project.Archives ?? [] });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // a project that cannot be read is not offered; its file stays for the user to look at
            }
        }
        return [.. projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Writes the project, whole or not at all, and makes it the one the window opens on next.</summary>
    public void Save()
    {
        if (CheckName(Name) is { } problem) throw new InvalidOperationException(problem);
        Directory.CreateDirectory(Folder);
        AtomicFile.WriteAllBytes(Path.Combine(Folder, Name + Extension), JsonSerializer.SerializeToUtf8Bytes(this, Json));
        File.WriteAllText(Path.Combine(Folder, LastUsedFile), Name);
    }

    /// <summary>The name of the project used last, or null.</summary>
    public static string? LastUsed()
    {
        try
        {
            string file = Path.Combine(Folder, LastUsedFile);
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// Publishes an export folder whole: the files are written to a stage beside it, then moved into place. An
/// existing folder is replaced only when <c>replace</c> is asked for and it is one an export wrote (it carries
/// <see cref="MarkerName"/>), so a re-export can never delete a folder of the user's.
/// </summary>
public static class ExportFolder
{
    /// <summary>The file every export folder carries at its root.</summary>
    public const string MarkerName = ".illusion-export";

    /// <summary>Whether <paramref name="folder"/> is one an export wrote.</summary>
    public static bool IsExport(string folder) => File.Exists(Path.Combine(folder, MarkerName));

    /// <summary>
    /// Runs <paramref name="write"/> on an empty stage, then publishes it at <paramref name="destination"/>. Nothing is
    /// published when it returns false (nothing to export); a throw leaves the destination as it was.
    /// </summary>
    public static bool Publish(string destination, bool replace, Func<string, bool> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(write);
        string full = Path.GetFullPath(destination);
        if (File.Exists(full)) throw new IOException($"{full} is a file, not a folder.");
        bool exists = Directory.Exists(full);
        if (exists && !replace) throw new IOException("That export folder already exists. Choose a new name to keep the previous export intact.");
        if (exists && !IsExport(full) && Directory.EnumerateFileSystemEntries(full).Any())
            throw new IOException($"{full} holds files no export wrote. Choose an empty folder, or one an earlier export made.");

        string stage = full + ".tmp-" + Guid.NewGuid().ToString("N");
        string old = full + ".old-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            if (!write(stage)) return false;
            File.WriteAllText(Path.Combine(stage, MarkerName), "Written by Illusion Toolkit. A later export of the same project replaces this folder.\n");
            if (exists) Directory.Move(full, old);
            try
            {
                Directory.Move(stage, full);
            }
            catch
            {
                if (exists && !Directory.Exists(full)) Directory.Move(old, full);
                throw;
            }
            return true;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            try
            {
                if (Directory.Exists(old)) Directory.Delete(old, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the previous export stays beside the new one; it is the new one that counts
            }
        }
    }
}
