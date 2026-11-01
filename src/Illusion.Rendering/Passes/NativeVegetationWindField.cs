using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

/// <summary>Source-native PS4727 one-frame simulation. Reset contents are explicitly caller-defined analysis inputs,
/// not a claim about the game's first-frame allocation contents. No viewport activation is implied.</summary>
public sealed unsafe class NativeVegetationWindField : IDisposable
{
    public const int Size = 512;
    private readonly GpuContext _gpu;
    private ComPtr<ID3D11VertexShader> _vertex;
    private ComPtr<ID3D11PixelShader> _pixel;
    private ComPtr<ID3D11Buffer> _material, _global;
    private ComPtr<ID3D11SamplerState> _sampler;
    private ComPtr<ID3D11RasterizerState> _raster;
    private readonly Field?[] _velocity = new Field?[2], _offset = new Field?[2];
    private Field? _force;
    private int _read;
    private bool _disposed, _reset;
    /// <summary>Number of completed single-frame submissions since explicit Reset.</summary>
    public long Frames { get; private set; }
    /// <summary>Borrowed current velocity texture; survives until the next Step/Reset/Dispose.</summary>
    public ComPtr<ID3D11ShaderResourceView> Velocity => _disposed || !_reset ? default : _velocity[_read]!.Resource;
    /// <summary>Borrowed current offset texture, including native interaction channels Z/W.</summary>
    public ComPtr<ID3D11ShaderResourceView> Offset => _disposed || !_reset ? default : _offset[_read]!.Resource;
    /// <summary>Borrowed previous offset required by FAST vertex sampling.</summary>
    public ComPtr<ID3D11ShaderResourceView> PreviousOffset => _disposed || !_reset ? default : _offset[1-_read]!.Resource;
    /// <summary>Third shader output retained as float diagnostics; native third-target allocation is not asserted.</summary>
    public ComPtr<ID3D11ShaderResourceView> DiagnosticForce => _disposed || !_reset ? default : _force!.Resource;

