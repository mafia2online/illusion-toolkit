using System.IO;
using System.Numerics;
using System.Reflection;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Scene;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestNativeWaterFoamContext(Action<string, bool> check)
    {
        using var gpu = new GpuContext(); using var renderer = new SceneRenderer(gpu);
        using var target = new SharedRenderTarget(gpu, 128, 96); using var context = new NativeWaterFoamContext(gpu);
        var camera = new Camera { Near = .5f, Far = 3000, AspectRatio = 128f / 96 };
        camera.LookAt(new(0, -20, 15), Vector3.Zero);
        var surface = CompiledSurfaceMaterials.WaterDefinition with { NativeWaterDefinition = new("", "") };
        var mesh = renderer.AddMesh(new MeshData
        {
            Name = "foam-height-plane", World = Matrix4x4.Identity,
            Positions = [new(-32, -32, 0), new(32, -32, 0), new(32, 32, 0), new(-32, 32, 0)],
            Normals = Enumerable.Repeat(Vector3.UnitZ, 4).ToArray(), Indices = [0, 1, 2, 0, 2, 3],
            Parts = [new MeshPart(0, 6, null, surface: surface)]
        });
        using var compiler = D3DCompiler.GetApi();
        var compile = typeof(SceneRenderer).Assembly.GetType("Illusion.Rendering.Shaders.ShaderCompiler")!.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static)!;
        const string planeSource = "cbuffer A:register(b0){float4 D;}float4 main(uint id:SV_VertexID):SV_Position{float2 uv=float2((id<<1)&2,id&2);return float4(uv*float2(2,-2)+float2(-1,1),D.x,1);}";
        using var code = (ComPtr<ID3D10Blob>)compile.Invoke(null, [compiler, planeSource, "main", "vs_5_0", "foam-depth-plane"])!;
        ID3D11VertexShader* vertex = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateVertexShader(code.GetBufferPointer(), code.GetBufferSize(), (ID3D11ClassLinkage*)null, &vertex));
        using ComPtr<ID3D11VertexShader> shader = vertex;
        var bufferDesc = new BufferDesc { ByteWidth = 16, Usage = Usage.Default, BindFlags = (uint)BindFlag.ConstantBuffer };
        ID3D11Buffer* bufferPointer = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateBuffer(in bufferDesc, (SubresourceData*)null, &bufferPointer));
        using ComPtr<ID3D11Buffer> buffer = bufferPointer;
        var rasterDesc = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true };
        ID3D11RasterizerState* raster = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rasterDesc, &raster)); using ComPtr<ID3D11RasterizerState> rasterState = raster;
        var depthDesc = new DepthStencilDesc { DepthEnable = true, DepthWriteMask = DepthWriteMask.All, DepthFunc = ComparisonFunc.LessEqual };
        ID3D11DepthStencilState* depth = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in depthDesc, &depth)); using ComPtr<ID3D11DepthStencilState> depthState = depth;

        byte[] Read(ComPtr<ID3D11ShaderResourceView> view, int bytesPerPixel)
        {
            ID3D11Resource* resource = null; view.GetResource(&resource);
            try
            {
                Texture2DDesc desc; ((ID3D11Texture2D*)resource)->GetDesc(&desc);
                desc.Usage = Usage.Staging; desc.BindFlags = 0; desc.CPUAccessFlags = (uint)CpuAccessFlag.Read;
                ID3D11Texture2D* staging = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, &staging));
                using ComPtr<ID3D11Texture2D> owned = staging; gpu.Context11.CopyResource((ID3D11Resource*)staging, resource);
                MappedSubresource map; SilkMarshal.ThrowHResult(gpu.Context11.Map((ID3D11Resource*)staging, 0, Map.Read, 0, &map));
                var bytes = new byte[checked((int)(desc.Width * desc.Height) * bytesPerPixel)];
                try { for (int y = 0; y < desc.Height; y++) new ReadOnlySpan<byte>((byte*)map.PData + y * map.RowPitch, (int)desc.Width * bytesPerPixel).CopyTo(bytes.AsSpan(y * (int)desc.Width * bytesPerPixel)); }
                finally { gpu.Context11.Unmap((ID3D11Resource*)staging, 0); }
                return bytes;
            }
            finally { resource->Release(); }
        }
        void Plane(float distance)
        {
            var ctx = gpu.Context11;
            ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, target.Dsv.Handle);
            ctx.ClearDepthStencilView(target.Dsv.Handle, (uint)ClearFlag.Depth, 1, 0);
            ctx.OMSetDepthStencilState(depthState.Handle, 0); ctx.RSSetState(rasterState.Handle);
            var vp = new Silk.NET.Direct3D11.Viewport(0, 0, 128, 96, 0, 1); ctx.RSSetViewports(1, &vp);
            ctx.IASetInputLayout((ID3D11InputLayout*)null); ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
            ctx.VSSetShader(shader.Handle, null, 0); ctx.PSSetShader((ID3D11PixelShader*)null, null, 0);
            var data = new Vector4(camera.Far / (camera.Far - camera.Near) - camera.Near * camera.Far / (camera.Far - camera.Near) / distance, 0, 0, 0);
            ctx.UpdateSubresource((ID3D11Resource*)buffer.Handle, 0, (Box*)null, &data, 0, 0);
            var b = buffer.Handle; ctx.VSSetConstantBuffers(0, 1, &b); ctx.Draw(3, 0);
        }
        foreach (float z in new[] { 1f, 10f, 100f, 1000f })
        {
            Plane(z); bool ready = context.Prepare(target, camera, [mesh], out _);
            byte[] pixels = Read(context.LinearDepth, 4); float error = 0;
            for (int i = 0; i < pixels.Length; i += 4) error = MathF.Max(error, MathF.Abs(BitConverter.ToSingle(pixels, i) - z / camera.Far));
            check($"native foam private D24 copy linearizes GPU plane at {z}", ready && error < 3e-5f);
        }
        var adapter = context.CameraConstants!; ReadOnlySpan<float> view = adapter.ViewConstants.Span;
        Vector3 point = camera.Position + camera.Forward * 10 + camera.Right * 2;
        Vector4 world = new(point, 1), expected = Vector4.Transform(world, camera.ViewProjection);
        Vector4 actual = new(Vector4.Dot(new(view[88], view[89], view[90], view[91]), world), Vector4.Dot(new(view[92], view[93], view[94], view[95]), world),
            Vector4.Dot(new(view[96], view[97], view[98], view[99]), world), Vector4.Dot(new(view[100], view[101], view[102], view[103]), world));
        check("native foam camera column packing matches world projection", Vector4.Distance(expected, actual) < .0001f);
        float ndcX = expected.X / expected.W, ndcY = expected.Y / expected.W;
        var localRay = new Vector3(ndcX * view[86] * view[85], view[85], ndcY * view[87] * view[86] * view[85]);
        Vector3 ray = new(Vector3.Dot(new(view[120], view[121], view[122]), localRay), Vector3.Dot(new(view[124], view[125], view[126]), localRay),
            Vector3.Dot(new(view[128], view[129], view[130]), localRay));
        check("native foam camera rays reconstruct forward-normalized depth", Vector3.Distance(camera.Position + ray * (10 / camera.Far), point) < .0001f);
        byte[] baseline = Read(context.Height, 2);
        check("native foam height capture and expansion preserve zero-height plane", Enumerable.Range(0, baseline.Length / 2).Any(i => BitConverter.ToUInt16(baseline, i * 2) == 0));
        mesh.World = Matrix4x4.CreateTranslation(0, 0, .25f); mesh.BoundsMin += Vector3.UnitZ * .25f; mesh.BoundsMax += Vector3.UnitZ * .25f;
        context.Prepare(target, camera, [mesh], out _); byte[] raised = Read(context.Height, 2);
        check("native foam height capture consumes world placement", !raised.AsSpan().SequenceEqual(baseline) && Enumerable.Range(0, raised.Length / 2).Any(i => BitConverter.ToUInt16(raised, i * 2) == BitConverter.HalfToUInt16Bits((Half).25f)));
        mesh.World = Matrix4x4.Identity; mesh.BoundsMin -= Vector3.UnitZ * .25f; mesh.BoundsMax -= Vector3.UnitZ * .25f;
        context.Prepare(target, camera, [mesh], out _);
        check("native foam height placement undo restores every GPU byte", Read(context.Height, 2).AsSpan().SequenceEqual(baseline));
        camera.Orthographic = true;
        check("native foam context refuses orthographic before writes", !context.Prepare(target, camera, [mesh], out _) && Read(context.Height, 2).AsSpan().SequenceEqual(baseline)); camera.Orthographic = false;
        mesh.Ghost = true;
        check("native foam context refuses ghost-only definition batch", !context.Prepare(target, camera, [mesh], out _) && Read(context.Height, 2).AsSpan().SequenceEqual(baseline)); mesh.Ghost = false;

        // Retail resources remain optional, and editable source files must be copied fixtures.
        string root = Path.GetFullPath(MafiaEnvironment.GameRoot ?? ".");
        if (root.Contains(Path.DirectorySeparatorChar + ".scratch" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(MafiaEnvironment.CityUniversSds))
        {
            var document = new CompiledSceneDocument(new FileInfo(MafiaEnvironment.CityUniversSds));
            var definitions = document.Meshes.Where(m => m.Parts.Any(p => p.Surface?.NativeWaterDefinition is not null)).ToArray();
            var uploaded = definitions.Select(renderer.AddMesh).ToArray();
            foreach (GpuMesh definition in uploaded)
            {
                Vector3 center = (definition.BoundsMin + definition.BoundsMax) * .5f;
                camera.LookAt(center + new Vector3(0, -20, 20), center); Plane(10);
                bool ready = context.Prepare(target, camera, [definition], out _); byte[] pixels = Read(context.Height, 2);
                check("native foam copied definition height " + definitions[Array.IndexOf(uploaded, definition)].Name,
                    ready && Enumerable.Range(0, pixels.Length / 2).Any(i => BitConverter.ToUInt16(pixels, i * 2) != BitConverter.HalfToUInt16Bits((Half)(-65504))));
            }
            if (uploaded.Length != 0)
            {
                IEnumerable<SdsFrameNode> Walk(SdsFrameNode n) { yield return n; foreach (var c in n.Children) foreach (var item in Walk(c)) yield return item; }
                var frame = document.Roots.SelectMany(Walk).Select(n => n.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().FirstOrDefault(f => f.Name == definitions[0].Name);
                if (frame is not null)
                {
                    var bytes = document.Graph.Resources.Select(r => r.ToArray()).ToArray(); Matrix4x4 before = frame.LocalTransform;
                    camera.LookAt(new(1777.0494f, -2774.3513f, 15), new(1777.0494f, -2740, 0)); Plane(10);
                    context.Prepare(target, camera, uploaded, out _); byte[] original = Read(context.Height, 2);
                    try
                    {
                        Matrix4x4 moved = before; moved.M43 += .25f; frame.LocalTransform = moved; uploaded[0].World = frame.WorldTransform;
                        context.Prepare(target, camera, uploaded, out _);
                        check("native foam copied canonical definition Z changes captured height", !Read(context.Height, 2).AsSpan().SequenceEqual(original));
                    }
                    finally { frame.LocalTransform = before; uploaded[0].World = frame.WorldTransform; }
                    context.Prepare(target, camera, uploaded, out _);
                    check("native foam copied canonical undo restores all GENR and GPU bytes", Read(context.Height, 2).AsSpan().SequenceEqual(original)
                        && document.Graph.Resources.Select((r, i) => r.ToArray().AsSpan().SequenceEqual(bytes[i])).All(v => v));
                }
            }
        }
        context.Dispose();
        check("native foam context disposed outputs cannot be borrowed", context.Height.Handle == null && context.LinearDepth.Handle == null && !context.Ready && !context.Prepare(target, camera, [mesh], out _));
    }
}
