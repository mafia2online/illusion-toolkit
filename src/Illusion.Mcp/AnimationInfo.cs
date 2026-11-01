namespace Illusion.Mcp;

public sealed record AnimationInfo(string Archive, IReadOnlyList<string> Clips, int SelectedClip,
    double Time, double Duration, bool Playing)
{
    public IReadOnlyList<AnimationSkeletonChoice> SkeletonCandidates { get; init; } = [];
    public int BoundRigs { get; init; }
}
public sealed record AnimationSkeletonChoice(int Clip, int Candidate, bool RequiresSelection);

public sealed record AnimationFloatSlot(int Index, string Name, float Value);
