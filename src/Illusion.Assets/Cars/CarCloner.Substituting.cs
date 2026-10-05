using Illusion.Assets.Sds;
using Illusion.Formats.ResourceFormats;

namespace Illusion.Assets.Cars;

public static partial class CarCloner
{
    /// <summary>
    /// Builds one car under ANOTHER car's name: the target's archive is replaced by the source's model, keyed
    /// throughout by the target's model name. No table is touched — the game goes on listing the target as it
    /// did and finds the source's shape, tuning and sounds in its archive.
    ///
    /// <para>
    /// This is how a car is tried where nothing can be registered — a multiplayer that spawns from a fixed list
    /// of names. A backup is kept of every archive replaced; restoring it (and dropping the working copy) puts
    /// the target back. The target's working copy is replaced as well, with whatever was not built from it.
    /// A winter twin is replaced only where both cars have one.
    /// </para>
    /// </summary>
    public static CarSubstituteOutcome? Substitute(string source, string target, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!MafiaEnvironment.IsInitialized)
        {
            refusal = "the game folder is not set";
            return null;
        }

        string sds = Path.Combine(MafiaEnvironment.PcFolder, "sds");
        string sourceStem = Path.GetFileNameWithoutExtension(source).ToLowerInvariant();
        string targetStem = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
        if (sourceStem == targetStem)
        {
            refusal = "the car and the one it replaces are the same";
            return null;
        }
        var tablesSds = new FileInfo(Path.Combine(sds, "tables", "tables.sds"));
        if (!tablesSds.Exists)
        {
            refusal = "the game's tables.sds is not where it belongs (pc\\sds\\tables)";
            return null;
        }
        GameTable vehicles = GameTable.Load(Path.Combine(SdsMeshLoader.EnsureExtracted(tablesSds), "tables", "vehicles.tbl"));
        int sourceRow = vehicles.FindRow(VehicleNameColumn, sourceStem);
        int targetRow = vehicles.FindRow(VehicleNameColumn, targetStem);
        if (sourceRow < 0 || targetRow < 0)
        {
            refusal = $"vehicles.tbl has no car named '{(sourceRow < 0 ? sourceStem : targetStem)}'";
            return null;
        }
        string sourceModel = (string)vehicles.Cell(sourceRow, VehicleNameColumn);
        string targetModel = (string)vehicles.Cell(targetRow, VehicleNameColumn);

        var notes = new List<string>();
        var cars = new List<(FileInfo From, FileInfo To)>();
        foreach (string suffix in new[] { "", "_z" })
        {
            var from = new FileInfo(Path.Combine(sds, "cars", sourceStem + suffix + ".sds"));
            var to = new FileInfo(Path.Combine(sds, "cars", targetStem + suffix + ".sds"));
            if (suffix.Length == 0 && (!from.Exists || !to.Exists))
            {
                refusal = $"there is no car archive {(from.Exists ? to.Name : from.Name)} in pc\\sds\\cars";
                return null;
            }
            if (!to.Exists)
            {
                if (from.Exists) notes.Add($"{targetModel} has no winter archive — the summer one stands in for it");
                continue;
            }
            if (!from.Exists)
            {
                notes.Add($"{sourceModel} has no winter archive — {to.Name} is left as it was");
                continue;
            }
            cars.Add((from, to));
        }

        // Both seasons or neither. The winter archive failing to pack used to leave the summer one substituted
        // and the winter one stock — and the target's working copy already deleted. The copy is moved aside
        // instead and only dropped once every archive is in; on a failure the replaced archives come back from
        // the backups taken a moment earlier.
        DateTime when = DateTime.Now;
        var packed = new List<(string Archive, string? Backup)>();
        var journal = new GameWriteJournal();
        try
        {
            foreach ((FileInfo from, FileInfo to) in cars)
            {
                string fromFolder = SdsMeshLoader.EnsureExtracted(from);
                SdsWriter.EnsureMemoryRequirements(from);
                string toFolder = MafiaEnvironment.ExtractedDir(to);
                journal.MoveAside(toFolder);
                journal.WillCreateFolder(toFolder);
                notes.AddRange(SubstituteExtracted(fromFolder, toFolder, sourceModel, targetModel));
                SdsWriter.PackResult made = SdsWriter.PackSds(to, createBackup: true, when);
                journal.Replaced(made.Archive, made.Backup);
                packed.Add((made.Archive, made.Backup));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            refusal = TakenBack("the substitution", ex, journal.Undo());
            return null;
        }
        journal.Commit();
        notes.Add(GameFileIndex.Reset()
            ? "the game's file list (vfs.bin) was reset — the next start rebuilds it"
            : $"the game's file list was not reset — remove {GameFileIndex.Path} before starting the game");
        refusal = null;
        return new CarSubstituteOutcome(targetModel, packed, notes);
    }

    /// <summary>
    /// The substitution itself, on working copies: copies the source car's folder to <paramref name="to"/>
    /// (which must not exist) and files everything in it under <paramref name="targetModel"/>. Nothing is packed.
    /// </summary>
    public static List<string> SubstituteExtracted(string from, string to, string sourceModel, string targetModel)
    {
        CopyTree(from, to);
        return RenameInside(to, sourceModel, targetModel);
    }
}
