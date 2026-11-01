using System.Numerics;
using System.Runtime.CompilerServices;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

/// <summary>Format/backend adapter for the independently verified native four-layer noise state.</summary>
public interface INativeVegetationFieldNoise
{
    void Reset();
    void Step(float dt,Vector3 weatherWind);
    float[] PackMatrices();
}
/// <summary>Actual native field material D135/D215.</summary>
public readonly record struct NativeVegetationFieldParameters(Vector2 RadiusPush,Vector3 Dynamics);

public sealed unsafe partial class SceneRenderer
{
    public bool NativeVegetationFastEnabled { get; private set; }
    public Vector3 NativeVegetationFastWeatherWind { get; private set; }
    public Vector2 NativeVegetationFastOrigin { get; private set; }
    public uint NativeVegetationFastSeed { get; private set; }
    public NativeVegetationFieldParameters NativeVegetationFastParameters => _fastParameters;
    public int NativeVegetationFastFrames { get; private set; }
    public int NativeVegetationFastTargetFrame => float.IsFinite(NativeVegetationWindTime)&&NativeVegetationWindTime>=0&&NativeVegetationWindTime<=600?(int)MathF.Round(NativeVegetationWindTime*60):-1;
    public bool NativeVegetationFastReady => NativeVegetationFastEnabled&&NativeVegetationWindEnabled&&!NativeVegetationWindBridgeSuspended&&!_fastInputsInvalid&&!_fastResetPending&&_fastField is not null&&NativeVegetationFastFrames==NativeVegetationFastTargetFrame;
    public int NativeVegetationFastDrawnParts { get; private set; }
    public string NativeVegetationFastStatus { get; private set; }="Disabled";
    private NativeVegetationFieldParameters _fastParameters;
    private NativeVegetationWindField? _fastField;
    private INativeVegetationFieldNoise? _fastNoiseState;
    private Func<INativeVegetationFieldNoise>? _fastNoiseFactory;
    private ComPtr<ID3D11ShaderResourceView> _fastNoise;
    private uint? _fastUploadedSeed;
    private bool _fastResetPending;
    private bool _fastInputsInvalid;
    private Vector4 _fastLastGlobal;
    private readonly float[] _fastGlobals=new float[540],_fastMaterial=new float[8];
    private ConditionalWeakTable<GpuMesh,FastBoundsCache> _fastBoundsCache=new();
    private sealed record FastBoundsCache(Matrix4x4[] Matrices,NativeVegetationFastMaterial?[] Materials,NativeVegetationWindPrepared? Prepared,string Reason);
    /// <summary>Stops field deformation without requiring material/noise inputs to remain available.</summary>
    public void DisableNativeVegetationFast() { NativeVegetationFastEnabled=false;NativeVegetationFastStatus="Disabled"; }
    /// <summary>Keeps the user's opt-in intent while refusing stale/invalid material inputs; a valid reload restarts replay.</summary>
    public void InvalidateNativeVegetationFastInputs(string reason) { _fastInputsInvalid=true;NativeVegetationFastStatus=reason; }

