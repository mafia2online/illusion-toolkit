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
    }

    private ComPtr<ID3D11Buffer> _buffer;
    private SurfaceMaterial? _last;
    private bool _initialized;
    private readonly nint[] _lastViews = new nint[8];
    public SurfaceBindings(GpuContext gpu) => _buffer = GpuBuffers.CreateConstant<Constants>(gpu);

    public void Bind(ComPtr<ID3D11DeviceContext> context, GpuPart part)
    {
        var buffer = _buffer.Handle; context.PSSetConstantBuffers(2, 1, &buffer);
        bool same = _initialized && Equals(_last, part.Surface);
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
    }

    public void Dispose() => _buffer.Dispose();
}
