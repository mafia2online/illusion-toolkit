using System.IO;
using Illusion.Assets;
using Illusion.Views;

namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? AnimationOpen(string archive, int clip, double time, bool playing, out AnimationInfo? result)
    {
        result = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (MafiaEnvironment.Profile.ArchiveVersion != 20) return "Native Havok animation preview requires Mafia III.";
        if (!double.IsFinite(time) || time < 0 || time > float.MaxValue) return "Animation time must be a finite nonnegative number.";
        string path = Path.IsPathRooted(archive) ? archive : Path.Combine(MafiaEnvironment.ArchiveFolder,
            archive.EndsWith(".sds", StringComparison.OrdinalIgnoreCase) ? archive : archive + ".sds");
        if (!File.Exists(path)) return "Animation archive does not exist: " + path;
        try
        {
            if (ResourceWindow is null) new ResourceEditorWindow { ShowActivated = false, WindowState = System.Windows.WindowState.Normal }.Show();
            var window = ResourceWindow?.OpenAnimationPreview(new FileInfo(path), activate: false);
            if (window is null) return "The resource editor could not open.";
            if ((uint)clip >= window.ClipNames.Count) return $"Clip index must be between 0 and {window.ClipNames.Count - 1}.";
            window.SelectClip(clip); window.Seek((float)time); window.SetPlaying(playing);
            result = new AnimationInfo(path, window.ClipNames, window.SelectedClip, window.CurrentTime, window.Duration, window.IsPlaying);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        { return "Native animation preview: " + ex.Message; }
    }
}
