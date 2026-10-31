namespace Illusion.Mcp;

public sealed record AnimationInfo(string Archive, IReadOnlyList<string> Clips, int SelectedClip,
    double Time, double Duration, bool Playing);