    public NativeVegetationWindField(GpuContext gpu)
    {
        _gpu=gpu;
        try
        {
            using var compiler=D3DCompiler.GetApi();
            using var vs=ShaderCompiler.Compile(compiler,"struct O{float4 p:SV_Position;float4 uv:TEXCOORD0;};O main(uint id:SV_VertexID){O o;float2 uv=float2((id<<1)&2,id&2);o.p=float4(uv*float2(2,-2)+float2(-1,1),0,1);o.uv=float4(uv,0,0);return o;}","main","vs_5_0","vegetation-field");
            ID3D11VertexShader* vertex=null;SilkMarshal.ThrowHResult(gpu.Device11.CreateVertexShader(vs.GetBufferPointer(),vs.GetBufferSize(),(ID3D11ClassLinkage*)null,&vertex));_vertex=vertex;
            using var ps=ShaderCompiler.Compile(compiler,NativeVegetationFieldShader.Source,"main","ps_5_0","vegetation-field");
            ID3D11PixelShader* pixel=null;SilkMarshal.ThrowHResult(gpu.Device11.CreatePixelShader(ps.GetBufferPointer(),ps.GetBufferSize(),(ID3D11ClassLinkage*)null,&pixel));_pixel=pixel;
            _material=Buffer(8);_global=Buffer(540);
            var samplerDesc=new SamplerDesc{Filter=Filter.MinMagLinearMipPoint,AddressU=TextureAddressMode.Wrap,AddressV=TextureAddressMode.Wrap,AddressW=TextureAddressMode.Wrap,MaxLOD=float.MaxValue};
            ID3D11SamplerState* sampler=null;SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in samplerDesc,&sampler));_sampler=sampler;
            var rasterDesc=new RasterizerDesc{FillMode=FillMode.Solid,CullMode=CullMode.None,DepthClipEnable=true};ID3D11RasterizerState*raster=null;SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rasterDesc,&raster));_raster=raster;
            for(int i=0;i<2;i++){_velocity[i]=new(gpu,Format.FormatR8G8Unorm);_offset[i]=new(gpu,Format.FormatR8G8B8A8Unorm);}
            _force=new(gpu,Format.FormatR32G32B32A32Float);
        }
        catch{Dispose();throw;}
    }
    /// <summary>Clears both histories to finite encoded [0,1] values chosen by the caller. No native boot-state default is supplied.</summary>
    public void Reset(Vector4 encodedVelocity,Vector4 encodedOffset)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(!ValidColor(encodedVelocity)||!ValidColor(encodedOffset))throw new ArgumentException("Explicit encoded history must be finite and within [0,1].");
        Unbind();_gpu.Context11.OMSetRenderTargets(0,(ID3D11RenderTargetView**)null,(ID3D11DepthStencilView*)null);
        float* v=stackalloc float[4]{encodedVelocity.X,encodedVelocity.Y,encodedVelocity.Z,encodedVelocity.W};float* o=stackalloc float[4]{encodedOffset.X,encodedOffset.Y,encodedOffset.Z,encodedOffset.W};float* z=stackalloc float[4]{0,0,0,0};
        foreach(var field in _velocity)_gpu.Context11.ClearRenderTargetView(field!.View.Handle,v);
        foreach(var field in _offset)_gpu.Context11.ClearRenderTargetView(field!.View.Handle,o);
        _gpu.Context11.ClearRenderTargetView(_force!.View.Handle,z);_read=0;Frames=0;_reset=true;
    }
    /// <summary>Submits exactly one shader frame. All constants and the borrowed R8 32³ native noise resource
    /// must be provided explicitly. Leaves OM/SRVs unbound; callers restore their render state.</summary>
    public void Step(ReadOnlySpan<float> material,ReadOnlySpan<float> globals,ComPtr<ID3D11ShaderResourceView> noise)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(!_reset)throw new InvalidOperationException("Explicit history Reset is required before Step.");
        if(material.Length!=8||globals.Length!=540||!Finite(material)||!Finite(globals)||material[6]<=0)throw new ArgumentException("Finite native 2/135 float4 constants and positive damping are required.");
        ShaderResourceViewDesc description; if(noise.Handle==null)throw new ArgumentException("Native noise texture is required.");noise.GetDesc(&description);
        if(description.ViewDimension!=D3DSrvDimension.D3DSrvDimensionTexture3D||description.Format!=Format.FormatR8Unorm)throw new ArgumentException("Native noise must be an R8 3D resource.");
        ID3D11Resource* noiseResource=null;noise.Handle->GetResource(&noiseResource);
        if(noiseResource==null)throw new ArgumentException("Native noise resource is unavailable.");
        try
        {
            ResourceDimension dimension;noiseResource->GetType(&dimension);
            if(dimension!=ResourceDimension.Texture3D)throw new ArgumentException("Native noise resource is not3D.");
            Texture3DDesc texture;((ID3D11Texture3D*)noiseResource)->GetDesc(&texture);
            if(texture.Width!=32||texture.Height!=32||texture.Depth!=32||texture.MipLevels!=1||description.Texture3D.MostDetailedMip!=0)
                throw new ArgumentException("Native noise requires the complete single-mip32-cube.");
        }
        finally{noiseResource->Release();}        Upload(_material,material);Upload(_global,globals);Unbind();int write=1-_read;var ctx=_gpu.Context11;
        var outputs=stackalloc ID3D11RenderTargetView*[3]{_velocity[write]!.View.Handle,_offset[write]!.View.Handle,_force!.View.Handle};
        ctx.OMSetRenderTargets(3,outputs,(ID3D11DepthStencilView*)null);ctx.OMSetDepthStencilState((ID3D11DepthStencilState*)null,0);ctx.OMSetBlendState((ID3D11BlendState*)null,(float*)null,uint.MaxValue);
        ctx.RSSetState(_raster);var viewport=new Viewport(0,0,Size,Size,0,1);ctx.RSSetViewports(1,in viewport);
        ctx.IASetInputLayout((ID3D11InputLayout*)null);ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        ctx.VSSetShader(_vertex,(ID3D11ClassInstance**)null,0);ctx.GSSetShader((ID3D11GeometryShader*)null,(ID3D11ClassInstance**)null,0);ctx.PSSetShader(_pixel,(ID3D11ClassInstance**)null,0);
        var constants=stackalloc ID3D11Buffer*[2]{_material.Handle,_global.Handle};ctx.PSSetConstantBuffers(0,2,constants);var sampler=_sampler.Handle;ctx.PSSetSamplers(0,1,&sampler);
        var inputs=stackalloc ID3D11ShaderResourceView*[3]{_velocity[_read]!.Resource.Handle,_offset[_read]!.Resource.Handle,noise.Handle};ctx.PSSetShaderResources(0,3,inputs);
        try{ctx.Draw(3,0);_read=write;Frames++;}finally{Unbind();ctx.OMSetRenderTargets(0,(ID3D11RenderTargetView**)null,(ID3D11DepthStencilView*)null);}
    }
    private static bool ValidColor(Vector4 color)=>float.IsFinite(color.LengthSquared())&&color.X>=0&&color.Y>=0&&color.Z>=0&&color.W>=0&&color.X<=1&&color.Y<=1&&color.Z<=1&&color.W<=1;
    private static bool Finite(ReadOnlySpan<float> values){foreach(float value in values)if(!float.IsFinite(value))return false;return true;}
    private void Unbind(){var empty=stackalloc ID3D11ShaderResourceView*[3]{null,null,null};_gpu.Context11.PSSetShaderResources(0,3,empty);_gpu.Context11.VSSetShaderResources(0,3,empty);_gpu.Context11.VSSetShaderResources(12,1,empty);}
    private void Upload(ComPtr<ID3D11Buffer> buffer,ReadOnlySpan<float> values){fixed(float*p=values)_gpu.Context11.UpdateSubresource((ID3D11Resource*)buffer.Handle,0,(Box*)null,p,0,0);}
    private ComPtr<ID3D11Buffer> Buffer(int count){var desc=new BufferDesc{ByteWidth=(uint)count*4,Usage=Usage.Default,BindFlags=(uint)BindFlag.ConstantBuffer};ID3D11Buffer*p=null;SilkMarshal.ThrowHResult(_gpu.Device11.CreateBuffer(in desc,(SubresourceData*)null,&p));return p;}
    private sealed class Field:IDisposable
    {
        public ComPtr<ID3D11Texture2D> Texture;public ComPtr<ID3D11RenderTargetView> View;public ComPtr<ID3D11ShaderResourceView> Resource;
        public Field(GpuContext gpu,Format format){try{var desc=new Texture2DDesc{Width=Size,Height=Size,MipLevels=1,ArraySize=1,Format=format,SampleDesc=new(1,0),Usage=Usage.Default,BindFlags=(uint)(BindFlag.ShaderResource|BindFlag.RenderTarget)};ID3D11Texture2D*p=null;SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc,(SubresourceData*)null,&p));Texture=p;ID3D11RenderTargetView*v=null;SilkMarshal.ThrowHResult(gpu.Device11.CreateRenderTargetView((ID3D11Resource*)p,(RenderTargetViewDesc*)null,&v));View=v;ID3D11ShaderResourceView*s=null;SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)p,(ShaderResourceViewDesc*)null,&s));Resource=s;}catch{Dispose();throw;}}
        public void Dispose(){Resource.Dispose();View.Dispose();Texture.Dispose();}
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_vertex.Dispose();_pixel.Dispose();_material.Dispose();_global.Dispose();_sampler.Dispose();_raster.Dispose();foreach(var field in _velocity)field?.Dispose();foreach(var field in _offset)field?.Dispose();_force?.Dispose();}
}
