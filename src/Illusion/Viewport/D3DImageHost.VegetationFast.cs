using System.Numerics;
using Illusion.Assets;
using Illusion.Formats.Compiled;
using Illusion.Mcp;
using Illusion.Rendering.Passes;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    private sealed class NativeFastNoiseAdapter:INativeVegetationFieldNoise
    {
        private readonly CompiledVegetationNoiseState _state=new();
        public void Reset()=>_state.Reset();
        public void Step(float dt,Vector3 wind)=>_state.Step(dt,wind);
        public float[] PackMatrices()=>_state.PackMatrices();
    }
    internal string? SetNativeVegetationFast(bool? enabled,float[]? weatherWind,float[]? fieldOrigin,uint? noiseSeed,out VegetationFastInfo? result)
    {
        result=null;if(Rnd is not { } renderer)return "Wait for the viewport to initialize.";
        bool command=enabled is not null||weatherWind is not null||fieldOrigin is not null||noiseSeed is not null;
        if(command&&IsLoading)return "Wait for scene loading before changing the FAST field.";
        if(command&&renderer.NativeVegetationWindBridgeSuspended)return "Restore bridge rest-geometry authoring before changing the FAST field.";
        if(weatherWind is not null&&weatherWind.Length!=3||fieldOrigin is not null&&fieldOrigin.Length!=2)return "Provide weatherWind[3] and fieldOrigin[2].";
        if(enabled==true&&!renderer.NativeVegetationFastEnabled&&(weatherWind is null||fieldOrigin is null||noiseSeed is null))return "Enabling FAST requires explicit weatherWind, fieldOrigin and noiseSeed.";
        if(command)
        {
            if(enabled==false&&weatherWind is null&&fieldOrigin is null&&noiseSeed is null)renderer.DisableNativeVegetationFast();
            else
            {
                if(!renderer.NativeVegetationWindEnabled)return "Enable vegetation_wind_preview with its explicit native context first.";
                if(renderer.NativeVegetationFastTargetFrame<0)return "FAST field replay requires a clock within0..600seconds.";
                try
                {
                    var parameters=ReadNativeFastParameters();
                    renderer.ConfigureNativeVegetationFast(enabled??renderer.NativeVegetationFastEnabled,
                        weatherWind is null?renderer.NativeVegetationFastWeatherWind:new(weatherWind[0],weatherWind[1],weatherWind[2]),
                        fieldOrigin is null?renderer.NativeVegetationFastOrigin:new(fieldOrigin[0],fieldOrigin[1]),noiseSeed??renderer.NativeVegetationFastSeed,
                        parameters,static()=>new NativeFastNoiseAdapter());
                }
                catch(Exception error)when(error is ArgumentException or InvalidOperationException){return error.Message;}
            }
        }
        var w=renderer.NativeVegetationFastWeatherWind;var o=renderer.NativeVegetationFastOrigin;
        result=new(renderer.NativeVegetationFastEnabled,renderer.NativeVegetationFastReady,renderer.NativeVegetationFastFrames,renderer.NativeVegetationFastTargetFrame,
            renderer.NativeVegetationFastDrawnParts,[w.X,w.Y,w.Z],[o.X,o.Y],renderer.NativeVegetationFastSeed,renderer.NativeVegetationFastStatus,
            "Opt-in native FAST KD/SO identity-fallback origins, original field/vertex equations and native clear state. Explicit fixed128-world-unit origin, seeded CRT noise, separate steady weather wind,60Hz bounded replay; zero actor colliders. Toolkit lighting/rest picking; no live-game camera/history/weather parity. LARGE/GENERAL remains independently controlled.");
        return null;
    }
    private static NativeVegetationFieldParameters ReadNativeFastParameters()
    {
        MafiaMaterials.EnsureLoaded();var info=MafiaMaterials.GetMaterialInfo(0x55D306DD06000A9C,0,0);
        if(info.ShaderId!=0x34243065074E26F1)throw new InvalidOperationException("Native pp_wind_simulation material/template is missing or changed.");
        var a=info.Parameters.FirstOrDefault(p=>p.ParamId=="D135")?.Values;var b=info.Parameters.FirstOrDefault(p=>p.ParamId=="D215")?.Values;
        if(a is not {Count:>=2}||b is not {Count:>=3}||!a.Take(2).Concat(b.Take(3)).All(float.IsFinite)||b[2]<=0)
            throw new InvalidOperationException("Native pp_wind_simulation material D135/D215 inputs are missing or invalid.");
        return new(new(a[0],a[1]),new(b[0],b[1],b[2]));
    }
    internal void ReloadNativeVegetationFastInputs()
    {
        if(Rnd is not {NativeVegetationFastEnabled:true} renderer)return;
        try{renderer.ConfigureNativeVegetationFast(true,renderer.NativeVegetationFastWeatherWind,renderer.NativeVegetationFastOrigin,renderer.NativeVegetationFastSeed,ReadNativeFastParameters(),static()=>new NativeFastNoiseAdapter());}
        catch(Exception error)when(error is InvalidOperationException or ArgumentException){renderer.InvalidateNativeVegetationFastInputs(error.Message);RaiseNotice(error.Message,isError:true);}
    }
}