    /// <summary>Explicit fixed-origin, seeded 60Hz native field preview. The original global CRT seed and live camera history are not inferred.
    /// Existing LARGE/GENERAL configuration is independent; forward catch-up submits at most four steps per draw.</summary>
    public void ConfigureNativeVegetationFast(bool enabled,Vector3 weatherWind,Vector2 origin,uint seed,
        NativeVegetationFieldParameters parameters,Func<INativeVegetationFieldNoise> noiseFactory)
    {
        if(!float.IsFinite(weatherWind.LengthSquared())||!float.IsFinite(origin.LengthSquared())||!float.IsFinite(parameters.RadiusPush.LengthSquared())
            ||!float.IsFinite(parameters.Dynamics.LengthSquared())||parameters.Dynamics.Z<=0)throw new ArgumentException("FAST field requires finite explicit context and positive native damping.");
        ArgumentNullException.ThrowIfNull(noiseFactory);
        bool changed=NativeVegetationFastWeatherWind!=weatherWind||NativeVegetationFastOrigin!=origin||NativeVegetationFastSeed!=seed||_fastParameters!=parameters;
        NativeVegetationFastEnabled=enabled;NativeVegetationFastWeatherWind=weatherWind;NativeVegetationFastOrigin=origin;NativeVegetationFastSeed=seed;
        _fastParameters=parameters;_fastNoiseFactory=noiseFactory;
        if(changed||_fastNoiseState is null||_fastInputsInvalid){_fastNoiseState=noiseFactory();_fastResetPending=true;}
        _fastInputsInvalid=false;
        NativeVegetationFastStatus=enabled?"Waiting for bounded frame replay":"Disabled";
    }
    private void AdvanceNativeVegetationFast()
    {
        NativeVegetationFastDrawnParts=0;
        if(!NativeVegetationFastEnabled)return;
        if(_fastInputsInvalid)return;
        if(!NativeVegetationWindEnabled){NativeVegetationFastStatus="Enable the explicit native wind context first";return;}
        if(NativeVegetationWindBridgeSuspended){NativeVegetationFastStatus="Suspended during bridge rest-geometry authoring";return;}
        int target=NativeVegetationFastTargetFrame;if(target<0){NativeVegetationFastStatus="FAST replay clock must be within0..600seconds";return;}
        if(!_meshes.Any(mesh=>mesh.Parts.Any(part=>part.Surface?.NativeVegetationFast is not null))){NativeVegetationFastStatus="No FAST material parts in this scene";return;}
        _fastField??=new NativeVegetationWindField(_gpu);
        if(_fastNoise.Handle==null||_fastUploadedSeed!=NativeVegetationFastSeed)
        {var replacement=CreateFastNoise(NativeVegetationFastSeed);_fastNoise.Dispose();_fastNoise=replacement;_fastUploadedSeed=NativeVegetationFastSeed;_fastResetPending=true;}
        if(_fastLastGlobal!=NativeVegetationWindGlobal){_fastLastGlobal=NativeVegetationWindGlobal;_fastResetPending=true;}
        if(_fastResetPending||NativeVegetationFastFrames>target)
        {
            _fastNoiseState??=_fastNoiseFactory!();_fastNoiseState.Reset();_fastField.Reset(new(.5f,.5f,0,0),new(.5f,.5f,0,0));NativeVegetationFastFrames=0;_fastResetPending=false;
        }
        _fastMaterial[0]=_fastParameters.RadiusPush.X;_fastMaterial[1]=_fastParameters.RadiusPush.Y;
        _fastMaterial[4]=_fastParameters.Dynamics.X;_fastMaterial[5]=_fastParameters.Dynamics.Y;_fastMaterial[6]=_fastParameters.Dynamics.Z;
        Array.Clear(_fastGlobals);_fastGlobals[52]=NativeVegetationWindGlobal.X;_fastGlobals[53]=NativeVegetationWindGlobal.Y;_fastGlobals[54]=NativeVegetationWindGlobal.Z;_fastGlobals[55]=NativeVegetationWindGlobal.W;
        _fastGlobals[136]=NativeVegetationFastOrigin.X;_fastGlobals[137]=NativeVegetationFastOrigin.Y;
        var snap = NativeFastSnapOffset(NativeVegetationFastOrigin); _fastGlobals[132]=snap.X;_fastGlobals[133]=snap.Y;
        for(int i=0;i<4;i++)_fastGlobals[228+i]=1;
        for(int i=0;i<8;i++)_fastGlobals[(83+i)*4+3]=.0001f; // Original unused-sphere defaults, not zero-radius fabricated colliders.
        for(int step=0;step<4&&NativeVegetationFastFrames<target;step++)
        {
            _fastNoiseState!.Step(1/60f,NativeVegetationFastWeatherWind);var matrices=_fastNoiseState.PackMatrices();
            if(matrices.Length!=48||matrices.Any(v=>!float.IsFinite(v)))throw new InvalidOperationException("Native field noise adapter returned invalid matrices.");
            matrices.CopyTo(_fastGlobals,492);_fastGlobals[212]=NativeVegetationFastFrames/60f;_fastGlobals[216]=(NativeVegetationFastFrames+1)/60f;
            _fastField.Step(_fastMaterial,_fastGlobals,_fastNoise);NativeVegetationFastFrames++;
        }
        NativeVegetationFastStatus=NativeVegetationFastFrames==target?"Ready: native field equations, explicit fixed origin/seed/weather, zero actor colliders":"Pending bounded replay";
    }
    private ComPtr<ID3D11ShaderResourceView> CreateFastNoise(uint seed)
    {
        var bytes=new byte[32768];uint state=seed;for(int i=0;i<bytes.Length;i++){state=unchecked(state*214013+2531011);bytes[i]=(byte)(state>>16);}
        var desc=new Texture3DDesc{Width=32,Height=32,Depth=32,MipLevels=1,Format=Format.FormatR8Unorm,Usage=Usage.Immutable,BindFlags=(uint)BindFlag.ShaderResource};ID3D11Texture3D*texture=null;
        fixed(byte*p=bytes){var data=new SubresourceData{PSysMem=p,SysMemPitch=32,SysMemSlicePitch=1024};SilkMarshal.ThrowHResult(_gpu.Device11.CreateTexture3D(in desc,in data,&texture));}
        try{ID3D11ShaderResourceView*view=null;SilkMarshal.ThrowHResult(_gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture,(ShaderResourceViewDesc*)null,&view));return view;}finally{texture->Release();}
    }
    private NativeVegetationWindPrepared? PrepareNativeVegetationFast(GpuMesh mesh)
    {
        var materials=mesh.Parts.Select(p=>p.Surface?.NativeVegetationFast).ToArray();if(materials.All(m=>m is null))return null;
        if(!NativeVegetationFastReady){FastRefuse(NativeVegetationFastEnabled?NativeVegetationFastStatus:"Native FAST field is disabled");return null;}
        if(_fastBoundsCache.TryGetValue(mesh,out var cached)&&ReferenceEquals(cached.Matrices,mesh.WindInstances)&&cached.Materials.SequenceEqual(materials))
        {if(cached.Prepared is null)FastRefuse(cached.Reason);return cached.Prepared;}
        var result=FastBounds(mesh,materials,out string reason);_fastBoundsCache.Remove(mesh);_fastBoundsCache.Add(mesh,new(mesh.WindInstances,materials,result,reason));
        if(result is null)FastRefuse(reason);return result;
    }
    private void FastRefuse(string reason)=>_vegetationWindRefusals[reason]=_vegetationWindRefusals.GetValueOrDefault(reason)+1;
    private static Vector2 NativeFastSnapOffset(Vector2 origin)
    { var scaled=origin*4;return new((MathF.Floor(scaled.X)-scaled.X)*.25f,(MathF.Floor(scaled.Y)-scaled.Y)*.25f); }
    private static NativeVegetationWindPrepared? FastBounds(GpuMesh mesh,NativeVegetationFastMaterial?[] materials,out string reason)
    {
        reason="";
        if(mesh.WindGeometry is not {PrimaryColorsComplete:true,NativeObjectOrigin:true} geometry||geometry.Bind!=Matrix4x4.Identity)
        {reason="FAST requires complete COLOR0 and verified identity-bound KD/SO origin; baked/rest/nonidentity meshes remain static";return null;}
        float radius=materials.OfType<NativeVegetationFastMaterial>().Max(m=>Math.Max(Math.Abs(m.Displacement.X),Math.Abs(m.Displacement.Y)));
        if(!float.IsFinite(radius)||radius>1e6f){reason="FAST displacement is outside finite conservative bounds";return null;}
        var bounds=new NativeLodBounds[mesh.WindInstances.Length];var expansion=new Vector3(MathF.BitIncrement(radius),MathF.BitIncrement(radius),0);
        var fullMin=new Vector3(float.PositiveInfinity);var fullMax=new Vector3(float.NegativeInfinity);
        for(int i=0;i<bounds.Length;i++)
        {
            var matrix=mesh.WindInstances[i];var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
            for(int c=0;c<8;c++){var p=Vector3.Transform(new((c&1)==0?mesh.LocalMin.X:mesh.LocalMax.X,(c&2)==0?mesh.LocalMin.Y:mesh.LocalMax.Y,(c&4)==0?mesh.LocalMin.Z:mesh.LocalMax.Z),matrix);min=Vector3.Min(min,p);max=Vector3.Max(max,p);}
            if(!float.IsFinite(min.LengthSquared())||!float.IsFinite(max.LengthSquared())||matrix.M14!=0||matrix.M24!=0||matrix.M34!=0||matrix.M44!=1){reason="FAST instance bounds are not finite affine";return null;}
            var local=Vector3.Max(Vector3.Abs(mesh.LocalMin),Vector3.Abs(mesh.LocalMax));
            const double epsilon=16*1.1920928955078125e-7;
            double rx=epsilon*((double)local.X*Math.Abs(matrix.M11)+(double)local.Y*Math.Abs(matrix.M21)+(double)local.Z*Math.Abs(matrix.M31)+Math.Abs(matrix.M41)+radius);
            double ry=epsilon*((double)local.X*Math.Abs(matrix.M12)+(double)local.Y*Math.Abs(matrix.M22)+(double)local.Z*Math.Abs(matrix.M32)+Math.Abs(matrix.M42)+radius);
            double rz=epsilon*((double)local.X*Math.Abs(matrix.M13)+(double)local.Y*Math.Abs(matrix.M23)+(double)local.Z*Math.Abs(matrix.M33)+Math.Abs(matrix.M43));
            if(!double.IsFinite(rx+ry+rz)||rx+ry+rz>float.MaxValue/16){reason="FAST transform rounding bound is nonfinite";return null;}
            var rounding=new Vector3(MathF.BitIncrement((float)rx),MathF.BitIncrement((float)ry),MathF.BitIncrement((float)rz));
            bounds[i]=new(min-expansion-rounding,max+expansion+rounding);fullMin=Vector3.Min(fullMin,bounds[i].Min);fullMax=Vector3.Max(fullMax,bounds[i].Max);
        }
        var cells=mesh.InstanceCells??[];var cellBounds=new NativeLodBounds[cells.Length];
        for(int c=0;c<cells.Length;c++){var min=cells[c].Min;var max=cells[c].Max;for(uint i=cells[c].Start;i<cells[c].Start+cells[c].Count;i++){min=Vector3.Min(min,bounds[i].Min);max=Vector3.Max(max,bounds[i].Max);}cellBounds[c]=new(min,max);}
        return new(bounds,cellBounds,fullMin,fullMax){LargeEnabled=false,FastEnabled=true};
    }
    private static NativeVegetationWindPrepared MergeFastBounds(NativeVegetationWindPrepared large,NativeVegetationWindPrepared fast)
        => new(large.Bounds.Zip(fast.Bounds,(a,b)=>new NativeLodBounds(Vector3.Min(a.Min,b.Min),Vector3.Max(a.Max,b.Max))).ToArray(),
            large.Cells.Zip(fast.Cells,(a,b)=>new NativeLodBounds(Vector3.Min(a.Min,b.Min),Vector3.Max(a.Max,b.Max))).ToArray(),Vector3.Min(large.Min,fast.Min),Vector3.Max(large.Max,fast.Max)){FastEnabled=true};
    private void ResetNativeVegetationFast(){_fastResetPending=true;_fastBoundsCache=new();NativeVegetationFastDrawnParts=0;NativeVegetationFastStatus=NativeVegetationFastEnabled?"Waiting for replay after scene reset":"Disabled";}
    private void DisposeNativeVegetationFast(){ID3D11ShaderResourceView* empty=null;_gpu.Context11.VSSetShaderResources(12,1,&empty);_fastField?.Dispose();_fastNoise.Dispose();}
}
