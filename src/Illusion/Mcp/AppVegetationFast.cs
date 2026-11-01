namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? VegetationFast(bool? enabled,float[]? weatherWind,float[]? fieldOrigin,uint? noiseSeed,out VegetationFastInfo? result)
    {
        result=null;if(TargetHost is not { } host)return TargetNotOpen;
        return host.SetNativeVegetationFast(enabled,weatherWind,fieldOrigin,noiseSeed,out result);
    }
}
