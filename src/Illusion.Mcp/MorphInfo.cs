namespace Illusion.Mcp;

public sealed record MorphInfo(string? Texture, int Lod, int Vertices, int ActiveVertices, int Influences,
    IReadOnlyList<string> TargetIds);
