namespace Illusion.Mcp;

public sealed record AnimationAuthoringInfo(string WorkingPath, int NativeClip, int Frames, int TransformTracks, int FloatTracks, bool Dirty)
{
    public string? Skeleton { get; init; }
    public byte BlendHint { get; init; }
    public IReadOnlyList<string> TransformTrackNames { get; init; } = [];
    public IReadOnlyList<string> FloatTrackNames { get; init; } = [];
    public float[]? LocalMatrix { get; init; }
    public float? Value { get; init; }
}
