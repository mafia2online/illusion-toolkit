using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

[StructLayout(LayoutKind.Sequential)]
public struct ZoneConstants
{
    public Matrix4x4 Wvp; // load as-is (reinterpret-as-column transposes)
    public Vector4 Color; // rgb + alpha
}

/// <summary>One loading zone to draw: its world box, the colour of its district, whether it is of the kind that
/// loads a district for a player who appears inside it, whether it is the one picked, and whether it was added
/// to the game's own zones - drawn with a second frame round it, so that it is told from them at a glance.</summary>
public readonly record struct ZoneBox(Vector3 Min, Vector3 Max, Vector3 Color, bool Loads, bool Picked, bool Added = false);

/// <summary>
/// Draws the AREA load zones so that they can be read: each zone as the EDGES of its box with a faint fill, both
/// hidden by the scene in front of them. Hundreds of filled boxes laid over one another - the city is tiled two
/// and three deep - came out as one coloured haze with no box to be told from the next. A zone's fill is left
/// out while the camera is inside it, zones fade with distance, the kind that does not load a district by itself
/// is drawn fainter, and the picked zone is drawn full and seen through walls.
/// </summary>
public sealed unsafe class ZoneRenderer : IDisposable
{
    private const string Hlsl = @"
cbuffer CB : register(b0) { float4x4 WVP; float4 Color; };
struct VSIn { float3 pos : POSITION; };
struct PSIn { float4 pos : SV_POSITION; };
PSIn VSMain(VSIn i){ PSIn o; o.pos = mul(WVP, float4(i.pos, 1.0)); return o; }
float4 PSMain(PSIn i) : SV_TARGET { return Color; }";

    private ComPtr<ID3D11VertexShader> _vs;
    private ComPtr<ID3D11PixelShader> _ps;
    private ComPtr<ID3D11InputLayout> _layout;
    private ComPtr<ID3D11Buffer> _cb;
    private ComPtr<ID3D11Buffer> _vb;
    private ComPtr<ID3D11Buffer> _ib;
    private ComPtr<ID3D11BlendState> _blend;
    private ComPtr<ID3D11DepthStencilState> _noDepth;
    private ComPtr<ID3D11RasterizerState> _raster;

    // Unit box [0,1]^3.
    private static readonly float[] CubeVerts =
    {
        0,0,0, 1,0,0, 1,1,0, 0,1,0,
        0,0,1, 1,0,1, 1,1,1, 0,1,1,
    };
    private static readonly uint[] CubeIndices =
    {
        0,1,2, 0,2,3,  4,6,5, 4,7,6,
        0,3,7, 0,7,4,  1,5,6, 1,6,2,
        0,4,5, 0,5,1,  3,2,6, 3,6,7,
    };
    private static readonly uint[] EdgeIndices =
    {
        0,1, 1,2, 2,3, 3,0,  4,5, 5,6, 6,7, 7,4,  0,4, 1,5, 2,6, 3,7,
    };

    private const float FadeFrom = 300f;       // metres from the camera at which a zone starts to fade...
    private const float FadeTo = 1500f;        // ...and is no longer drawn
    private const float AddedFrame = 2.5f;     // metres between an added zone's box and the second frame round it

    private ComPtr<ID3D11Buffer> _edges;

    public ZoneRenderer(GpuContext gpu)
    {
        using D3DCompiler compiler = D3DCompiler.GetApi();
        ComPtr<ID3D10Blob> vsCode = ShaderCompiler.Compile(compiler, Hlsl, "VSMain", "vs_5_0", "zone");
        ComPtr<ID3D10Blob> psCode = ShaderCompiler.Compile(compiler, Hlsl, "PSMain", "ps_5_0", "zone");
        (_vs, _ps) = ShaderCompiler.CreateShaders(gpu, vsCode, psCode);

        byte* posName = (byte*)SilkMarshal.StringToPtr("POSITION");
        InputElementDesc elem = ShaderCompiler.VertexElement(posName, 0, Format.FormatR32G32B32Float, 0);
        ID3D11InputLayout* layout = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateInputLayout(
            &elem, 1, vsCode.GetBufferPointer(), vsCode.GetBufferSize(), ref layout));
        _layout = layout;
        SilkMarshal.Free((nint)posName);
        vsCode.Dispose();
        psCode.Dispose();

        // Box.
        fixed (float* pv = CubeVerts)
            _vb = GpuBuffers.CreateImmutable(gpu, pv, (uint)(CubeVerts.Length * sizeof(float)), BindFlag.VertexBuffer);
        fixed (uint* pi = CubeIndices)
            _ib = GpuBuffers.CreateImmutable(gpu, pi, (uint)(CubeIndices.Length * sizeof(uint)), BindFlag.IndexBuffer);
        fixed (uint* pe = EdgeIndices)
            _edges = GpuBuffers.CreateImmutable(gpu, pe, (uint)(EdgeIndices.Length * sizeof(uint)), BindFlag.IndexBuffer);

        _cb = GpuBuffers.CreateConstant<ZoneConstants>(gpu);

