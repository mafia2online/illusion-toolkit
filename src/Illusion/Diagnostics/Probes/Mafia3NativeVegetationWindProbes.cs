using System.Numerics;
using System.Text.Json;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Domain;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestNativeVegetationWind(Action<string, bool> check)
    {
        using var gpu = new GpuContext(); using var compiler = D3DCompiler.GetApi(); var context = gpu.Context11;
        const string vertexSource = "float4 main(uint id:SV_VertexID):SV_Position{float2 uv=float2((id<<1)&2,id&2);return float4(uv*float2(2,-2)+float2(-1,1),0,1);}";
        string pixelSource = "cbuffer B:register(b0){float4 D[16];}" + NativeVegetationWindShader.Functions + "float4 main():SV_Target{return float4(EvaluateNativeLargeVegetationWind(D[0].xyz,D[1],D[0].w,float4x4(D[2],D[3],D[4],D[5]),D[6],D[7].xyz,D[8].xyz,D[9].xyz,D[10].xyz,D[11].xyz,D[11].w,D[12],D[13],D[14],D[15].x),1);}";
        using var vertexCode = ShaderCompiler.Compile(compiler, vertexSource, "main", "vs_5_0", "vegetation-wind-golden");
        using var pixelCode = ShaderCompiler.Compile(compiler, pixelSource, "main", "ps_5_0", "vegetation-wind-golden");
        var shaders = ShaderCompiler.CreateShaders(gpu, vertexCode, pixelCode); using var vertex = shaders.Item1; using var pixel = shaders.Item2;
        var textureDescription = new Texture2DDesc { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.FormatR32G32B32A32Float,
            SampleDesc = new(1, 0), Usage = Usage.Default, BindFlags = (uint)BindFlag.RenderTarget };
        ID3D11Texture2D* texturePointer = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in textureDescription, (SubresourceData*)null, &texturePointer));
        using ComPtr<ID3D11Texture2D> texture = texturePointer;
        ID3D11RenderTargetView* targetPointer = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRenderTargetView((ID3D11Resource*)texturePointer, (RenderTargetViewDesc*)null, &targetPointer));
        using ComPtr<ID3D11RenderTargetView> target = targetPointer;
        textureDescription.Usage = Usage.Staging; textureDescription.BindFlags = 0; textureDescription.CPUAccessFlags = (uint)CpuAccessFlag.Read;
        ID3D11Texture2D* stagingPointer = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in textureDescription, (SubresourceData*)null, &stagingPointer));
        using ComPtr<ID3D11Texture2D> staging = stagingPointer;
        var bufferDescription = new BufferDesc { ByteWidth = 256, Usage = Usage.Default, BindFlags = (uint)BindFlag.ConstantBuffer };
        ID3D11Buffer* bufferPointer = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateBuffer(in bufferDescription, (SubresourceData*)null, &bufferPointer));
        using ComPtr<ID3D11Buffer> buffer = bufferPointer;
        context.IASetInputLayout((ID3D11InputLayout*)null); context.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        context.VSSetShader(vertex.Handle, (ID3D11ClassInstance**)null, 0); context.PSSetShader(pixel.Handle, (ID3D11ClassInstance**)null, 0);
        context.PSSetConstantBuffers(0, 1, &bufferPointer); context.OMSetRenderTargets(1, &targetPointer, (ID3D11DepthStencilView*)null);
        var viewport = new Silk.NET.Direct3D11.Viewport(0, 0, 1, 1, 0, 1); context.RSSetViewports(1, in viewport);
        Vector3 Sample(float[] data)
        {
            fixed (float* pointer = data) context.UpdateSubresource((ID3D11Resource*)buffer.Handle, 0, (Box*)null, pointer, 0, 0);
            context.Draw(3, 0); context.CopyResource((ID3D11Resource*)staging.Handle, (ID3D11Resource*)texture.Handle);
            MappedSubresource mapped; SilkMarshal.ThrowHResult(context.Map((ID3D11Resource*)staging.Handle, 0, Map.Read, 0, &mapped));
            var result = *(Vector3*)mapped.PData; context.Unmap((ID3D11Resource*)staging.Handle, 0); return result;
        }
        using var goldens = JsonDocument.Parse(NativeVegetationWindGoldens);
        var cases = goldens.RootElement.EnumerateArray().Select(item => (Data: item.GetProperty("Data").EnumerateArray().Select(number => number.GetSingle()).ToArray(),
            Expected: item.GetProperty("Value").EnumerateArray().Select(number => number.GetSingle()).ToArray())).ToArray();
        float maximum = 0;
        foreach (var item in cases)
        {
            var expected = new Vector3(item.Expected[0], item.Expected[1], item.Expected[2]);
            maximum = Math.Max(maximum, Vector3.Distance(Sample(item.Data), expected));
        }
        check("native LARGE foliage current-position GPU matches nine original VS6954 affine/material/color/context goldens", cases.Length == 9 && maximum <= 2e-6f);
        var input = cases[0].Data.ToArray(); var before = Sample(input); input[60] += 1.25f; var moved = Sample(input); input[60] -= 1.25f;
        check("native foliage time changes geometry and deterministic rewind restores exact GPU position", Vector3.Distance(before, moved) > 1e-5f && before == Sample(input));
        input = cases[0].Data.ToArray(); input[3] = 0; input[43] = .5f; input[45] = 1; input[41] = 1; input[42] = 1; var noFlexibility = Sample(input); input[3] = 1;
        check("verified second native BGRA COLOR alpha controls foliage leaf/detail displacement", Vector3.Distance(noFlexibility, Sample(input)) > 1e-8f);
    }

    internal static void TestNativeVegetationWindBounds(Action<string, bool> check)
    {
        using var goldens = JsonDocument.Parse(NativeVegetationWindGoldens); int accepted = 0; bool contained = true;
        GpuMesh.VegetationWindGeometry? firstGeometry = null; NativeVegetationWindMaterial? firstMaterial = null; Matrix4x4 firstMatrix = default;
        foreach (var item in goldens.RootElement.EnumerateArray())
        {
            var d = item.GetProperty("Data").EnumerateArray().Select(n => n.GetSingle()).ToArray();
            var expected = item.GetProperty("Value").EnumerateArray().Select(n => n.GetSingle()).ToArray();
            Vector3 V(int at) => new(d[at], d[at + 1], d[at + 2]);
            var matrix = new Matrix4x4(d[8], d[9], d[10], d[11], d[12], d[13], d[14], d[15], d[16], d[17], d[18], d[19], d[20], d[21], d[22], d[23]);
            var material = new NativeVegetationWindMaterial(new(d[24], d[25], d[26], d[27]), V(28), V(32), V(36), V(40), V(44), d[47]);
            var geometry = new GpuMesh.VegetationWindGeometry([V(0)], [0], Matrix4x4.Identity, Matrix4x4.Identity, true, true);
            var result = NativeVegetationWindBounds.Calculate(geometry, [material], [matrix], [], Vector3.Zero, Vector3.Zero,
                new(d[48], d[49], d[50], d[51]), V(52), new(d[56], d[57]), 600, out _);
            if (result is null) continue;
            var value = new Vector3(expected[0], expected[1], expected[2]); var box = result.Bounds[0];
            contained &= Vector3.Min(value, box.Min) == box.Min && Vector3.Max(value, box.Max) == box.Max; accepted++;
            firstGeometry ??= geometry; firstMaterial ??= material; firstMatrix = matrix;
        }
        check("conservative native wind frustum bounds contain every accepted original GPU golden", accepted == 9 && contained);
        var zero = firstGeometry! with { Positions = [Vector3.Zero] };
        var singular = NativeVegetationWindBounds.Calculate(zero, [firstMaterial!], [firstMatrix], [], Vector3.Zero, Vector3.Zero,
            Vector4.UnitX, new(1000), Vector2.Zero, 0, out string reason);
        check("native origin degeneracy fails closed per mesh before GPU deformation", singular is null && reason.Contains("zero-height", StringComparison.Ordinal));
        var baked = firstGeometry! with { NativeObjectOrigin = false };
        var unsupported = NativeVegetationWindBounds.Calculate(baked, [firstMaterial!], [firstMatrix], [], Vector3.Zero, Vector3.Zero,
            Vector4.UnitX, new(1000), Vector2.Zero, 0, out reason);
        check("native baked/rest wind without proven object origin remains static with an explicit reason", unsupported is null && reason.Contains("object origin", StringComparison.Ordinal));
        long work = 0;
        bool first = NativeVegetationWindWorkBudget.TryReserve(1_500_000, ref work, out _);
        bool second = NativeVegetationWindWorkBudget.TryReserve(1_500_000, ref work, out _);
        bool third = NativeVegetationWindWorkBudget.TryReserve(1_500_000, ref work, out bool oversized);
        bool giant = NativeVegetationWindWorkBudget.TryReserve(2_000_001, ref work, out bool giantReason);
        check("native wind preview bounds CPU work without blocking on giant or queued mesh validation", first && second && !third && !oversized && !giant && giantReason && work == 3_000_000);
    }

    private const string NativeVegetationWindGoldens = """
[{"Data":[2.5196,3.46,1.7608,0.9909772,0.9978954,0.9358663,0.9466463,0.4488618,0.79324955,0.10370731,0,0,-0.10370731,0.79324955,0,0,0,0,0.8,0,-17,-24,0.2,1,0.1,0.3,0.2,5,0,2,0.05,0,0.2,0.3,3,0,1.3,0.05,20,0,0.0015,0.03,0.1,0,0.008,0,0,0.03,0.5403023,0.84147096,0,1.0298331,2000,2000,0,0,0.001,0,0,0,8.219693,0,0,0],"Value":[-15.360151,-20.994053,1.6085076]},{"Data":[-1.0595001,1.3701997,1.8998,0.79951227,0.23140268,0.9377732,0.46633488,0.84079796,1.0630289,0.2827886,0,0,-0.2827886,1.0630289,0,0,0,0,1.1,0,-16,-23,0.4,1,0.1,0.3,0.2,0,0,2,0.05,0,0.2,0.3,3,0,1.3,0.05,20,0,0.0015,0.03,0.1,0,0.008,0,0,0.03,-0.41614684,0.9092974,0,5.1882873,2000,2000,0,0,0.001,0,0,0,72.322845,0,0,0],"Value":[-17.513199,-21.843943,2.4903429]},{"Data":[1.5804996,-2.9863,2.6107,0.19893149,0.06374456,0.17361425,0.88273937,0.7818044,1.2948728,0.5322638,0,0,-0.5322638,1.2948728,0,0,0,0,1.4000001,0,-15,-22,0.6,1,0.1,0.3,0.2,5,0,2,0.05,0,0.2,0.3,3,0,1.3,0.05,20,0,0.0015,0.03,0.1,0,0.008,0,0,0.03,-0.9899925,0.14112,0,4.9501233,2,3,0,0,20,0.8,0,0,176.32474,0,0,0],"Value":[-11.363988,-25.02564,4.2548847]},{"Data":[1.6819997,0.4413998,1.4077,0.8634461,0.31705436,0.13042656,0.67689264,0.77708584,1.4752927,0.8446962,0,0,-0.8446962,1.4752927,0,0,0,0,1.7,0,-14,-21,0.8,1,0.1,0.3,0.2,0,0,2,0.05,0,0.2,0.3,3,0,1.3,0.05,20,0,0.0015,0.03,0.1,0,0.008,0,0,0.03,-0.6536436,-0.7568025,0,2.9118934,2000,2000,0,0,0.001,0,0,0,196.9696,0,0,0],"Value":[-11.891427,-18.928068,3.1931424]},{"Data":[1.9604001,-2.3219,1.0968,0.04861444,0.1061179,0.75678235,0.71413493,0.6819202,0.3980419,0.3025932,0,0,-0.3025932,0.3980419,0,0,0,0,0.5,0,-13,-20,1,1,0.1,0.3,0.2,5,0,2,0.05,0,0.2,0.3,3,0,1.3,0.05,20,0,0.0015,0.03,0.1,0,0.008,0,0,0.03,0.2836622,-0.9589243,0,2.4947262,2000,2000,0,0,0.001,0,0,0,106.12358,0,0,0],"Value":[-11.517112,-20.331005,1.5488847]},{"Data":[2.8909998,0.1264,0.0167,0.35691705,0.21757454,0.2680347,0.009918871,0.008536382,0.56873083,0.5626235,0,0,-0.5626235,0.56873083,0,0,0,0,0.8,0,-12,-19,1.2,1,0.1,0.3,0.2,0,0,2,0.05,0,0.2,0.3,3,0,1.3,0.05,20,0,0.0015,0.03,0.1,0,0.008,0,0,0.03,0.96017027,-0.2794155,0,3.61472,2,3,0,0,20,0.8,0,0,28.12604,0,0,0],"Value":[-10.426915,-17.301567,1.2127602]},{"Data":[-2.964,-2.6216,1.5207,0.4450764,0.13736366,0.22046292,0.3308684,0.67978966,0.67512035,0.8684541,0,0,-0.8684541,0.67512035,0,0,0,0,1.1,0,-11,-18,0,1,0.1,0.3,0.2,5,0,2,0.05,0,0.2,0.3,3,0,1.3,0.05,20,0,0.0015,0.03,0.1,0,0.008,0,0,0.03,0.75390226,0.6569866,0,4.9987946,2000,2000,0,0,0.001,0,0,0,173.11899,0,0,0],"Value":[-10.724971,-22.344505,1.6752689]},{"Data":[0.6831,-0.8510001,2.3133,0.67982024,0.86865205,0.63110596,0.011768313,0.4461339,0.70870847,1.207366,0,0,-1.207366,0.70870847,0,0,0,0,1.4000001,0,-10,-17,0.2,1,0.1,0.3,0.2,0,0,2,0.05,0,0.2,0.3,3,0,1.3,0.05,20,0,0.0015,0.03,0.1,0,0.008,0,0,0.03,-0.14550003,0.98935825,0,1.3108586,2000,2000,0,0,0.001,0,0,0,87.71853,0,0,0],"Value":[-8.488421,-16.778341,3.438567]},{"Data":[0.3564999,1.3134999,2.5957,0.54583454,0.11590813,0.11813295,0.070782356,0.07755232,0.6632579,1.565276,0,0,-1.565276,0.6632579,0,0,0,0,1.7,0,-9,-16,0.4,1,0.1,0.3,0.2,5,0,2,0.05,0,0.2,0.3,3,0,1.3,0.05,20,0,0.0015,0.03,0.1,0,0.008,0,0,0.03,-0.91113025,0.4121185,0,0.8912416,2,3,0,0,20,0.8,0,0,158.2558,0,0,0],"Value":[-10.819375,-14.570618,4.812941]}]
""";
}
