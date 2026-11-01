namespace Illusion.Mcp;

/// <summary>Explicit fixed-origin FAST field replay state; never a live-game weather/history claim.</summary>
public sealed record VegetationFastInfo(bool Enabled,bool Ready,int Frames,int TargetFrame,int DrawnParts,
    float[] WeatherWind,float[] FieldOrigin,uint NoiseSeed,string Status,string Scope);
