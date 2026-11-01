namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? NativePaint(bool? enabled, uint? shuffleSeed, out PaintPreviewInfo status)
    {
        status = new(false, 1, 0, 0, 0, 0, 0, 0, 0, 0, null, new Dictionary<string, int>());
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Assets.MafiaEnvironment.Profile.ArchiveVersion != 20) return "Native paint preview requires Mafia III.";
        status = host.Streamer.ConfigureNativePaint(enabled, shuffleSeed); return null;
    }
}
