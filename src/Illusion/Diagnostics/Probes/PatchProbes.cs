using System.Globalization;
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
        if (!CheckOutputPath(basePath, outputPath)) return;

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


    /// <summary><c>--list-frames &lt;base.sds&gt; [filter]</c> — named frames in a district's scene.</summary>
    public static void RunListFrames(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Report("usage: --list-frames <base.sds> [filter]");
            return;
        }

        SdsArchive archive = SdsArchive.Open(args[1]);
        var author = new ScenePatchAuthor(archive);
        var names = author.FrameNames();
        string? filter = args.Length >= 3 ? args[2] : null;

        var shown = filter is null
            ? names
            : names.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var log = new StringBuilder();
        log.AppendLine($"archive : {args[1]}");
        log.AppendLine($"frames  : {names.Count} named" + (filter is null ? "" : $", {shown.Count} matching '{filter}'"));
        foreach (string name in shown)
        {
            log.AppendLine("  " + name);
        }

        Report(log.ToString().TrimEnd());
    }

    /// <summary>
    /// <c>--remove-frames &lt;base.sds&gt; &lt;out.sds.patch&gt; --frame &lt;name|0xhash&gt;... [--collision-radius &lt;r&gt;]</c>
    /// </summary>
    public static void RunRemoveFrames(string[] args)
    {
        if (args.Length < 5)
        {
            Report("usage: --remove-frames <base.sds> <out.sds.patch> --frame <name|0xhash>... [--collision-radius <r>]");
            return;
        }

        string basePath = args[1];
        string outputPath = args[2];

        if (!CheckOutputPath(basePath, outputPath)) return;
        if (!File.Exists(basePath))
        {
            Report($"base archive not found: {basePath}");
            return;
        }

        var selectors = new List<string>();
        float radius = 0.0f; // see ScenePatchAuthor.RemoveFrames — proximity pairing is unsound by default

        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--frame" && i + 1 < args.Length)
            {
                selectors.Add(args[++i]);
            }
            else if (args[i] == "--collision-radius" && i + 1 < args.Length
                     && float.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            {
                radius = parsed;
            }
            else
            {
                Report($"unrecognised argument: {args[i]}");
                return;
            }
        }

        if (selectors.Count == 0)
        {
            Report("no --frame selectors given");
            return;
        }

        var log = new StringBuilder();
        try
        {
            SdsArchive archive = SdsArchive.Open(basePath);
            var author = new ScenePatchAuthor(archive);
            RemovalResult result = author.RemoveFrames(selectors, radius);

            log.AppendLine($"base     : {basePath}");
            log.AppendLine($"selectors: {selectors.Count}, matched {result.MatchedFrames}");
            log.AppendLine($"frames   : {result.DeletedFrames} removed (children included)");
            log.AppendLine($"collision: {result.DeletedCollisionInstances} placements removed (radius {radius})");
            foreach (string missed in result.Unmatched)
            {
                log.AppendLine($"  no match: {missed}");
            }

            SdsPatchFile patch = author.Build();
            using (var output = File.Create(outputPath))
            {
                patch.Save(output);
            }

            using var written = File.OpenRead(outputPath);
            SdsPatchFile reloaded = SdsPatchFile.Load(written);

            log.AppendLine($"output   : {outputPath} ({new FileInfo(outputPath).Length} bytes)");
            log.AppendLine($"verified : {reloaded.SkippedEntryIndices.Count} skipped "
                           + $"{Format(reloaded.SkippedEntryIndices)}, {reloaded.Entries.Count} carried");
            foreach (var entry in reloaded.Entries)
            {
                log.AppendLine($"           type {entry.TypeId}, {entry.Data?.Length ?? 0} bytes");
            }
        }
        catch (InvalidOperationException ex)
        {
            log.AppendLine($"refused: {ex.Message}");
        }

        Report(log.ToString().TrimEnd());
    }


    /// <summary>
    /// <c>--patch-diff &lt;base.sds&gt; &lt;file.sds.patch&gt;</c> — which frames a patch adds or removes,
    /// by parsing the FrameResource it carries against the one in the base archive.
    /// </summary>
    public static void RunPatchDiff(string[] args)
    {
        if (args.Length < 3 || !File.Exists(args[1]) || !File.Exists(args[2]))
        {
            Report("usage: --patch-diff <base.sds> <file.sds.patch>");
            return;
        }

        SdsArchive archive = SdsArchive.Open(args[1]);
        using var input = File.OpenRead(args[2]);
        SdsPatchFile patch = SdsPatchFile.Load(input);

        var log = new StringBuilder();
        log.AppendLine($"base    : {args[1]}");
        log.AppendLine($"patch   : {args[2]}");
        log.AppendLine($"skipped : {Format(patch.SkippedEntryIndices)}");

        int frameOrdinal = OrdinalOfType(archive, "FrameResource");
        if (frameOrdinal < 0)
        {
            Report("The base archive has no FrameResource; frame comparison is unavailable.");
            return;
        }
        var baseNames = FrameNamesOf(archive.Entries[frameOrdinal].Data);
        int frameTypeId = TypeIdOf(archive, "FrameResource");
        byte[]? carried = patch.Entries.FirstOrDefault(e => e.TypeId == frameTypeId)?.Data;

        if (carried is null)
        {
            log.AppendLine("carries no FrameResource — nothing to compare");
            Report(log.ToString().TrimEnd());
            return;
        }

        var patchedNames = FrameNamesOf(carried);
        var removed = baseNames.Except(patchedNames, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var addedNames = patchedNames.Except(baseNames, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

        log.AppendLine($"frames  : {baseNames.Count} base, {patchedNames.Count} patched");
        log.AppendLine($"removed : {removed.Count}");
        foreach (string name in removed.Take(64))
        {
            log.AppendLine("  - " + name);
        }

        log.AppendLine($"added   : {addedNames.Count}");
        foreach (string name in addedNames.Take(64))
        {
            log.AppendLine("  + " + name);
        }

        Report(log.ToString().TrimEnd());
    }


    /// <summary>
    /// <c>--frame-collision &lt;base.sds&gt; &lt;frameName&gt;</c> — where a frame sits and which collision
    /// placements are near it, to check whether proximity is a sound way to pair the two.
    /// </summary>
    public static void RunFrameCollision(string[] args)
    {
        if (args.Length < 3 || !File.Exists(args[1]))
        {
            Report("usage: --frame-collision <base.sds> <frameName>");
            return;
        }

        SdsArchive archive = SdsArchive.Open(args[1]);
        string wanted = args[2];
        int frameOrdinal = OrdinalOfType(archive, "FrameResource");
        if (frameOrdinal < 0)
        {
            Report("The archive has no FrameResource.");
            return;
        }

        var frames = new Illusion.Formats.Frames.FrameResource();
        using (var source = new MemoryStream(archive.Entries[frameOrdinal].Data ?? []))
        {
            frames.ReadFromFile(source);
        }

        var frame = frames.FrameObjects.Values
            .OfType<Illusion.Formats.Frames.ObjectTypes.FrameObjectBase>()
            .FirstOrDefault(f => string.Equals(f.Name.String, wanted, StringComparison.OrdinalIgnoreCase));

        var log = new StringBuilder();
        log.AppendLine($"archive : {args[1]}");

        if (frame is null)
        {
            log.AppendLine($"frame '{wanted}' not found");
            Report(log.ToString().TrimEnd());
            return;
        }

        System.Numerics.Vector3 origin = frame.WorldTransform.Translation;
        log.AppendLine($"frame   : {frame.Name.String}  hash 0x{frame.Name.Hash:X16}");
        log.AppendLine($"position: {origin.X:F2}, {origin.Y:F2}, {origin.Z:F2}");
        log.AppendLine($"children: {frame.Children.Count}");

        int collisionOrdinal = OrdinalOfType(archive, "Collisions");
        if (collisionOrdinal < 0)
        {
            log.AppendLine("archive has no Collisions resource");
            Report(log.ToString().TrimEnd());
            return;
        }

        Illusion.Formats.Collisions.CollisionFile collisions;
        using (var source = new MemoryStream(archive.Entries[collisionOrdinal].Data ?? []))
        {
            collisions = Illusion.Formats.Collisions.CollisionFile.Read(source);
        }

        int hashMatches = collisions.Instances.Count(i => i.Hash == frame.Name.Hash);
        log.AppendLine($"collision: {collisions.Instances.Count} placements, {collisions.Meshes.Count} meshes");
        log.AppendLine($"          {hashMatches} placement(s) whose hash equals the frame name hash");

        var nearest = collisions.Instances
            .Select(i => new { Instance = i, Distance = System.Numerics.Vector3.Distance(origin, i.Position) })
            .OrderBy(x => x.Distance)
            .Take(8)
            .ToList();

        log.AppendLine("nearest placements:");
        foreach (var entry in nearest)
        {
            log.AppendLine($"          {entry.Distance,8:F2}  hash 0x{entry.Instance.Hash:X16}  group {entry.Instance.Group}");
        }

        Report(log.ToString().TrimEnd());
    }

    private static HashSet<string> FrameNamesOf(byte[]? resource)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (resource is null)
        {
            return names;
        }

        var frames = new Illusion.Formats.Frames.FrameResource();
        using var source = new MemoryStream(resource);
        frames.ReadFromFile(source);

        foreach (var frame in frames.FrameObjects.Values.OfType<Illusion.Formats.Frames.ObjectTypes.FrameObjectBase>())
        {
            if (!string.IsNullOrEmpty(frame.Name.String))
            {
                names.Add(frame.Name.String);
            }
        }

        return names;
    }

    private static int TypeIdOf(SdsArchive archive, string typeName)
    {
        for (int i = 0; i < archive.ResourceTypes.Count; i++)
        {
            if (string.Equals(archive.ResourceTypes[i].Name, typeName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int OrdinalOfType(SdsArchive archive, string typeName)
    {
        int typeId = TypeIdOf(archive, typeName);
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            if (archive.Entries[i].TypeId == typeId)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Format(List<int> ordinals) =>
        ordinals.Count == 0 ? "" : "[" + string.Join(", ", ordinals.Take(24)) + (ordinals.Count > 24 ? ", ..." : "") + "]";

    private static bool CheckOutputPath(string basePath, string outputPath)
    {
        if (!string.Equals(Path.GetFullPath(basePath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase)) return true;
        Report("Refused: the output patch would overwrite the base archive.");
        return false;
    }

    private static void Report(string text)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_patch.txt");
        File.WriteAllText(outFile, text);
    }
}
