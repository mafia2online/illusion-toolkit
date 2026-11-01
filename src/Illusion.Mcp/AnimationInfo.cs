namespace Illusion.Mcp;

public sealed record AnimationInfo(string Archive, IReadOnlyList<string> Clips, int SelectedClip,
    double Time, double Duration, bool Playing);

public sealed record AnimationFloatSlot(int Index, string Name, float Value);
