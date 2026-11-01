using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Scene;
using Illusion.Rendering.Textures;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

public sealed unsafe partial class NativeWaterSurface
{
    private ComPtr<ID3D11Texture2D> _opaqueColor, _opaqueDepth;
    private ComPtr<ID3D11ShaderResourceView> _opaqueColorView, _opaqueDepthView, _opticsBrdf;
    private int _opticsWidth, _opticsHeight;
    private Vector2 _opticsDepth;
    private bool _opticsDepthValid;
    public bool OpticsConfigured => !_disposed && _opticsDepthValid && _opticsBrdf.Handle != null;
    public bool OpticsActive { get; private set; }
    public bool ReflectionsEnabled { get; set; } = true;
    public bool RefractionEnabled { get; set; } = true;

    /// <summary>Native D261 depth thresholds and user-owned T902 Blinn environment BRDF.
    /// Screen-space radiance comes from this editor's opaque scene, not engine environment captures.</summary>
    public bool ConfigureOptics(byte[]? brdf, Vector2 depthThresholds)
    {
        _opticsBrdf.Dispose(); _opticsBrdf = default; _opticsDepthValid = false;
        if (_disposed || brdf is null || !float.IsFinite(depthThresholds.X) || !float.IsFinite(depthThresholds.Y)
            || depthThresholds.X <= 0 || depthThresholds.Y <= 0) return false;
        _opticsBrdf = DdsTexture.LoadMipmapped(_gpu, brdf);
        _opticsDepth = depthThresholds; _opticsDepthValid = true;
        return OpticsConfigured;
    }

    public bool SetOpticsDepthThresholds(Vector2 depth)
    {
        _opticsDepthValid = float.IsFinite(depth.X) && float.IsFinite(depth.Y) && depth.X > 0 && depth.Y > 0;
        if (!_opticsDepthValid) return false;
        _opticsDepth = depth; return true;
    }

    private void CaptureOptics(SharedRenderTarget target, Camera camera)
    {
        OpticsActive = false;
        if (!OpticsConfigured) return;
        Texture2DDesc source; target.DepthTex.GetDesc(&source);
        if (source.Format != Format.FormatD24UnormS8Uint || source.SampleDesc.Count != 1) return;
        if (_opticsWidth != target.Width || _opticsHeight != target.Height)
        {
            DisposeOpticsTargets();
            try
            {
                Texture2DDesc color; target.Texture11.GetDesc(&color);
                color.Usage = Usage.Default; color.BindFlags = (uint)BindFlag.ShaderResource;
                color.CPUAccessFlags = 0; color.MiscFlags = 0;
                ID3D11Texture2D* texture = null;
                SilkMarshal.ThrowHResult(_gpu.Device11.CreateTexture2D(in color, (SubresourceData*)null, &texture)); _opaqueColor = texture;
                ID3D11ShaderResourceView* view = null;
                SilkMarshal.ThrowHResult(_gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, &view)); _opaqueColorView = view;
                source.Format = Format.FormatR24G8Typeless; source.BindFlags = (uint)BindFlag.ShaderResource;
                source.Usage = Usage.Default; source.CPUAccessFlags = 0; source.MiscFlags = 0;
                texture = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateTexture2D(in source, (SubresourceData*)null, &texture)); _opaqueDepth = texture;
                var sd = new ShaderResourceViewDesc { Format = Format.FormatR24UnormX8Typeless, ViewDimension = D3DSrvDimension.D3DSrvDimensionTexture2D };
                sd.Texture2D.MipLevels = 1;
                view = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, in sd, &view)); _opaqueDepthView = view;
                _opticsWidth = target.Width; _opticsHeight = target.Height;
            }
            catch { DisposeOpticsTargets(); throw; }
        }
        var ctx = _gpu.Context11;
        ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        ctx.CopyResource((ID3D11Resource*)_opaqueColor.Handle, (ID3D11Resource*)target.Texture11.Handle);
        ctx.CopyResource((ID3D11Resource*)_opaqueDepth.Handle, (ID3D11Resource*)target.DepthTex.Handle);
        SetVector(224, new(camera.Near, camera.Far, _opticsDepth.X, _opticsDepth.Y));
        SetVector(228, new(1, ReflectionsEnabled ? 1 : 0, RefractionEnabled ? 1 : 0, 0));
        SetVector(232, new(camera.Forward, 0));
        OpticsActive = true;
    }

    private void DisposeOpticsTargets()
    {
        _opaqueColorView.Dispose(); _opaqueDepthView.Dispose(); _opaqueColor.Dispose(); _opaqueDepth.Dispose();
        _opaqueColorView = default; _opaqueDepthView = default; _opaqueColor = default; _opaqueDepth = default;
        _opticsWidth = _opticsHeight = 0;
    }
    private void DisposeOptics() { DisposeOpticsTargets(); _opticsBrdf.Dispose(); _opticsBrdf = default; }
}
