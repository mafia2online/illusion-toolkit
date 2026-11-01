namespace Illusion.Mcp;

public sealed record WaterSurfaceInfo(bool Available, bool Enabled, bool Active, bool Playing,
    float Time, bool Normals, int DefinitionPartsDrawn, string? InputError, bool FineNormalsConfigured = false,
    int FlowPartsDrawn = 0, bool FoamConfigured = false, bool FoamEnabled = false, bool FoamReady = false,
    int FoamFrame = 0, int FoamTargetFrame = 0, uint FoamSeed = 1, string FoamMode = "diffuse", string? FoamError = null);
