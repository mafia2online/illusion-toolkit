using System.Security.Cryptography;
using System.Text.Json;

namespace Illusion.Assets.Compiled;

/// <summary>A recoverable workspace-only commit. Native SDS files are never journal targets.</summary>
internal sealed class CompiledProxySaveJournal : IDisposable
{
    private sealed record SavedFile(string Path, string Backup, string Hash);
    private sealed record Record(string[] Folders, string[] Metadata, SavedFile[] Files);
    private readonly string _root, _directory;
    private readonly FileStream _lease;
    private readonly Record _record;
    private bool _complete;
    internal static string Workspace => Path.Combine(MafiaEnvironment.ResourcesFolder!, ".illusion", "linked-proxies");

    internal CompiledProxySaveJournal(IEnumerable<string> folders, IEnumerable<string> metadata)
    {
        _root = Path.GetFullPath(MafiaEnvironment.ResourcesFolder!); Directory.CreateDirectory(Workspace);
        _lease = new(Path.Combine(Workspace, "save.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _directory = Path.Combine(Workspace, "journal");
        try
        {
            RecoverLocked(_root, _directory);
            Directory.CreateDirectory(_directory);
            string Relative(string p) => Path.GetRelativePath(_root, ValidatePath(_root, p));
            var native = folders.Select(p => ValidatePath(_root, p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (native.Any(p => !p.EndsWith(".sds", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Proxy commits may target only extracted native archive folders.");
            var mappings = metadata.Select(p => ValidatePath(_root, p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var paths = native.SelectMany(p => Directory.GetFiles(p)).Concat(mappings.Where(File.Exists)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var files = new List<SavedFile>();
            for (int i = 0; i < paths.Length; i++)
            {
                string backup = i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin";
                File.Copy(paths[i], Path.Combine(_directory, backup));
                files.Add(new(Relative(paths[i]), backup, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths[i])))));
            }
            _record = new(native.Select(Relative).ToArray(), mappings.Select(Relative).ToArray(), files.ToArray());
            AtomicFile.WriteAllBytes(Path.Combine(_directory, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(_record));
        }
        catch { _lease.Dispose(); throw; }
    }
    internal void Commit()
    {
        AtomicFile.WriteAllBytes(Path.Combine(_directory, "committed"), [1]); _complete = true;
    }
    public void Dispose()
    {
        try
        {
            if (!_complete) Restore(_root, _directory, _record);
            Directory.Delete(_directory, true);
        }
        finally { _lease.Dispose(); TryRemoveLease(); }
    }
    internal static void Recover()
    {
        if (!Directory.Exists(Path.Combine(Workspace, "journal"))) return;
        var lease = new FileStream(Path.Combine(Workspace, "save.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { RecoverLocked(Path.GetFullPath(MafiaEnvironment.ResourcesFolder!), Path.Combine(Workspace, "journal")); }
        finally { lease.Dispose(); TryRemoveLease(); }
    }
    private static void RecoverLocked(string root, string directory)
    {
        if (!Directory.Exists(directory)) return;
        string path = Path.Combine(directory, "journal.json");
        if (!File.Exists(Path.Combine(directory, "committed")) && File.Exists(path))
            Restore(root, directory, JsonSerializer.Deserialize<Record>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Invalid proxy save journal."));
        Directory.Delete(directory, true);
    }
    private static void Restore(string root, string directory, Record record)
    {
        var folders = record.Folders.Select(p => ValidatePath(root, Path.Combine(root, p))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var metadataPaths = record.Metadata.Select(p => ValidatePath(root, Path.Combine(root, p))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (folders.Any(p => !p.EndsWith(".sds", StringComparison.OrdinalIgnoreCase)) || metadataPaths.Any(p =>
            !p.StartsWith(Workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p) != ".json"))
            throw new InvalidDataException("Invalid proxy recovery destinations.");
        var originals = record.Files.Select(f => (File: f, Target: ValidatePath(root, Path.Combine(root, f.Path)), Backup: Path.Combine(directory, f.Backup))).ToArray();
        foreach (var item in originals)
            if ((!folders.Contains(Path.GetDirectoryName(item.Target)!) && !metadataPaths.Contains(item.Target))
                || Path.GetFileName(item.File.Backup) != item.File.Backup || !File.Exists(item.Backup)
                || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.Backup))) != item.File.Hash)
                throw new InvalidDataException("Proxy save recovery backup is missing or invalid.");
        var expected = originals.Select(p => p.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in record.Folders)
        {
            string path = ValidatePath(root, Path.Combine(root, folder));
            if (!path.EndsWith(".sds", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid native proxy journal folder.");
            foreach (string current in Directory.GetFiles(path)) if (!expected.Contains(current)) File.Delete(current);
        }
        foreach (string metadata in record.Metadata)
        {
            string path = ValidatePath(root, Path.Combine(root, metadata));
            if (!path.StartsWith(Workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid proxy mapping recovery path.");
            if (!expected.Contains(path) && File.Exists(path)) File.Delete(path);
        }
        foreach (var item in originals) AtomicFile.WriteAllBytes(item.Target, File.ReadAllBytes(item.Backup));
    }
    private static string ValidatePath(string root, string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Proxy workspace path escapes resources.");
        return full;
    }
    private static void TryRemoveLease()
    {
        // Another document can acquire the exclusive lease as soon as it is released.
        // In that case its open handle protects the file and cleanup is deferred.
        try { File.Delete(Path.Combine(Workspace, "save.lock")); }
        catch (IOException) { }
    }
}
