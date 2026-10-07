namespace Illusion.Assets.Sds;

/// <summary>
/// The game's cached list of its own files: <c>%LOCALAPPDATA%\2K Games\Mafia II\Data\vfs.bin</c>.
///
/// <para>
/// The game finds an archive through this list, not through the folder. It refreshes the list only in part
/// when it starts, and a NEW archive dropped into <c>pc\sds</c> is not picked up: a cloned car was in the
/// vehicle table and in the garage, and still had no model, until the cache was removed and rebuilt in full.
/// Steam's own install script removes it for the same reason (<c>RemoveSaves.exe /vfsonly</c>). An archive
/// that only changed — the same name as before — needs none of this.
/// </para>
/// </summary>
public static class GameFileIndex
{
    /// <summary>Where the cache lives for the current user.</summary>
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "2K Games", "Mafia II", "Data", "vfs.bin");

    /// <summary>Removes the cache so the next start of the game builds it from the files that are there.</summary>
    /// <returns>True when a cache was removed; false when there was none, or it is in use (the game is running).</returns>
    public static bool Reset()
    {
        try
        {
            if (!File.Exists(Path)) return false;
            File.Delete(Path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
