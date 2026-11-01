using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Shaders;

internal sealed unsafe class VegetationWindBindings : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Flags;
        public Matrix4x4 Bind;
        public Matrix4x4 InverseBind;
        public Vector4 Pivot, Leaf, D185, D186, D187, D188, Wind, LocalPosition, LocalParameters;
        public Vector4 FastParameters;
        public Vector4 FastOffsets;
    }
    private ComPtr<ID3D11Buffer> _buffer;
    private Constants _last;
    private bool _initialized;
    private ComPtr<ID3D11SamplerState> _fastSampler;
    public VegetationWindBindings(GpuContext gpu)
    {
        _buffer = GpuBuffers.CreateConstant<Constants>(gpu);
        var description = new SamplerDesc { Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxLOD = float.MaxValue };
        ID3D11SamplerState* sampler = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in description, &sampler)); _fastSampler = sampler;
    }
    public void Bind(ComPtr<ID3D11DeviceContext> context, GpuMesh mesh, NativeVegetationWindMaterial? material,
        float time, Vector4 wind, Vector3 localPosition, Vector2 localParameters)
    {
        var values = new Constants();
        if (material is not null && mesh.WindGeometry is { } geometry)
        {
            values.Flags = new(1, time, 0, 0); values.Bind = geometry.Bind; values.InverseBind = geometry.InverseBind;
            values.Pivot = material.Pivot; values.Leaf = new(material.Leaf, 0); values.D185 = new(material.D185, 0);
            values.D186 = new(material.D186, 0); values.D187 = new(material.D187, 0); values.D188 = new(material.D188, material.D189);
            values.Wind = wind; values.LocalPosition = new(localPosition, 0); values.LocalParameters = new(localParameters, 0, 0);
        }
        if (!_initialized || !MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref values, 1)).SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref _last, 1))))
        { GpuBuffers.UpdateConstant(context, _buffer, ref values); _last = values; _initialized = true; }
        var pointer = _buffer.Handle; context.VSSetConstantBuffers(4, 1, &pointer);
        ID3D11ShaderResourceView* empty = null; context.VSSetShaderResources(12, 1, &empty);
    }
    public void BindFast(ComPtr<ID3D11DeviceContext> context, NativeVegetationFastMaterial material, Vector2 origin, Vector2 snapOffset, ComPtr<ID3D11ShaderResourceView> offset)
    {
        var values = new Constants { Flags = new(2, 0, 0, 0), FastParameters = new(material.Displacement, origin.X, origin.Y), FastOffsets = new(snapOffset, 0, 0) };
        if (!_initialized || !MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref values, 1)).SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref _last, 1))))
        { GpuBuffers.UpdateConstant(context, _buffer, ref values); _last = values; _initialized = true; }
        var buffer = _buffer.Handle; context.VSSetConstantBuffers(4, 1, &buffer); var resource = offset.Handle; context.VSSetShaderResources(12, 1, &resource);
        var sampler = _fastSampler.Handle; context.VSSetSamplers(1, 1, &sampler);
    }
    public void Dispose() { _buffer.Dispose(); _fastSampler.Dispose(); }
    public const string Hlsl = """
cbuffer NativeWindCB:register(b4){
 float4 NativeWindFlags;row_major float4x4 NativeWindBind;row_major float4x4 NativeWindInverseBind;
 float4 NativeWindPivot,NativeWindLeaf,NativeWindD185,NativeWindD186,NativeWindD187,NativeWindD188;
 float4 NativeWindGlobal,NativeWindLocalPosition,NativeWindLocalParameters;
 float4 NativeFastParameters;
 float4 NativeFastOffsets;
}
Texture2D<float4> NativeFastOffset:register(t12);SamplerState NativeFastSampler:register(s1);
""" + NativeVegetationFastShader.Functions + NativeVegetationWindShader.Functions + """
float3 ApplyNativeVegetationWind(float3 modelPosition,float4 color0,float flexibility,row_major float4x4 world){
 if(NativeWindFlags.x<.5)return mul(float4(modelPosition,1),world).xyz;
 if(NativeWindFlags.x>1.5)return EvaluateNativeFastVegetationWind(mul(float4(modelPosition,1),world).xyz,world[3].xy,color0.r,
 NativeFastParameters.xy,float2(0,0),NativeFastParameters.zw,NativeFastOffsets.xy,NativeFastOffset,NativeFastSampler);
 float3 raw=mul(float4(modelPosition,1),NativeWindInverseBind).xyz;
 return EvaluateNativeLargeVegetationWind(raw,color0,flexibility,mul(NativeWindBind,world),NativeWindPivot,NativeWindLeaf.xyz,
 NativeWindD185.xyz,NativeWindD186.xyz,NativeWindD187.xyz,NativeWindD188.xyz,NativeWindD188.w,
 NativeWindGlobal,NativeWindLocalPosition,NativeWindLocalParameters,NativeWindFlags.y);
}
""";
}
