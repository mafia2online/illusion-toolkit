using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Shaders;

internal sealed unsafe class SurfaceBindings : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Mode;
        public Vector4 Scales;
        public Vector4 Scales2;
        public Vector4 Masks;
        public Vector4 PaintPrimary;
        public Vector4 PaintSecondary;
        public Vector4 DirtA0;
        public Vector4 DirtA1;
        public Vector4 DirtB0;
        public Vector4 DirtB1;
        public Vector4 DirtControls;
        public Vector4 DirtNormalControls;
    }

    private ComPtr<ID3D11Buffer> _buffer;
    private SurfaceMaterial? _last;
    private bool _initialized;
    private bool _lastHasDecalUvs;
    private readonly nint[] _lastViews = new nint[8];
    public SurfaceBindings(GpuContext gpu) => _buffer = GpuBuffers.CreateConstant<Constants>(gpu);
    public void Invalidate() => _initialized = false;

    public void Bind(ComPtr<ID3D11DeviceContext> context, GpuPart part, bool hasDecalUvs)
    {
        var buffer = _buffer.Handle; context.PSSetConstantBuffers(2, 1, &buffer);
        bool same = _initialized && Equals(_last, part.Surface) && _lastHasDecalUvs == hasDecalUvs;
        _lastHasDecalUvs = hasDecalUvs;
        for (int i = 0; i < 8; i++)
        {
            nint view = part.SurfaceSrvs is { } views ? (nint)views[i].Handle : 0;
            same &= view == _lastViews[i]; _lastViews[i] = view;
        }
        if (same) return;
        _initialized = true; _last = part.Surface;
        var values = new Constants();
        if (part.Surface is { } surface)
        {
            values.Mode.X = surface.Layers;
            values.Scales = new(surface.BaseScale, surface.Layer2Scale.X, surface.Layer2Scale.Y);
            values.Scales2 = new(surface.Layer3Scale, 0, 0);
            values.Masks = new(surface.MaskScale, surface.Mask2Scale.X, surface.Mask2Scale.Y);
            values.PaintPrimary = surface.PaintPrimary;
            values.PaintSecondary = surface.PaintSecondary;
            if (hasDecalUvs && surface.Layers < -.5f && surface.VehicleDecal is { } decal)
            { values.Mode.Z = 1; values.Mode.W = decal.Opacity; }
            if (surface.VehicleDirt is { } dirt && surface.Layers < -.5f)
            {
                values.Mode.Y = 1;
                values.DirtA0 = new(dirt.PaletteA0, 0); values.DirtA1 = new(dirt.PaletteA1, 0);
                values.DirtB0 = new(dirt.PaletteB0, 0); values.DirtB1 = new(dirt.PaletteB1, 0);
                values.DirtControls = new(dirt.Amount, dirt.Smoothness.X, dirt.Smoothness.Y);
                if (dirt.Normal is { } normal)
                {
                    values.DirtA0.W = 1;
                    values.DirtNormalControls = new(normal.UvScale, normal.ThresholdOffset, normal.NativeAmountZ, normal.NativeSmoothnessZ);
                }
            }
        }
        GpuBuffers.UpdateConstant(context, _buffer, ref values);
        var textures = stackalloc ID3D11ShaderResourceView*[8];
        for (int i = 0; i < 8; i++) textures[i] = part.SurfaceSrvs is { } srvs ? srvs[i].Handle : null;
        context.PSSetShaderResources(3, 8, textures);
    }

    public static void BindColors(ComPtr<ID3D11DeviceContext> context, GpuMesh mesh)
    {
        var buffer = mesh.ColorBuffer.Handle; uint stride = 16, offset = 0;
        context.IASetVertexBuffers(2, 1, &buffer, &stride, &offset);
        var decal = mesh.DecalUvBuffer.Handle; stride = 8;
        context.IASetVertexBuffers(3, 1, &decal, &stride, &offset);
        var secondary = mesh.SecondaryColorBuffer.Handle; stride = 16;
        context.IASetVertexBuffers(4, 1, &secondary, &stride, &offset);
    }

    public void Dispose() => _buffer.Dispose();
}
