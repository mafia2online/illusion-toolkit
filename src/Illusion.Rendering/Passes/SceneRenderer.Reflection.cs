namespace Illusion.Rendering.Passes;

public sealed partial class SceneRenderer
{
    public bool NativeWaterTemporalReflections
    {
        get => _nativeWater?.TemporalReflectionsEnabled == true;
        set
        {
            if (_nativeWater == null) return;
            if (_nativeWater.TemporalReflectionsEnabled != value) _nativeWater.ResetTemporalReflections();
            _nativeWater.TemporalReflectionsEnabled = value;
        }
    }
    public bool NativeWaterTemporalReflectionsActive => NativeWaterActive && _nativeWater?.TemporalReflectionsActive == true;
    public int NativeWaterTemporalReflectionFrames => _nativeWater?.TemporalReflectionFrames ?? 0;
    public static string NativeWaterTemporalReflectionScope => NativeWaterSurface.TemporalReflectionScope;
    public void ResetNativeWaterTemporalReflections() => _nativeWater?.ResetTemporalReflections();
}
