using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private sealed class FastProbeNoise:INativeVegetationFieldNoise
    {
        private readonly CompiledVegetationNoiseState _state=new();
        public void Reset()=>_state.Reset();public void Step(float dt,Vector3 wind)=>_state.Step(dt,wind);public float[] PackMatrices()=>_state.PackMatrices();
    }
    internal static void TestNativeVegetationFastScene(Action<string,bool> check)
    {
        var archive=new FileInfo(Path.Combine(MafiaEnvironment.CityFolder,"bc_bayou_general.sds"));if(!archive.Exists)return;
        var document=new CompiledSceneDocument(archive);
        var source=document.Meshes.First(m=>m.Parts.Any(p=>p.Surface?.NativeVegetationFast is not null)&&m.NativeWindObjectOrigin&&m.Colors is not null);
        var mesh=new MeshData{Name=source.Name,World=Matrix4x4.Identity,Positions=source.Positions,Normals=source.Normals,UVs=source.UVs,Tangents=source.Tangents,Binormals=source.Binormals,
            Colors=source.Colors,SecondaryColors=source.SecondaryColors,NativeWindGeometryBind=source.NativeWindGeometryBind,NativeWindObjectOrigin=source.NativeWindObjectOrigin,
            Indices=source.Indices,Parts=source.Parts,Instances=[source.Instances![0]]};
        using var gpu=new GpuContext();using var renderer=new SceneRenderer(gpu){ShowSky=false,Mode=RenderMode.MaterialPreview};using var target=new SharedRenderTarget(gpu,256,256);
        var uploaded=renderer.AddMesh(mesh);var origin=mesh.Instances![0].Translation;
        renderer.Camera.LookAt(origin+new Vector3(5,-7,4),origin+new Vector3(0,0,1.5f));
        byte[] Render(){renderer.Render(target);return RenderTargetReadback.Read(gpu,target);}
        void Clock(float time)=>renderer.ConfigureNativeVegetationWind(true,time,new(1,0,0,15),new(2000,2000,0),new(.001f,0));
        var info=MafiaMaterials.GetMaterialInfo(0x55D306DD06000A9C,0,0);var a=info.Parameters.Single(p=>p.ParamId=="D135").Values;var b=info.Parameters.Single(p=>p.ParamId=="D215").Values;
        var parameters=new NativeVegetationFieldParameters(new(a[0],a[1]),new(b[0],b[1],b[2]));var weather=new Vector3(15,0,0);var fieldOrigin=new Vector2(origin.X,origin.Y);
        var baseline=Render();int baselineDraws=renderer.DrawCalls;Clock(0);
        renderer.ConfigureNativeVegetationFast(true,weather,fieldOrigin,17,parameters,static()=>new FastProbeNoise());var first=Render();
        check("actual FAST copied mesh retains normalized COLOR0/native KD identity fallback",renderer.NativeVegetationFastReady&&renderer.NativeVegetationFastDrawnParts>0&&renderer.NativeVegetationWindEligibleMeshes==1);
        Clock(.5f);_ =Render();check("actual FAST field advances at most four native frames per draw",renderer.NativeVegetationFastFrames<=4&&!renderer.NativeVegetationFastReady);
        for(int draw=0;draw<16&&!renderer.NativeVegetationFastReady;draw++)_ =Render();var later=Render();
        check("actual FAST native field reaches explicit target frame",renderer.NativeVegetationFastReady&&renderer.NativeVegetationFastFrames==30&&renderer.NativeVegetationFastDrawnParts>0);
        check("actual FAST copied geometry responds to native field clock",!first.SequenceEqual(later));
        Clock(0);check("actual FAST rewind restores exact native boot-state pixels",first.SequenceEqual(Render())&&renderer.NativeVegetationFastFrames==0);
        renderer.SetGhostFocus([uploaded]);var bridge=Render();
        check("actual FAST bridge editing suspends deformation and retains authored rest pixels",renderer.NativeVegetationWindBridgeSuspended&&!renderer.NativeVegetationFastReady&&baseline.SequenceEqual(bridge));renderer.SetGhostFocus(null);
        bool rejected=false;try{renderer.ConfigureNativeVegetationFast(true,new(float.NaN),fieldOrigin,18,parameters,static()=>new FastProbeNoise());}catch(ArgumentException){rejected=true;}
        check("actual FAST invalid context refuses atomically",rejected&&renderer.NativeVegetationFastSeed==17&&first.SequenceEqual(Render()));
        renderer.InvalidateNativeVegetationFastInputs("Test invalid material parameter");var invalid=Render();
        check("actual FAST invalid material inputs stop stale simulation without losing enabled intent",renderer.NativeVegetationFastEnabled&&!renderer.NativeVegetationFastReady&&renderer.NativeVegetationFastDrawnParts==0&&baseline.SequenceEqual(invalid));
        renderer.ConfigureNativeVegetationFast(true,weather,fieldOrigin,17,parameters,static()=>new FastProbeNoise());
        check("actual FAST repaired material restores exact replay pixels",first.SequenceEqual(Render())&&renderer.NativeVegetationFastReady);
        Clock(600);int beforeCapture=renderer.NativeVegetationFastFrames;string? captureRefusal=renderer.RenderForCapture(target);
        check("actual FAST capture refuses pending fallback within bounded work",captureRefusal is not null&&!renderer.NativeVegetationFastReady
            &&renderer.NativeVegetationFastFrames-beforeCapture<=32);
        Clock(0);check("actual FAST bounded capture rewind restores exact pixels",renderer.RenderForCapture(target) is null&&first.SequenceEqual(RenderTargetReadback.Read(gpu,target)));
        renderer.DisableNativeVegetationFast();check("actual FAST disabled mode restores exact pixels and draw counts",baseline.SequenceEqual(Render())&&renderer.DrawCalls==baselineDraws);
        check("actual FAST field/frame preview preserves every compiled working file",document.Graph.Resources.All(resource=>!resource.IsDirty&&resource.ToArray().SequenceEqual(File.ReadAllBytes(resource.Path))));
    }
}
