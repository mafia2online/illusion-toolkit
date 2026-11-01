namespace Illusion.Mcp;

public sealed record WaterSurfaceInfo(bool Available, bool Enabled, bool Active, bool Playing,
    float Time, bool Normals, int DefinitionPartsDrawn, string? InputError);
