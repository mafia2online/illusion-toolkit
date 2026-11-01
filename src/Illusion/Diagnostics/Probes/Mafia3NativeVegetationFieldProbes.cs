using System.Numerics;
using System.Security.Cryptography;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestNativeVegetationField(Action<string,bool> check)
    {
        using var gpu=new GpuContext();using var field=new NativeVegetationWindField(gpu);
        using var noise=Noise(32);using var wrongNoise=Noise(8);
        float[] material=[1,1,0,0,8,1,.4f,0];var globals=new float[540];globals[52]=1;globals[55]=2;
        for(int i=0;i<4;i++)globals[228+i]=1;
        for(int i=0;i<8;i++){globals[(83+i)*4]=1000;globals[(83+i)*4+1]=1000;globals[(83+i)*4+3]=.0001f;}
        bool refusal=false;try{field.Step(material,globals,noise);}catch(InvalidOperationException){refusal=true;}
        check("Native FAST field requires explicit analysis-history reset",refusal&&field.Frames==0);
        field.Reset(new(.5f,.5f,0,0),new(.5f,.5f,0,0));string initial=Read(field.Offset);
        check("Native FAST field uses native RG8 velocity/RGBA8 offset formats",FormatOf(field.Velocity)==Format.FormatR8G8Unorm&&FormatOf(field.Offset)==Format.FormatR8G8B8A8Unorm);
        refusal=false;try{field.Reset(new(float.NaN),Vector4.Zero);}catch(ArgumentException){refusal=true;}
        check("Native FAST field invalid reset is atomic",refusal&&Read(field.Offset)==initial&&field.Frames==0);
        refusal=false;try{field.Step(material,globals,wrongNoise);}catch(ArgumentException){refusal=true;}
        check("Native FAST field rejects wrong noise dimensions before writes",refusal&&Read(field.Offset)==initial&&field.Frames==0);
        material[6]=0;refusal=false;try{field.Step(material,globals,noise);}catch(ArgumentException){refusal=true;}
        check("Native FAST field invalid damping is atomic",refusal&&Read(field.Offset)==initial&&field.Frames==0);material[6]=.4f;
        for(int i=0;i<16;i++){globals[212]=i/60f;globals[216]=(i+1)/60f;field.Step(material,globals,noise);}
        string moved=Read(field.Offset),velocity=Read(field.Velocity);
        check("Native FAST field submits bounded single steps and produces motion",field.Frames==16&&moved!=initial);
        field.Reset(new(.5f,.5f,0,0),new(.5f,.5f,0,0));
        for(int i=0;i<16;i++){globals[212]=i/60f;globals[216]=(i+1)/60f;field.Step(material,globals,noise);}
        check("Native FAST field explicit reset/replay restores exact offset and velocity",Read(field.Offset)==moved&&Read(field.Velocity)==velocity);
        long before=field.Frames;globals[216]=float.NaN;refusal=false;try{field.Step(material,globals,noise);}catch(ArgumentException){refusal=true;}
        check("Native FAST field nonfinite constants preserve completed history",refusal&&field.Frames==before&&Read(field.Offset)==moved);

        ComPtr<ID3D11ShaderResourceView> Noise(uint size)
        {
            var data=new byte[size*size*size];Array.Fill(data,(byte)192);
            var desc=new Texture3DDesc{Width=size,Height=size,Depth=size,MipLevels=1,Format=Format.FormatR8Unorm,Usage=Usage.Immutable,BindFlags=(uint)BindFlag.ShaderResource};
            ID3D11Texture3D*p=null;fixed(byte*b=data){var initial=new SubresourceData{PSysMem=b,SysMemPitch=size,SysMemSlicePitch=size*size};SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture3D(in desc,in initial,&p));}
            try{ID3D11ShaderResourceView*v=null;SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)p,(ShaderResourceViewDesc*)null,&v));return v;}finally{p->Release();}
        }
        static Format FormatOf(ComPtr<ID3D11ShaderResourceView> view){ShaderResourceViewDesc desc;view.GetDesc(&desc);return desc.Format;}
        string Read(ComPtr<ID3D11ShaderResourceView> view)
        {
            gpu.Context11.OMSetRenderTargets(0,(ID3D11RenderTargetView**)null,(ID3D11DepthStencilView*)null);
            ID3D11Resource*resource=null;view.Handle->GetResource(&resource);
            try
            {
                Texture2DDesc desc;((ID3D11Texture2D*)resource)->GetDesc(&desc);int bytes=desc.Format==Format.FormatR8G8Unorm?2:4;
                desc.Usage=Usage.Staging;desc.BindFlags=0;desc.CPUAccessFlags=(uint)CpuAccessFlag.Read;ID3D11Texture2D*p=null;
                SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc,(SubresourceData*)null,&p));using ComPtr<ID3D11Texture2D>staging=p;
                gpu.Context11.CopyResource((ID3D11Resource*)p,resource);MappedSubresource map;SilkMarshal.ThrowHResult(gpu.Context11.Map((ID3D11Resource*)p,0,Map.Read,0,&map));
                try{using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);for(int y=0;y<desc.Height;y++)hash.AppendData(new ReadOnlySpan<byte>((byte*)map.PData+y*map.RowPitch,(int)desc.Width*bytes));return Convert.ToHexString(hash.GetHashAndReset());}
                finally{gpu.Context11.Unmap((ID3D11Resource*)p,0);}
            }
            finally{resource->Release();}
        }
    }
}
