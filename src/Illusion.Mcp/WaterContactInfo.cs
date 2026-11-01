namespace Illusion.Mcp;

public sealed record WaterContactInfo(bool Available, bool Enabled, bool Ready, bool Configured,
    int Frame, int TargetFrame, float[] Origin, bool ZeroHeightCapture, bool ZeroWind, string Scope, string? Error, bool JointFoam = false, int FoamFrame = 0, int FoamTargetFrame = 0);