        // Alpha blending (over).
        var bd = new BlendDesc();
        bd.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = Blend.SrcAlpha,
            DestBlend = Blend.InvSrcAlpha,
            BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One,
            DestBlendAlpha = Blend.Zero,
            BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ID3D11BlendState* blend = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateBlendState(in bd, ref blend));
        _blend = blend;

        // Overlay: no depth-test and no depth write.
        var dsd = new DepthStencilDesc { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.Always };
        ID3D11DepthStencilState* nd = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in dsd, ref nd));
        _noDepth = nd;

        var rsd = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 };
        ID3D11RasterizerState* rs = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rsd, ref rs));
        _raster = rs;
    }

    /// <param name="eye">Where the camera stands: zones fade with their distance from it.</param>
    /// <param name="parallel">The view is a parallel one (Top, Front...): the camera then stands wherever the
    /// projection was framed from, which says nothing about how far a zone is - nothing fades.</param>
    /// <param name="depthRead">The scene's own "test, never write" depth state, so the scene hides what lies
    /// behind it.</param>
    public void Render(ComPtr<ID3D11DeviceContext> ctx, Matrix4x4 viewProj, Vector3 eye, bool parallel,
        ComPtr<ID3D11DepthStencilState> depthRead, IReadOnlyList<ZoneBox> boxes)
    {
        if (boxes == null || boxes.Count == 0) return;

        ctx.OMSetBlendState(_blend, (float*)null, 0xffffffff);
        ctx.OMSetDepthStencilState(depthRead, 0);
        ctx.RSSetState(_raster);
        ctx.IASetInputLayout(_layout);
        ctx.VSSetShader(_vs, (ID3D11ClassInstance**)null, 0);
        ctx.PSSetShader(_ps, (ID3D11ClassInstance**)null, 0);
        var cb = _cb.Handle;
        ctx.VSSetConstantBuffers(0, 1, &cb);
        ctx.PSSetConstantBuffers(0, 1, &cb);
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        uint stride = 12, offset = 0;
        var vb = _vb.Handle;
        ctx.IASetVertexBuffers(0, 1, &vb, &stride, &offset);
        ctx.IASetIndexBuffer(_ib, Format.FormatR32Uint, 0);

        // How much of each zone is left at its distance, and whether the camera stands inside it.
        Span<float> fade = boxes.Count <= 2048 ? stackalloc float[boxes.Count] : new float[boxes.Count];
        Span<bool> inside = boxes.Count <= 2048 ? stackalloc bool[boxes.Count] : new bool[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            ZoneBox box = boxes[i];
            float distance = Vector3.Distance(eye, Vector3.Clamp(eye, box.Min, box.Max));
            inside[i] = !parallel && distance <= 0f;
            fade[i] = box.Picked || parallel ? 1f : Math.Clamp(1f - ((distance - FadeFrom) / (FadeTo - FadeFrom)), 0f, 1f);
        }

        void Draw(ZoneBox box, float alpha, float lighten, uint indices)
        {
            Matrix4x4 world = Matrix4x4.CreateScale(box.Max - box.Min) * Matrix4x4.CreateTranslation(box.Min);
            Vector3 colour = Vector3.Lerp(box.Color, Vector3.One, lighten);
            var c = new ZoneConstants { Wvp = world * viewProj, Color = new Vector4(colour, alpha) };
            GpuBuffers.UpdateConstant(ctx, _cb, ref c);
            ctx.DrawIndexed(indices, 0, 0);
        }

        // Fills: faint, and none for a zone the camera is in - its faces are all round the viewer, and their
        // tint would lie over the whole picture.
        for (int i = 0; i < boxes.Count; i++)
        {
            ZoneBox box = boxes[i];
            if (inside[i] && !box.Picked) continue;
            float alpha = box.Picked ? (inside[i] ? 0.05f : 0.20f) : (box.Loads ? 0.055f : 0.025f) * fade[i];
            if (alpha > 0.004f) Draw(box, alpha, 0f, (uint)CubeIndices.Length);
        }

        // Edges: what tells one zone from the next.
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist);
        ctx.IASetIndexBuffer(_edges, Format.FormatR32Uint, 0);
        for (int i = 0; i < boxes.Count; i++)
        {
            ZoneBox box = boxes[i];
            float alpha = box.Picked ? 1f : (box.Loads ? 0.9f : 0.4f) * fade[i];
            if (alpha > 0.02f) Draw(box, alpha, box.Picked ? 0.55f : 0.25f, (uint)EdgeIndices.Length);
            // A zone that was added: a second, near-white frame a little way out. It fades half as much as the
            // rest - what one has made oneself is what one looks for from afar.
            if (box.Added)
            {
                var grown = box with { Min = box.Min - new Vector3(AddedFrame), Max = box.Max + new Vector3(AddedFrame) };
                Draw(grown, box.Picked ? 1f : 0.5f + (0.5f * fade[i]), 0.85f, (uint)EdgeIndices.Length);
            }
        }

        // The picked zone once more, through whatever stands in front of it.
        ctx.OMSetDepthStencilState(_noDepth, 0);
        for (int i = 0; i < boxes.Count; i++)
        {
            if (boxes[i].Picked) Draw(boxes[i], 0.45f, 0.55f, (uint)EdgeIndices.Length);
        }

        // Restore opaque blending for the next frame.
        ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, 0xffffffff);
    }

    public void Dispose()
    {
        _raster.Dispose();
        _noDepth.Dispose();
        _blend.Dispose();
        _edges.Dispose();
        _ib.Dispose();
        _vb.Dispose();
        _cb.Dispose();
        _layout.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
